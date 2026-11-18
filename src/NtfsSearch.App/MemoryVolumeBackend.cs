using System.ComponentModel;
using System.Globalization;
using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed partial class MemoryVolumeBackend(SearchCatalog catalog, IEnumerable<ExitSnapshotVolume> startup,
    string? cacheDirectory = null)
{
    private readonly object gate = new();
    private readonly Dictionary<string, VolumeState> volumes = new(StringComparer.Ordinal);
    private readonly string dataDirectory = Path.GetFullPath(cacheDirectory ?? PortableDataDirectory.Resolve());
    private readonly HashSet<string> scanned = new(StringComparer.Ordinal);
    private readonly string session = Guid.NewGuid().ToString("N");
    private readonly IndexingConcurrencyPolicy policy =
        IndexingConcurrencyPolicy.Create(Environment.ProcessorCount, VolumeResources.AvailableMemoryBytes());
    private bool discovered, restored, suspended;
    private int revision;
    private int peakScans;
    private int launchReset;
    private string? collectorError;
    private MemoryCollectorConnection? collector;

    internal event Action? StatusChanged;
    internal event Action<string, bool>? Progress;

    internal IReadOnlyList<VolumeStatus> Statuses
    {
        get
        {
            lock (gate) return volumes.Values.Select(v => collectorError is { } error
                ? new VolumeStatus(v.Volume.Root, error) { HasError = true }
                : v.Status).ToArray();
        }
    }

    internal CollectorMetrics? Metrics
    {
        get
        {
            lock (gate)
            {
                var active = volumes.Values.Where(v => v.ScanPid is not null).ToArray();
                return new(session, policy.ScanLimit, policy.WorkerProcessors, policy.MemoryBudget, active.Select(v => v.Volume.Root).ToArray(),
                    active.Select(v => v.ScanPid!.Value).ToArray(), peakScans, DateTime.UtcNow) { ScansStarted = scanned.ToArray() };
            }
        }
    }

    internal int LiveVolumeCount { get { lock (gate) return volumes.Values.Count(v => v.Monitoring); } }
    internal bool AuditCanFinish
    {
        get
        {
            lock (gate) return ReadyToSave && FullAuditReadiness.CanFinish(volumes.Count,
                volumes.Values.Count(v => v.Monitoring), volumes.Values.Count(v => v.ScanPid.HasValue));
        }
    }

    internal bool ReadyToSave
    {
        get
        {
            lock (gate) return discovered && restored && volumes.Count > 0
                && volumes.Values.All(v => v.Confirmed && !v.NeedsScan && v.View is not null);
        }
    }

    internal IReadOnlyList<ExitSnapshotVolume> CaptureForExit()
    {
        lock (gate) return volumes.Values.Select(v => v.View).OfType<ExitSnapshotVolume>().ToArray();
    }

    internal Task RefreshAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            suspended = false;
            launchReset++;
            collectorError = null;
            foreach (var volume in volumes.Values)
            {
                volume.Refresh = true;
                Revise(volume, "等待重新采集…");
            }
        }
        StatusChanged?.Invoke();
        return Task.CompletedTask;
    }

    internal void CancelCollection()
    {
        lock (gate)
        {
            suspended = true;
            collectorError = null;
            foreach (var volume in volumes.Values.Where(v => v.NeedsScan || v.Refresh))
            {
                volume.Refresh = false;
                Revise(volume, volume.View is null ? "采集已取消。" : "采集已取消，已有索引继续可用。");
            }
        }
        StatusChanged?.Invoke();
    }

    internal async Task RunAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var stop = lifetime.Token;
        Discover();
        Task restoration = Task.Run(() => Restore(stop), stop);
        Task<MemoryCollectorConnection>? launching = null;
        var watchers = new List<Task>();
        await using var scheduler = new VolumeWorkScheduler(policy.ScanLimit, policy.MemoryBudget, ScanAsync);
        DateTime nextDiscovery = DateTime.UtcNow.AddSeconds(2), nextLaunch = DateTime.MinValue;
        int attempts = 0, reset = 0;
        using var tick = new PeriodicTimer(TimeSpan.FromMilliseconds(180));
        try
        {
            do
            {
                stop.ThrowIfCancellationRequested();
                if (restoration.IsCompleted) await restoration;
                DateTime now = DateTime.UtcNow;
                if (now >= nextDiscovery)
                {
                    Discover();
                    nextDiscovery = now.AddSeconds(2);
                }
                lock (gate)
                {
                    if (reset != launchReset)
                    {
                        reset = launchReset;
                        attempts = 0;
                        nextLaunch = DateTime.MinValue;
                    }
                }
                if (collector?.Completion.IsCompleted == true)
                {
                    collector.Dispose();
                    Volatile.Write(ref collector, null);
                    nextLaunch = now.AddSeconds(1);
                }
                if (launching?.IsCompleted == true)
                {
                    try
                    {
                        Volatile.Write(ref collector, await launching);
                        SetCollectorStatus(null);
                        // 联机成功后允许采集器故障重启；被用户拒绝时，等待主动刷新。
                        attempts = 0;
                    }
                    catch (Exception error) when (MemoryCollector.IsCollectionError(error))
                    {
                        SetCollectorStatus(error is Win32Exception { NativeErrorCode: 1223 }
                            ? "管理员授权已取消，已有索引继续可用。"
                            : "采集进程启动失败：" + error.Message);
                        if (error is Win32Exception { NativeErrorCode: 1223 }) attempts = 3;
                        nextLaunch = now.AddSeconds(2);
                    }
                    launching = null;
                }
                var connection = collector;
                VolumeWork[] work;
                lock (gate)
                {
                    if (connection is null && launching is null && !suspended && volumes.Count > 0 && attempts < 3 && now >= nextLaunch)
                    {
                        attempts++;
                        launching = MemoryCollectorConnection.StartAsync(stop);
                    }

                    work = restored && !suspended && connection is not null
                        ? volumes.Values.Where(v => (v.NeedsScan || v.Refresh) && v.WatchStop is null && now >= v.RetryAt)
                            .Select(v => new VolumeWork(v.Volume, v.Revision.ToString(CultureInfo.InvariantCulture), v.Estimate)).ToArray()
                        : [];
                    if (connection is not null)
                        foreach (var volume in volumes.Values.Where(v => !v.NeedsScan && !v.Refresh && v.View is not null
                            && v.WatchStop is null && now >= v.RetryAt))
                        {
                            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stop);
                            volume.WatchStop = cancellation;
                            int generation = volume.Revision;
                            var view = volume.View!;
                            var graph = volume.Graph;
                            watchers.Add(Task.Run(() => WatchAsync(connection, volume, generation, view, graph, cancellation)));
                        }
                }
                watchers.RemoveAll(task => task.IsCompletedSuccessfully);
                scheduler.Reconcile(work);
                scheduler.Pump(stop, allowNew: VolumeResources.AvailableMemoryBytes() >= Math.Max(1, policy.MemoryBudget / 2));
            } while (await tick.WaitForNextTickAsync(stop));
        }
        finally
        {
            await lifetime.CancelAsync();
            collector?.Dispose();
            if (launching is not null)
            {
                try { (await launching).Dispose(); }
                catch (Exception error) when (error is OperationCanceledException || MemoryCollector.IsCollectionError(error)) { }
            }
            try { await Task.WhenAll(watchers.Append(restoration)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private void Discover()
    {
        var devices = VolumeDiscovery.Discover();
        var retained = devices.Select(VolumeCache.Key).ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        lock (gate)
        {
            foreach (string key in volumes.Keys.Where(key => !retained.Contains(key)).ToArray())
            {
                volumes[key].WatchStop?.Cancel();
                volumes[key].ScanStop?.Cancel();
                volumes.Remove(key);
                changed = true;
            }
            foreach (var volume in devices)
            {
                string key = VolumeCache.Key(volume);
                if (volumes.ContainsKey(key)) continue;
                var estimate = VolumeResources.EstimateBuildBytes(volume.Root[0], 0);
                volumes.Add(key, new(volume, key, ++revision, estimate));
                changed = true;
            }
            if (changed) catalog.RemoveExcept(retained);
            discovered = true;
        }
        if (changed) StatusChanged?.Invoke();
    }

    private void Restore(CancellationToken token)
    {
        foreach (var view in startup)
        {
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (!volumes.TryGetValue(VolumeCache.Key(view.Volume), out var volume) || volume.View is not null) continue;
                volume.View = view with { Volume = volume.Volume };
                volume.NeedsScan = false;
                volume.Status = new(volume.Volume.Root, "已加载退出快照，正在检查USN变化…");
                catalog.PublishLayers(volume.Key, view.Layers);
            }
            StatusChanged?.Invoke();
        }
        lock (gate) restored = true;
        StatusChanged?.Invoke();
    }

    private void SetCollectorStatus(string? error)
    {
        lock (gate)
        {
            if (collectorError == error) return;
            collectorError = error;
        }
        StatusChanged?.Invoke();
    }

    private void Revise(VolumeState volume, string message)
    {
        volume.Revision = ++revision;
        volume.Monitoring = false;
        volume.Status = new(volume.Volume.Root, message);
        volume.RetryAt = DateTime.MinValue;
        volume.WatchStop?.Cancel();
        volume.ScanStop?.Cancel();
    }

    private bool IsCurrent(VolumeState volume, int generation) => volume.Revision == generation
        && volumes.TryGetValue(volume.Key, out var current) && ReferenceEquals(volume, current);

    private sealed class VolumeState(NtfsVolume volume, string key, int revision, long estimate)
    {
        internal readonly NtfsVolume Volume = volume;
        internal readonly string Key = key;
        internal readonly long Estimate = estimate;
        internal int Revision = revision;
        internal DateTime RetryAt;
        internal bool NeedsScan = true;
        internal bool Refresh;
        internal bool Confirmed;
        internal bool Monitoring;
        internal ExitSnapshotVolume? View;
        internal DirectoryGraph? Graph;
        internal VolumeStatus Status = new(volume.Root, "等待后台采集");
        internal int? ScanPid;
        internal CancellationTokenSource? ScanStop;
        internal CancellationTokenSource? WatchStop;
    }
}
