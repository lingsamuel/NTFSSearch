using System.Diagnostics;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class SearchHost
{
    private readonly SearchCatalog catalog = new();
    private readonly AutomaticVolumeLoader loader;
    private readonly WindowsVolumeBackend? windowsBackend;

    internal SearchCatalog Catalog => catalog;
    internal AutomaticVolumeLoader Loader => loader;
    internal CollectorMetrics? Metrics => windowsBackend?.Metrics;
    internal event Action<string, bool>? Progress;

    internal SearchHost(int? demoCount = null, string? cacheDirectory = null)
    {
        IAutomaticVolumeBackend backend;
        if (demoCount is int count) backend = new SmokeBackend(count);
        else
        {
            windowsBackend = new WindowsVolumeBackend(cacheDirectory);
            windowsBackend.Progress += (text, failure) => Progress?.Invoke(text, failure);
            windowsBackend.LiveUpdated += update => catalog.PublishLiveLayers(update.VolumeKey, update.Generation, update.Layers);
            backend = windowsBackend;
        }
        var policy = IndexingConcurrencyPolicy.Create(Environment.ProcessorCount, VolumeResources.AvailableMemoryBytes());
        loader = new(backend, catalog, policy.CacheLoadLimit);
    }

    internal async Task RunAutomaticAsync(CancellationToken token)
    {
        if (windowsBackend is null)
        {
            await loader.RunAsync(token);
            return;
        }

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task loading = Task.Run(() => loader.RunAsync(stopping.Token), stopping.Token);
        Task monitoring = Task.Run(() => windowsBackend.RunLiveAsync(stopping.Token), stopping.Token);
        // 任一循环失败时，停止另一项并报告原始错误，否则窗口会一直显示正在加载。
        await Task.WhenAny(loading, monitoring);
        await stopping.CancelAsync();
        await Task.WhenAll(loading, monitoring);
    }

    internal async Task RefreshAsync(CancellationToken token)
    {
        windowsBackend?.RequestRefresh();
        await loader.RefreshAsync(token, retryCollector: true);
    }

    internal void CancelCollection() => windowsBackend?.CancelCollection();

    internal async Task SmokeTestAsync()
    {
        await loader.RefreshAsync();
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
                DiscoveredRoots = loader.Statuses.Select(s => s.Root).ToArray(),
                IndexedVolumes = catalog.VolumeCount,
                IndexedObjects = result.TotalCount,
                SearchHasResults = result.Rows.Count > 0,
                FirstIndexedRoot = result.Rows.FirstOrDefault()?.FullPath[..3],
                Parallel = windowsBackend?.Metrics,
                Mode = "正常自动发现、提权采集、缓存发布与自动加载路径；无手工索引、无主窗口、首卷就绪后停止测试。"
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
        try
        {
            while (true)
            {
                await WaitForFullAuditReadinessAsync(running, deadline.Token);
                var liveVerification = await AutomaticLiveVerification.RunAsync(catalog, diagnosticDirectory, deadline.Token);
                var rows = new List<object>();
                foreach (var status in loader.Statuses)
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
        }
    }

    private async Task WaitForFullAuditReadinessAsync(Task running, CancellationToken token)
    {
        while (true)
        {
            while (loader.Statuses.Count == 0 || !windowsBackend!.AuditCanFinish)
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
        public IReadOnlyList<NtfsVolume> Discover() => [new("smoke-volume", @"C:\", 100, "测试")];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token) => Task.FromResult<CachedVolume?>(new(DemoData.Create(count), false));
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "测试磁盘已发现";
    }
}
