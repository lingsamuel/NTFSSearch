using System.Diagnostics;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class SearchHost
{
    private readonly SearchCatalog catalog = new();
    private readonly AutomaticVolumeLoader? demo;
    private readonly string cacheDirectory;
    private readonly string snapshotPath;
    private volatile MemoryVolumeBackend? windowsBackend;

    internal SearchCatalog Catalog => catalog;
    internal IReadOnlyList<VolumeStatus> Statuses => windowsBackend?.Statuses ?? demo?.Statuses ?? [];
    internal CollectorMetrics? Metrics => windowsBackend?.Metrics;
    internal event Action? StatusChanged;
    internal event Action<string, bool>? Progress;

    internal SearchHost(int? demoCount = null, string? cacheDirectory = null)
    {
        this.cacheDirectory = cacheDirectory ?? PortableDataDirectory.Resolve();
        snapshotPath = Path.Combine(this.cacheDirectory, ExitSnapshotStore.FileName);
        if (demoCount is int count)
        {
            demo = new(new SmokeBackend(count), catalog);
            demo.StatusChanged += () => StatusChanged?.Invoke();
        }
    }

    internal async Task RunAutomaticAsync(CancellationToken token)
    {
        if (demo is not null)
        {
            await demo.RunAsync(token);
            return;
        }

        var backend = new MemoryVolumeBackend(catalog, ReadStartup(token), cacheDirectory);
        backend.StatusChanged += () => StatusChanged?.Invoke();
        backend.Progress += (message, failure) => Progress?.Invoke(message, failure);
        windowsBackend = backend;
        await backend.RunAsync(token);
    }

    private IEnumerable<ExitSnapshotVolume> ReadStartup(CancellationToken token)
    {
        if (!File.Exists(snapshotPath)) yield break;
        Progress?.Invoke("正在加载上次退出保存的索引…", false);
        int parallelism = IndexingConcurrencyPolicy.Create(Environment.ProcessorCount,
            VolumeResources.AvailableMemoryBytes()).CacheLoadLimit;
        using var entries = ExitSnapshotStore.LoadVolumes(snapshotPath, token, maxConcurrency: parallelism).GetEnumerator();
        while (true)
        {
            ExitSnapshotVolume current;
            try
            {
                if (!entries.MoveNext()) yield break;
                current = entries.Current;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                Progress?.Invoke("退出快照无法读取，将从磁盘恢复索引：" + error.Message, true);
                yield break;
            }
            yield return current;
        }
    }

    internal Task RefreshAsync(CancellationToken token)
        => windowsBackend?.RefreshAsync(token) ?? demo?.RefreshAsync(token) ?? Task.CompletedTask;

    internal void CancelCollection() => windowsBackend?.CancelCollection();

    internal (NtfsVolume Volume, IReadOnlyList<SearchIndex> Layers)[] CaptureAnalysisVolumes()
    {
        if (windowsBackend is { } backend)
            return backend.CapturePublishedViews().Select(view => (view.Volume, view.Layers)).ToArray();
        if (demo is null) return [];
        var views = new List<(NtfsVolume, IReadOnlyList<SearchIndex>)>();
        foreach (var volume in SmokeBackend.Volumes)
            if (catalog.PublishedLayers(VolumeCache.Key(volume)) is { } layers) views.Add((volume, layers));
        return views.ToArray();
    }

    /// <summary>正常关闭且采集循环退出后保存，冻结每个卷的已发布视图与水位。</summary>
    internal void SaveExitSnapshot(Action<int, int> progress)
    {
        if (windowsBackend is not { ReadyToSave: true } backend) return;
        var volumes = backend.CapturePublishedViews();
        if (volumes.Count > 0) ExitSnapshotStore.Save(snapshotPath, volumes, progress: progress);
    }

    internal async Task SmokeTestAsync()
    {
        await demo!.RefreshAsync();
        var result = catalog.Search("\"hello world\"", SortField.Size, descending: true, limit: 200);
        if (catalog.Count != 1000 || result.TotalCount != 59 || result.Rows.Count != 59 ||
            result.Rows[0].Size < result.Rows[^1].Size)
            throw new InvalidOperationException("自动采集后的搜索与排序自检失败。");
    }

    internal async Task<object> AutomaticReadTestAsync(bool requireParallel = false)
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        Task running = RunAutomaticAsync(deadline.Token);
        try
        {
            while (catalog.Count == 0 || requireParallel && (windowsBackend?.Metrics?.PeakProcesses ?? 0) < 2)
            {
                if (running.IsCompleted) await running;
                await Task.Delay(200, deadline.Token);
            }
            var result = await Task.Run(() => catalog.Search("", limit: 1, cancellationToken: deadline.Token), deadline.Token);
            return new
            {
                DiscoveredRoots = Statuses.Select(s => s.Root).ToArray(),
                IndexedVolumes = catalog.VolumeCount,
                IndexedObjects = result.TotalCount,
                SearchHasResults = result.Rows.Count > 0,
                FirstIndexedRoot = result.Rows.FirstOrDefault()?.FullPath[..3],
                Parallel = windowsBackend?.Metrics,
                Mode = "正常自动发现、提权采集、内存发布路径；无手工索引、无主窗口、首卷就绪后停止测试。"
            };
        }
        finally
        {
            windowsBackend?.CancelCollection();
            await deadline.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    internal async Task<object> FullAuditAsync(string diagnosticDirectory)
    {
        using var deadline = new CancellationTokenSource();
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        Task running = RunAutomaticAsync(deadline.Token);
        bool finished = false;
        try
        {
            while (true)
            {
                await WaitForFullAuditReadinessAsync(running, deadline.Token);
                var liveVerification = await AutomaticLiveVerification.RunAsync(catalog, diagnosticDirectory, deadline.Token);
                var rows = new List<object>();
                foreach (var status in Statuses)
                {
                    var result = await Task.Run(() => catalog.Search(status.Root, limit: 0, cancellationToken: deadline.Token), deadline.Token);
                    rows.Add(new { status.Root, Count = result.TotalCount });
                }
                // CRUD或查询期间若日志失效触发修复，等新代提交且发布后重新审计，避免关闭时取消修复子进程。
                if (!windowsBackend!.AuditCanFinish) continue;
                using var process = Process.GetCurrentProcess();
                var memory = new
                {
                    WorkingSetBytes = process.WorkingSet64,
                    PrivateBytes = process.PrivateMemorySize64,
                    ManagedBytes = GC.GetTotalMemory(false),
                    AllocatedBytes = GC.GetTotalAllocatedBytes(false)
                };
                finished = true;
                return new
                {
                    Success = true,
                    Total = catalog.Count,
                    LiveVolumes = windowsBackend.LiveVolumeCount,
                    Volumes = rows,
                    ScansStarted = windowsBackend.Metrics?.ScansStarted,
                    LiveVerification = liveVerification,
                    Memory = memory
                };
            }
        }
        finally
        {
            windowsBackend?.CancelCollection(); await deadline.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
            if (finished) SaveExitSnapshot((_, _) => { });
        }
    }

    private async Task WaitForFullAuditReadinessAsync(Task running, CancellationToken token)
    {
        while (true)
        {
            while (Statuses.Count == 0 || !windowsBackend!.AuditCanFinish)
            {
                if (running.IsCompleted) await running;
                await Task.Delay(500, token);
            }
            await Task.Delay(2500, token);
            if (windowsBackend.AuditCanFinish) return;
        }
    }

    private sealed class SmokeBackend(int count) : IAutomaticVolumeBackend
    {
        internal static readonly NtfsVolume[] Volumes =
            [new("smoke-volume", @"C:\", 100, "测试"), new("smoke-empty", @"D:\", 100, "空卷")];
        public IReadOnlyList<NtfsVolume> Discover() => count == 1000 ? Volumes : [Volumes[0]];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token) =>
            Task.FromResult<CachedVolume?>(new(volume == Volumes[0] ? DemoData.Create(count) : new IndexBuilder().Build(token), false));
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "测试磁盘已发现";
    }
}
