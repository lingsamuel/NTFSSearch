using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using NtfsSearch.Core;

namespace NtfsSearch.App;

public partial class MainWindow : Window
{
    private readonly SearchCatalog catalog = new();
    private readonly AutomaticVolumeLoader loader;
    private readonly WindowsVolumeBackend? windowsBackend;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? queryCancellation;
    private readonly bool smokeTest;
    private int offset;
    private long total;
    private bool ready, closing;
    private const int PageSize = 200;

    public MainWindow(bool smokeTest = false, string? cacheDirectory = null)
    {
        InitializeComponent();
        InitializeSearch();
        this.smokeTest = smokeTest;
        IAutomaticVolumeBackend backend;
        if (smokeTest) backend = new SmokeBackend();
        else
        {
            windowsBackend = new WindowsVolumeBackend(Dispatcher, cacheDirectory);
            windowsBackend.Progress += text => OnUi(() => { if (!closing) TaskStatus.Text = text; });
            windowsBackend.LiveUpdated += update => catalog.PublishLiveLayers(update.VolumeKey, update.Generation, update.Layers);
            backend = windowsBackend;
        }
        var policy = IndexingConcurrencyPolicy.Create(Environment.ProcessorCount, VolumeResources.AvailableMemoryBytes());
        loader = new(backend, catalog, policy.CacheLoadLimit);
        catalog.Changed += () => OnUi(RequestCatalogRefresh);
        loader.StatusChanged += () => OnUi(() =>
        {
            if (closing) return;
            DriveStatus.Text = string.Join("\n", loader.Statuses.Select(s => s.Root + "  " + s.Message));
            var metrics = windowsBackend?.Metrics;
            TaskStatus.Text = $"已发现 {loader.Statuses.Count} 个 NTFS 卷；已读取 {catalog.Count:N0} 个对象。"
                + (metrics is null ? "" : $" 后台并行 {metrics.ActiveRoots.Length}/{metrics.ScanLimit} 个卷。");
        });
        ready = true;
        TaskStatus.Text = "正在发现本机 NTFS 磁盘。已完成的磁盘会自动加入搜索。";
        Loaded += OnLoaded;
    }

    private void OnUi(Action action)
    {
        if (Dispatcher.CheckAccess()) action();
        else _ = Dispatcher.InvokeAsync(action);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (smokeTest) return;
        try { await RunAutomaticAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) TaskStatus.Text = error.Message; }
    }

    private Task RunAutomaticAsync(CancellationToken token) => Task.WhenAll(
        Task.Run(() => loader.RunAsync(token), token), windowsBackend is null ? Task.CompletedTask : Task.Run(() => windowsBackend.RunLiveAsync(token), token));

    private async void RefreshDisks(object sender, RoutedEventArgs e)
    {
        try
        {
            windowsBackend?.RequestRefresh();
            await Task.Run(() => loader.RefreshAsync(lifetime.Token, retryCollector: true), lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) TaskStatus.Text = error.Message; }
    }

    private void QueryChanged(object sender, RoutedEventArgs e) { if (!ready) return; offset = 0; _ = SearchAsync(); }
    private void SortChanged(object sender, SelectionChangedEventArgs e) => QueryChanged(sender, e);
    private void CancelTasks(object sender, RoutedEventArgs e)
    {
        CancelSearch();
        TryAction(() => windowsBackend?.CancelCollection());
    }
    private void PreviousPage(object sender, RoutedEventArgs e) { offset = Math.Max(0, offset - PageSize); _ = SearchAsync(); }
    private void NextPage(object sender, RoutedEventArgs e) { if ((long)offset + PageSize < total) { offset += PageSize; _ = SearchAsync(); } }
    private void OpenSelected(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is ResultView row) TryAction(() => Process.Start(new ProcessStartInfo(row.FullPath) { UseShellExecute = true }));
    }
    private void RevealSelected(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ResultView row) return;
        TryAction(() =>
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + row.FullPath); Process.Start(start);
        });
    }
    private void CopySelected(object sender, RoutedEventArgs e)
    { if (ResultsGrid.SelectedItem is ResultView row) TryAction(() => Clipboard.SetText(row.FullPath)); }
    private void TryAction(Action action) { try { action(); } catch (Exception error) { TaskStatus.Text = error.Message; } }
    private void OnClosing(object? sender, CancelEventArgs e)
    { closing = true; queryState.Close(); refreshTimer.Stop(); lifetime.Cancel(); queryCancellation?.Cancel(); }

    internal async Task<object> AutomaticReadTestAsync(bool requireParallel = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        Task running = RunAutomaticAsync(deadline.Token);
        try
        {
            while (loader.Statuses.Count == 0 || windowsBackend!.LiveVolumeCount != loader.Statuses.Count)
            {
                if (running.IsCompleted) await running;
                await Task.Delay(500, deadline.Token);
            }
            await Task.Delay(2500, deadline.Token);
            var liveVerification = await AutomaticLiveVerification.RunAsync(catalog, diagnosticDirectory, deadline.Token);
            var rows = new List<object>();
            foreach (var status in loader.Statuses)
            {
                var result = await Task.Run(() => catalog.Search(status.Root, limit: 0, cancellationToken: deadline.Token), deadline.Token);
                rows.Add(new { status.Root, Count = result.TotalCount });
            }
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
        finally
        {
            windowsBackend?.CancelCollection(); await deadline.CancelAsync();
            try { await running; } catch (OperationCanceledException) { }
        }
    }

    private sealed class SmokeBackend : IAutomaticVolumeBackend
    {
        public IReadOnlyList<NtfsVolume> Discover() => [new("smoke-volume", @"C:\", 100, "测试")];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token) => Task.FromResult<CachedVolume?>(new(DemoData.Create(), false));
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "测试磁盘已发现";
    }
}
