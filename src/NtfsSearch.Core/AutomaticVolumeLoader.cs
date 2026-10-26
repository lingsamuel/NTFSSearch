namespace NtfsSearch.Core;

public interface IAutomaticVolumeBackend
{
    bool CollectorNeedsRestart => false;
    void RequestRebuild(NtfsVolume volume) { }
    IReadOnlyList<NtfsVolume> Discover();
    string CacheRevision(NtfsVolume volume);
    Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token);
    Task EnsureCollectorAsync(CancellationToken token);
    string ReadStatus(NtfsVolume volume);
}

public sealed record VolumeStatus(string Root, string Message);

public sealed class AutomaticVolumeLoader(IAutomaticVolumeBackend backend, SearchCatalog catalog)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, string> attempted = new(StringComparer.Ordinal);
    private VolumeStatus[] statuses = [];
    private bool collectorAttempted;
    public IReadOnlyList<VolumeStatus> Statuses => Volatile.Read(ref statuses);
    public event Action? StatusChanged;

    public async Task RefreshAsync(CancellationToken token = default, bool retryCollector = false)
    {
        await gate.WaitAsync(token);
        try
        {
            var volumes = await Task.Run(backend.Discover, token);
            catalog.RemoveExcept(volumes.Select(VolumeCache.Key));
            foreach (string key in attempted.Keys.Except(volumes.Select(VolumeCache.Key)).ToArray()) attempted.Remove(key);
            var next = new List<VolumeStatus>();
            foreach (var volume in volumes)
            {
                token.ThrowIfCancellationRequested();
                string key = VolumeCache.Key(volume), revision = backend.CacheRevision(volume);
                string message = backend.ReadStatus(volume);
                if (!attempted.TryGetValue(key, out string? previous) || previous != revision)
                {
                    // 先记录已尝试的版本；损坏主库回退后也不会每两秒反复加载同一损坏版本。
                    attempted[key] = revision;
                    try
                    {
                        if (await backend.LoadAsync(volume, token) is { } loaded)
                        {
                            catalog.Publish(key, loaded.Index);
                            if (loaded.UsedBackup) backend.RequestRebuild(volume);
                            message = loaded.UsedBackup ? "缓存损坏，已自动回退上一代，等待重建" : $"已加载 {loaded.Index.Count:N0} 个对象";
                        }
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    { message = e.Message; if (e is InvalidDataException) backend.RequestRebuild(volume); }
                }
                else if (Statuses.FirstOrDefault(s => s.Root == volume.Root) is { } old && message == "等待后台采集") message = old.Message;
                next.Add(new(volume.Root, message));
                Volatile.Write(ref statuses, next.ToArray()); StatusChanged?.Invoke();
            }
            Volatile.Write(ref statuses, next.ToArray()); StatusChanged?.Invoke();
            if (!collectorAttempted || retryCollector || backend.CollectorNeedsRestart)
            {
                collectorAttempted = true;
                try { await backend.EnsureCollectorAsync(token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    Volatile.Write(ref statuses, volumes.Select(v => new VolumeStatus(v.Root, e.Message)).ToArray());
                    StatusChanged?.Invoke();
                }
            }
        }
        finally { gate.Release(); }
    }

    public async Task RunAsync(CancellationToken token)
    {
        await RefreshAsync(token);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(token)) await RefreshAsync(token);
    }
}
