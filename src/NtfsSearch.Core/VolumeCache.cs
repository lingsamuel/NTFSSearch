using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NtfsSearch.Core;

public sealed record VolumeManifest(string VolumeId, string Root, string SnapshotFile, DateTime IndexedUtc);
public sealed record CachedVolume(SearchIndex Index, bool UsedBackup);

public sealed class VolumeCache
{
    public string DirectoryPath { get; }
    public VolumeCache(string directory) { DirectoryPath = Path.GetFullPath(directory); Directory.CreateDirectory(DirectoryPath); }

    public static string Key(NtfsVolume volume) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(volume.Id + "|" + volume.Root)))[..24];
    public string ManifestPath(NtfsVolume volume) => Path.Combine(DirectoryPath, Key(volume) + ".ready.json");
    public long CachedSnapshotBytes(NtfsVolume volume)
    {
        try
        {
            var manifest = ReadManifest(volume, ManifestPath(volume));
            if (manifest is null) return 0;
            var info = new FileInfo(Path.Combine(DirectoryPath, manifest.SnapshotFile));
            return info.Exists ? info.Length : 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { return 0; }
    }
    public string Revision(NtfsVolume volume)
    {
        string Stamp(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? info.LastWriteTimeUtc.Ticks + ":" + info.Length : "missing";
        }
        return Stamp(ManifestPath(volume)) + "|" + Stamp(ManifestPath(volume) + ".bak");
    }

    public VolumeManifest Publish(NtfsVolume volume, SearchIndex index, CancellationToken token = default)
    {
        using var lease = AcquireLease(volume, exclusive: true, token);
        foreach (string pattern in new[] { Key(volume) + ".*.ntidx.*.tmp", Key(volume) + ".ready.json.*.tmp" })
            foreach (string temporary in Directory.EnumerateFiles(DirectoryPath, pattern))
                try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string snapshot = Key(volume) + "." + Guid.NewGuid().ToString("N") + ".ntidx";
        SnapshotStore.Save(index, Path.Combine(DirectoryPath, snapshot), token);
        var manifest = new VolumeManifest(volume.Id, volume.Root, snapshot, DateTime.UtcNow);
        AtomicJson.Write(ManifestPath(volume), manifest, keepBackup: true);
        // 发布成功后仅回收本卷旧代，保留主清单和备份清单引用的快照。
        var keep = new HashSet<string>(StringComparer.Ordinal) { snapshot };
        try { if (ReadManifest(volume, ManifestPath(volume) + ".bak") is { } old) keep.Add(old.SnapshotFile); }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException) { }
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, Key(volume) + ".*.ntidx"))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return manifest;
    }

    public CachedVolume? Load(NtfsVolume volume, CancellationToken token = default, IProgress<string>? progress = null)
    {
        using var lease = AcquireLease(volume, exclusive: false, token);
        bool found = false;
        foreach (bool backup in new[] { false, true })
        {
            string path = ManifestPath(volume) + (backup ? ".bak" : "");
            if (!File.Exists(path)) continue;
            found = true;
            try
            {
                var manifest = ReadManifest(volume, path);
                if (manifest is null) continue;
                return new(SnapshotStore.Load(Path.Combine(DirectoryPath, manifest.SnapshotFile), token, progress), backup);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { }
        }
        if (found) throw new InvalidDataException("磁盘缓存损坏，后台将自动重建；其他磁盘仍可查询。");
        return null;
    }

    private static VolumeManifest? ReadManifest(NtfsVolume volume, string path)
    {
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("索引清单过大。");
        var manifest = JsonSerializer.Deserialize<VolumeManifest>(AtomicJson.ReadText(path)) ?? throw new InvalidDataException("索引清单为空。");
        if (manifest.VolumeId != volume.Id || manifest.Root != volume.Root) throw new InvalidDataException("卷身份与缓存不符。");
        if (string.IsNullOrEmpty(manifest.SnapshotFile) || manifest.SnapshotFile != Path.GetFileName(manifest.SnapshotFile)
            || !manifest.SnapshotFile.StartsWith(Key(volume) + ".", StringComparison.Ordinal)
            || !manifest.SnapshotFile.EndsWith(".ntidx", StringComparison.Ordinal)) throw new InvalidDataException("缓存文件引用无效。");
        return manifest;
    }

    private FileStream AcquireLease(NtfsVolume volume, bool exclusive, CancellationToken token)
    {
        string path = Path.Combine(DirectoryPath, Key(volume) + ".publish.lock");
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33)
            {
                if (started.Elapsed > TimeSpan.FromMinutes(2)) throw new IOException("等待本卷缓存发布超时，其他索引仍可查询。", error);
                Thread.Sleep(50);
            }
        }
    }
}

public static class AtomicJson
{
    public static string ReadText(string path)
    {
        // Windows 原子替换要求读者允许删除共享；句柄仍会读到其打开的完整旧版本。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static void Write<T>(string path, T value, bool keepBackup = false)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            if (keepBackup && File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
