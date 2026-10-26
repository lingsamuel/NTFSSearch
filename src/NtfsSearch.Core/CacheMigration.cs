using System.Text.Json;

namespace NtfsSearch.Core;

public enum CacheMigrationStatus { Migrated, KeptDestination, Failed }
public sealed record CacheMigrationVolumeResult(string VolumeKey, string? Root, CacheMigrationStatus Status, string Message);
public sealed record CacheMigrationResult(IReadOnlyList<CacheMigrationVolumeResult> Volumes, bool SourceInUse, bool CanCleanSource)
{
    public bool FullValidationCompleted { get; init; }
}

/// <summary>逐卷迁移旧缓存。源始终保留，清理资格只表示本次检查时可进一步执行受控清理。</summary>
public static class CacheMigration
{
    public static Task<CacheMigrationResult> MigrateAsync(string sourceDirectory, string destinationDirectory,
        CancellationToken token = default, bool fullValidate = false) =>
        Task.Run(() => Migrate(sourceDirectory, destinationDirectory, token, fullValidate), token);

    public static CacheMigrationResult Migrate(string sourceDirectory, string destinationDirectory,
        CancellationToken token = default, bool fullValidate = false)
    {
        token.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(sourceDirectory), target = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(source)) return new([], false, false);
        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return new([], false, false);
        RejectLink(source);
        Directory.CreateDirectory(target); RejectLink(target);
        using var ownership = TryCollectorLease(source);
        bool sourceInUse = ownership is null;
        var results = new List<CacheMigrationVolumeResult>();
        bool complete = true;
        var sourceCache = new VolumeCache(source); var destinationCache = new VolumeCache(target);
        string[] keys = Directory.EnumerateFiles(source, "*.ready.json*")
            .Select(Path.GetFileName).OfType<string>().Where(n => n.EndsWith(".ready.json", StringComparison.Ordinal)
                || n.EndsWith(".ready.json.bak", StringComparison.Ordinal))
            .Select(n => n[..n.IndexOf(".ready.json", StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).ToArray();
        foreach (string key in keys)
        {
            token.ThrowIfCancellationRequested();
            NtfsVolume? volume = null;
            string? staging = null;
            try
            {
                if (key.Length != 24 || key.Any(c => !char.IsAsciiHexDigitUpper(c)))
                    throw new InvalidDataException("旧缓存清单名称不属于本应用。");
                volume = ReadIdentity(source, key);
                using var sourceLease = sourceCache.AcquireLease(volume, exclusive: false, token);
                using var destinationLease = destinationCache.AcquireLease(volume, exclusive: true, token);
                // 每卷共用发布锁，避免迁移用旧清单覆盖并发采集刚发布的新代。
                var sourceGenerations = ReadGenerations(sourceCache, volume, token, fullValidate, out bool allValid);
                complete &= allValid;
                if (HasValidGeneration(destinationCache, volume, token, fullValidate))
                {
                    results.Add(new(key, volume.Root, CacheMigrationStatus.KeptDestination, "目标已有有效缓存，已保留。"));
                    continue;
                }
                // 实时代必须连同WAL及消费检查点保持完整代际；旧版搬迁只复制静态快照，不能伪称已迁移变化。
                if (sourceGenerations.Any(manifest => manifest.Journal is not null))
                    throw new InvalidDataException("自动迁移仅支持不含实时日志的旧版缓存；实时代已保留，请将原有 Data 随程序一起移动。");
                if (sourceGenerations.Count == 0) throw new InvalidDataException("旧缓存没有完整、校验通过的快照。");
                staging = Path.Combine(target, "." + key + ".migration-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(staging);
                var stagedManifests = new List<VolumeManifest>();
                foreach (var manifest in sourceGenerations)
                {
                    token.ThrowIfCancellationRequested();
                    string snapshot = key + "." + Guid.NewGuid().ToString("N") + ".ntidx";
                    CopySnapshot(Path.Combine(source, manifest.SnapshotFile), Path.Combine(staging, snapshot), token);
                    // 备份快照只跟随其完整清单迁移，不复制孤儿文件或临时产物。
                    string sourceBackup = Path.Combine(source, manifest.SnapshotFile + ".bak");
                    if (File.Exists(sourceBackup))
                    {
                        try
                        {
                            RejectLink(sourceBackup);
                            ValidateSnapshot(sourceBackup, token, fullValidate);
                            CopySnapshot(sourceBackup, Path.Combine(staging, snapshot + ".bak"), token);
                        }
                        catch (Exception e) when (IsCacheError(e))
                        {
                            complete = false;
                            File.Delete(Path.Combine(staging, snapshot + ".bak"));
                        }
                    }
                    stagedManifests.Add(manifest with { SnapshotFile = snapshot });
                }
                token.ThrowIfCancellationRequested();
                // 所有代先复制并验证，最后才发布清单；任何清单永远只指向完整快照。
                foreach (string snapshot in Directory.EnumerateFiles(staging))
                    File.Move(snapshot, Path.Combine(target, Path.GetFileName(snapshot)));
                string manifestPath = destinationCache.ManifestPath(volume);
                if (stagedManifests.Count > 1) AtomicJson.Write(manifestPath + ".bak", stagedManifests[1]);
                AtomicJson.Write(manifestPath, stagedManifests[0]);
                results.Add(new(key, volume.Root, CacheMigrationStatus.Migrated,
                    allValid ? "旧缓存已迁移并校验；源缓存暂时保留。" : "已迁移完整的可用代；损坏的源缓存已保留。"));
            }
            catch (Exception error) when (IsCacheError(error))
            {
                complete = false;
                results.Add(new(key, volume?.Root, CacheMigrationStatus.Failed, "迁移未完成，可重试：" + error.Message));
            }
            finally
            {
                if (staging is not null)
                    try { Directory.Delete(staging, recursive: true); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            }
        }
        bool fullyValidated = fullValidate && complete && results.Count > 0;
        return new(results, sourceInUse, !sourceInUse && fullyValidated) { FullValidationCompleted = fullyValidated };
    }

    private static NtfsVolume ReadIdentity(string directory, string key)
    {
        foreach (string suffix in new[] { ".ready.json", ".ready.json.bak" })
        {
            try
            {
                string path = Path.Combine(directory, key + suffix); RejectLink(path);
                var manifest = JsonSerializer.Deserialize<VolumeManifest>(AtomicJson.ReadText(path, 65536));
                if (manifest is null || string.IsNullOrEmpty(manifest.VolumeId) || string.IsNullOrEmpty(manifest.Root)) continue;
                var volume = new NtfsVolume(manifest.VolumeId, manifest.Root, 0, "");
                if (VolumeCache.Key(volume) == key) return volume;
            }
            catch (Exception e) when (IsCacheError(e)) { }
        }
        throw new InvalidDataException("旧缓存清单的卷身份无效。");
    }

    private static List<VolumeManifest> ReadGenerations(VolumeCache cache, NtfsVolume volume,
        CancellationToken token, bool fullValidate, out bool allValid)
    {
        allValid = true;
        var generations = new List<VolumeManifest>();
        foreach (string suffix in new[] { "", ".bak" })
        {
            string path = cache.ManifestPath(volume) + suffix;
            if (!File.Exists(path)) continue;
            try
            {
                RejectLink(path);
                var manifest = VolumeCache.ReadManifest(volume, path)!;
                ValidateSnapshotName(volume, manifest.SnapshotFile);
                string snapshot = Path.Combine(cache.DirectoryPath, manifest.SnapshotFile);
                RejectLink(snapshot);
                ValidateSnapshot(snapshot, token, fullValidate);
                generations.Add(manifest);
            }
            catch (Exception e) when (IsCacheError(e)) { allValid = false; }
        }
        return generations;
    }

    private static bool HasValidGeneration(VolumeCache cache, NtfsVolume volume, CancellationToken token, bool fullValidate) =>
        ReadGenerations(cache, volume, token, fullValidate, out _).Count > 0;

    private static void ValidateSnapshot(string path, CancellationToken token, bool fullValidate)
    {
        // 默认只用固定缓冲区验证SHA和格式版本，完整记录/排序校验由前台正常加载完成。
        // 显式完整校验才加载单卷，并立即丢弃结果；默认迁移永远不授予源清理资格。
        if (fullValidate) _ = SnapshotStore.Load(path, token);
        else SnapshotStore.ValidateChecksum(path, token);
    }

    private static void ValidateSnapshotName(NtfsVolume volume, string name)
    {
        string prefix = VolumeCache.Key(volume) + ".";
        if (name.Length != prefix.Length + 32 + 6 || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(".ntidx", StringComparison.Ordinal)
            || !Guid.TryParseExact(name.AsSpan(prefix.Length, 32), "N", out _))
            throw new InvalidDataException("快照文件名不属于本应用的完整代。");
    }

    private static void CopySnapshot(string source, string destination, CancellationToken token)
    {
        RejectLink(source);
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
        {
            var buffer = new byte[1 << 20];
            int read;
            while ((read = input.Read(buffer)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
            output.Flush(flushToDisk: true);
        }
        SnapshotStore.ValidateChecksum(destination, token);
    }

    private static FileStream? TryCollectorLease(string directory)
    {
        string path = Path.Combine(directory, "collector.lock");
        try { RejectLink(path); return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void RejectLink(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("迁移不跟随符号链接或重解析点。");
    }

    private static bool IsCacheError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
