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
    private readonly SemaphoreSlim queryGate = new(1);
    private CancellationTokenSource? queryCancellation;
    private readonly bool smokeTest;
    private int offset;
    private long total;
    private bool ready, closing;
    private const int PageSize = 200;

    public MainWindow(bool smokeTest = false)
    {
        InitializeComponent();
        this.smokeTest = smokeTest;
        IAutomaticVolumeBackend backend;
        if (smokeTest) backend = new SmokeBackend();
        else
        {
            windowsBackend = new WindowsVolumeBackend(Dispatcher);
            windowsBackend.Progress += text => OnUi(() => { if (!closing) TaskStatus.Text = text; });
            backend = windowsBackend;
        }
        loader = new(backend, catalog);
        catalog.Changed += () => OnUi(() => { if (!closing) { offset = 0; _ = SearchAsync(); } });
        loader.StatusChanged += () => OnUi(() =>
        {
            if (closing) return;
            DriveStatus.Text = string.Join("\n", loader.Statuses.Select(s => s.Root + "  " + s.Message));
            TaskStatus.Text = $"已发现 {loader.Statuses.Count} 个 NTFS 卷；已读取 {catalog.Count:N0} 个对象。";
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
        try { await Task.Run(() => loader.RunAsync(lifetime.Token), lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing) TaskStatus.Text = error.Message; }
    }

    private async Task SearchAsync()
    {
        if (!ready || closing) return;
        queryCancellation?.Cancel();
        var source = new CancellationTokenSource(); queryCancellation = source;
        long version = catalog.Version;
        string query = QueryBox.Text;
        var sort = (SortField)SortBox.SelectedIndex;
        bool descending = DescendingBox.IsChecked == true, matchPath = MatchPathBox.IsChecked == true;
        int pageOffset = offset;
        bool entered = false;
        try
        {
            await queryGate.WaitAsync(source.Token); entered = true;
            ResultStatus.Text = "搜索中…";
            var result = await Task.Run(() => catalog.Search(query, sort, descending, matchPath,
                pageOffset, PageSize, source.Token), source.Token);
            if (closing || source.IsCancellationRequested || version != catalog.Version) return;
            total = result.TotalCount;
            ResultsGrid.ItemsSource = result.Rows.Select(r => new ResultView(r)).ToArray();
            ResultStatus.Text = catalog.Count == 0 ? "暂无已完成的磁盘，读取完成后会自动显示结果。"
                : $"精确匹配 {total:N0} 个 · 第 {pageOffset / PageSize + 1:N0} 页 · 查询 {result.Elapsed.TotalMilliseconds:F2} ms · 已索引 {catalog.Count:N0} 个";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!closing && !source.IsCancellationRequested) ResultStatus.Text = error.Message; }
        finally
        {
            if (entered) queryGate.Release();
            if (ReferenceEquals(queryCancellation, source)) queryCancellation = null;
            source.Dispose();
        }
    }

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
        queryCancellation?.Cancel();
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
    { closing = true; lifetime.Cancel(); queryCancellation?.Cancel(); }

    internal async Task SmokeTestAsync()
    {
        await loader.RefreshAsync(lifetime.Token);
        await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        if (catalog.Count != 1000) throw new InvalidOperationException("自动发现和加载没有发布磁盘。");
        QueryBox.Text = "\"hello world\""; SortBox.SelectedIndex = 2; DescendingBox.IsChecked = true;
        await SearchAsync();
        var rows = ResultsGrid.ItemsSource.Cast<ResultView>().ToArray();
        if (total != 59 || rows.Length != 59 || rows[0].Size < rows[^1].Size)
            throw new InvalidOperationException("自动采集后的搜索与排序自检失败。");
        Measure(new Size(1120, 760)); Arrange(new Rect(0, 0, 1120, 760));
    }

    internal async Task<object> AutomaticReadTestAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        Task running = Task.Run(() => loader.RunAsync(deadline.Token), deadline.Token);
        try
        {
            while (catalog.Count == 0)
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

    private sealed class SmokeBackend : IAutomaticVolumeBackend
    {
        public IReadOnlyList<NtfsVolume> Discover() => [new("smoke-volume", @"C:\", 100, "测试")];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token) => Task.FromResult<CachedVolume?>(new(DemoData.Create(), false));
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "测试磁盘已发现";
    }

    private sealed class ResultView(SearchRow row)
    {
        public string Name => row.Name;
        public string FullPath => row.FullPath;
        public long? Size => row.Size;
        public string SizeText => row.Size is long size ? $"{size:N0}" : "—";
        public string ModifiedText => row.ModifiedUtcTicks is long ticks && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    }
}
