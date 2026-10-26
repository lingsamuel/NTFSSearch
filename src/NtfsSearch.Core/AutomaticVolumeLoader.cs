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

public sealed class AutomaticVolumeLoader(IAutomaticVolumeBackend backend, SearchCatalog catalog, int maxConcurrentLoads = 2)
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
            if (maxConcurrentLoads < 1) throw new InvalidOperationException("缓存加载并发必须为正数。");
            var oldStatuses = Statuses.ToDictionary(s => s.Root, StringComparer.Ordinal);
            var next = new VolumeStatus[volumes.Count];
            var visible = new bool[volumes.Count];
            var pending = new List<(NtfsVolume Volume, int Position)>();
            for (int position = 0; position < volumes.Count; position++)
            {
                var volume = volumes[position];
                token.ThrowIfCancellationRequested();
                string key = VolumeCache.Key(volume), revision = backend.CacheRevision(volume);
                string message = backend.ReadStatus(volume);
                visible[position] = catalog.Contains(key);
                if (!attempted.TryGetValue(key, out string? previous) || previous != revision)
                {
                    // 先记录已尝试的版本；损坏主库回退后也不会每两秒反复加载同一损坏版本。
                    attempted[key] = revision;
                    pending.Add((volume, position));
                }
                else if (oldStatuses.TryGetValue(volume.Root, out var old) && message == "等待后台采集") message = old.Message;
                next[position] = new(volume.Root, message);
            }
            Volatile.Write(ref statuses, next.ToArray()); StatusChanged?.Invoke();
            var statusGate = new object();
            void UpdateStatus(int position, string message, bool loaded = false)
            {
                lock (statusGate)
                {
                    visible[position] |= loaded;
                    next[position] = new(volumes[position].Root, message);
                    Volatile.Write(ref statuses, next.ToArray());
                }
                StatusChanged?.Invoke();
            }
            async Task EnsureCollector()
            {
                if (collectorAttempted && !retryCollector && !backend.CollectorNeedsRestart) return;
                collectorAttempted = true;
                try { await backend.EnsureCollectorAsync(token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    lock (statusGate)
                    {
                        for (int i = 0; i < next.Length; i++) if (!visible[i]) next[i] = new(volumes[i].Root, e.Message);
                        Volatile.Write(ref statuses, next.ToArray());
                    }
                    StatusChanged?.Invoke();
                }
            }
            string RequestRebuild(NtfsVolume volume)
            {
                try { backend.RequestRebuild(volume); return ""; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { return "；无法写入重建请求：" + error.Message; }
            }
            // 采集启动与已有缓存恢复独立推进，完成的卷立即发布，不等慢卷或整批结束。
            Task collector = EnsureCollector();
            Task loading = Parallel.ForEachAsync(pending,
                new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = maxConcurrentLoads }, async (work, cancellation) =>
                {
                    try
                    {
                        if (await backend.LoadAsync(work.Volume, cancellation) is not { } loaded) return;
                        catalog.Publish(VolumeCache.Key(work.Volume), loaded.Index);
                        string repairError = loaded.UsedBackup ? RequestRebuild(work.Volume) : "";
                        UpdateStatus(work.Position, loaded.UsedBackup ? "缓存损坏，已自动回退上一代，等待重建" + repairError : $"已加载 {loaded.Index.Count:N0} 个对象", loaded: true);
                    }
                    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
                    {
                        string repairError = e is InvalidDataException ? RequestRebuild(work.Volume) : "";
                        UpdateStatus(work.Position, e.Message + repairError);
                    }
                });
            await Task.WhenAll(collector, loading);
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
