namespace NtfsSearch.Core;

internal static class SnapshotFormatUpgrade
{
    internal static bool IsEligible(CachedVolume loaded) => !loaded.UsedBackup && loaded.Manifest?.Journal is not null
        && loaded.Index.HasIdentity && ReferenceEquals(loaded.Index.IdentitySource, loaded.Index)
        && loaded.Index.HiddenRows is null && !loaded.Index.HasOrderCorrections;

    internal static bool Run(VolumeCache cache, NtfsVolume volume, CachedVolume loaded, CancellationToken token)
    {
        if (!IsEligible(loaded)) return false;
        using var lease = cache.AcquireLease(volume, exclusive: true, token);
        // 读者完成Attach后可能已有另一代发布；必须在独占租约内重读主清单，不能借前代回退确认身份。
        var current = VolumeCache.ReadManifest(volume, cache.ManifestPath(volume));
        if (current is null || current != loaded.Manifest) return false;
        foreach (string stale in Directory.EnumerateFiles(cache.DirectoryPath, current.SnapshotFile + ".format-*.tmp"))
            try { File.Delete(stale); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string snapshot = Path.Combine(cache.DirectoryPath, current.SnapshotFile);
        if (!loaded.UsedSnapshotBackup && SnapshotStore.IsCurrentFormat(snapshot, token)) return false;
        string candidate = snapshot + ".format-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // 只保存Load返回的原始基线；已尾读的层和路径覆盖不能混入基线，否则旧检查点身份会改变。
            SnapshotStore.Save(loaded.Index, candidate, token);
            SnapshotStore.ValidateChecksum(candidate, token);
            token.ThrowIfCancellationRequested();
            // 主文件若已损坏，不能让Replace把坏编码覆盖唯一健康的同代备份。
            AtomicFile.Publish(candidate, snapshot, loaded.UsedSnapshotBackup ? null : snapshot + ".bak", token);
            return true;
        }
        finally
        {
            try { File.Delete(candidate); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
