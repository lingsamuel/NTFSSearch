namespace NtfsSearch.Core;

internal static class GenerationGarbageCollector
{
    internal static void Clean(string directory, NtfsVolume volume, IReadOnlySet<string> keep)
    {
        string key = VolumeCache.Key(volume);
        foreach (string path in Directory.EnumerateDirectories(directory, key + ".*.ntidx.changes"))
        {
            string snapshot = Path.GetFileName(path)[..^8];
            if (keep.Contains(snapshot) || !IsOwned(snapshot, key)) continue;
            try
            {
                // 锁在代目录外；所有日志读/写者持共享租约，确认退役后才能整体回收。
                using var lease = new FileStream(Path.Combine(directory, snapshot + ".generation.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                Directory.Delete(path, true);
                foreach (string file in Directory.EnumerateFiles(directory, snapshot + ".live-*.state*")) File.Delete(file);
                foreach (string segments in Directory.EnumerateDirectories(directory, snapshot + ".live-*.state.segments")) Directory.Delete(segments, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
    private static bool IsOwned(string snapshot, string key) => snapshot.StartsWith(key + ".", StringComparison.Ordinal)
        && snapshot.EndsWith(".ntidx", StringComparison.Ordinal)
        && Guid.TryParseExact(snapshot[(key.Length + 1)..^6], "N", out _);
}
