using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NtfsSearch.Core;

public sealed record VolumeManifest(string VolumeId, string Root, string SnapshotFile, DateTime IndexedUtc, JournalPosition? Journal = null);
public sealed record CachedVolume(SearchIndex Index, bool UsedBackup, VolumeManifest? Manifest = null, IReadOnlyList<SearchIndex>? Layers = null, bool UsedSnapshotBackup = false);

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
        using var publication = BeginPublication(volume, index, null, token);
        publication.Commit(token); return publication.Manifest;
    }

    public PreparedVolumePublication BeginPublication(NtfsVolume volume, SearchIndex index,
        JournalPosition? journal, CancellationToken token = default)
    {
        var lease = AcquireLease(volume, exclusive: true, token);
        string snapshot = Key(volume) + "." + Guid.NewGuid().ToString("N") + ".ntidx";
        try
        {
            foreach (string pattern in new[] { Key(volume) + ".*.ntidx.*.tmp", Key(volume) + ".ready.json.*.tmp" })
                foreach (string temporary in Directory.EnumerateFiles(DirectoryPath, pattern))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            SnapshotStore.Save(index, Path.Combine(DirectoryPath, snapshot), token);
            return new(this, volume, new(volume.Id, volume.Root, snapshot, DateTime.UtcNow, journal), lease);
        }
        catch { lease.Dispose(); throw; }
    }

    private void Commit(NtfsVolume volume, VolumeManifest manifest, CancellationToken token)
    {
        AtomicJson.Write(ManifestPath(volume), manifest, keepBackup: true, token);
    }

    private void Cleanup(NtfsVolume volume, VolumeManifest manifest)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { manifest.SnapshotFile };
        try { if (ReadManifest(volume, ManifestPath(volume) + ".bak") is { } old) keep.Add(old.SnapshotFile); }
        catch (Exception e) when (e is InvalidDataException or IOException or JsonException) { }
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, Key(volume) + ".*.ntidx"))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        foreach (string backup in Directory.EnumerateFiles(DirectoryPath, Key(volume) + ".*.ntidx.bak"))
        {
            if (keep.Contains(Path.GetFileName(backup)[..^4])) continue;
            try { File.Delete(backup); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        GenerationGarbageCollector.Clean(DirectoryPath, volume, keep);
    }

    public void CleanupRetired(NtfsVolume volume, CancellationToken token = default)
    {
        using var lease = AcquireLease(volume, exclusive: true, token);
        if (CurrentManifest(volume) is { } current) Cleanup(volume, current);
    }

    public VolumeManifest? CurrentManifest(NtfsVolume volume)
    {
        foreach (string suffix in new[] { "", ".bak" })
        {
            try { if (ReadManifest(volume, ManifestPath(volume) + suffix) is { } manifest) return manifest; }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public sealed class PreparedVolumePublication : IDisposable
    {
        private readonly VolumeCache cache;
        private readonly NtfsVolume volume;
        private readonly FileStream lease;
        private bool committed, disposed;
        public VolumeManifest Manifest { get; private set; }
        internal PreparedVolumePublication(VolumeCache cache, NtfsVolume volume, VolumeManifest manifest, FileStream lease)
        { this.cache = cache; this.volume = volume; Manifest = manifest; this.lease = lease; }
        public void Commit(CancellationToken token = default)
        {
            ObjectDisposedException.ThrowIf(disposed, this); token.ThrowIfCancellationRequested();
            if (committed) return;
            Manifest = Manifest with { IndexedUtc = DateTime.UtcNow };
            cache.Commit(volume, Manifest, token); committed = true;
            try { cache.Cleanup(volume, Manifest); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                if (!committed)
                    try { File.Delete(Path.Combine(cache.DirectoryPath, Manifest.SnapshotFile)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            finally { lease.Dispose(); }
        }
    }

    public CachedVolume? Load(NtfsVolume volume, CancellationToken token = default, IProgress<string>? progress = null)
    {
        using var lease = AcquireLease(volume, exclusive: false, token);
        bool found = false;
        foreach (bool backup in new[] { false, true })
        {
            string path = ManifestPath(volume) + (backup ? ".bak" : "");
            try
            {
                var manifest = ReadManifest(volume, path);
                if (manifest is null) continue;
                found = true;
                string snapshot = Path.Combine(DirectoryPath, manifest.SnapshotFile);
                try { return new(SnapshotStore.Load(snapshot, token, progress), backup, manifest); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    // 同一基线的格式备份保持原日志身份；它不属于上一代清单回退，不应触发MFT重建。
                    return new(SnapshotStore.Load(snapshot + ".bak", token, progress), backup, manifest, UsedSnapshotBackup: true);
                }
            }
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException) { found = true; }
        }
        if (found) throw new InvalidDataException("磁盘缓存损坏，后台将自动重建；其他磁盘仍可查询。");
        return null;
    }

    public bool TryUpgradeSnapshot(NtfsVolume volume, CachedVolume loaded, CancellationToken token = default)
    {
        try { return SnapshotFormatUpgrade.Run(this, volume, loaded, token); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException
            or JsonException or OperationCanceledException)
        {
            // 只是已恢复基线的存储优化；磁盘满、锁冲突和取消均不能撤销可查询结果或请求重建。
            return false;
        }
    }

    internal static VolumeManifest? ReadManifest(NtfsVolume volume, string path)
    {
        if (!AtomicFile.ExistsWithReplaceGrace(path)) return null;
        var manifest = JsonSerializer.Deserialize<VolumeManifest>(AtomicJson.ReadText(path, 65536)) ?? throw new InvalidDataException("索引清单为空。");
        if (manifest.VolumeId != volume.Id || manifest.Root != volume.Root) throw new InvalidDataException("卷身份与缓存不符。");
        if (string.IsNullOrEmpty(manifest.SnapshotFile) || manifest.SnapshotFile != Path.GetFileName(manifest.SnapshotFile)
            || !manifest.SnapshotFile.StartsWith(Key(volume) + ".", StringComparison.Ordinal)
            || !manifest.SnapshotFile.EndsWith(".ntidx", StringComparison.Ordinal)) throw new InvalidDataException("缓存文件引用无效。");
        return manifest;
    }

    internal FileStream AcquireLease(NtfsVolume volume, bool exclusive, CancellationToken token)
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
    public static string ReadText(string path, CancellationToken token = default) => ReadText(path, int.MaxValue, token);

    internal static string ReadText(string path, int maximumBytes, CancellationToken token = default)
    {
        // Windows 原子替换要求读者允许删除共享；句柄仍会读到其打开的完整旧版本。
        string? text = null;
        AtomicFile.Retry(() =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > maximumBytes) throw new InvalidDataException("索引清单过大。");
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }, OperatingSystem.IsWindows(), token, allowMissing: true);
        return text!;
    }

    public static void Write<T>(string path, T value, bool keepBackup = false, CancellationToken token = default)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, value); stream.Flush(true); }
            AtomicFile.Publish(temporary, path, keepBackup ? path + ".bak" : null, token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
