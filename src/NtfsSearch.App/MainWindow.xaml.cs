using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using NtfsSearch.Core;

namespace NtfsSearch.App;

public partial class MainWindow : Window
{
    private SearchIndex? index;
    private CancellationTokenSource? queryCancellation, loadCancellation;
    private readonly SemaphoreSlim queryGate = new(1);
    private int offset;
    private long total;
    private bool ready, closing;
    private const int PageSize = 200;

    public MainWindow()
    {
        InitializeComponent();
        index = DemoData.Create(); ready = true;
        TaskStatus.Text = "演示数据，1000 个对象。可导入 EFU 或打开本程序保存的索引。";
        _ = SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (!ready || closing || index is null) return;
        queryCancellation?.Cancel();
        var source = new CancellationTokenSource(); queryCancellation = source;
        var current = index;
        string query = QueryBox.Text;
        var sort = (SortField)SortBox.SelectedIndex;
        bool descending = DescendingBox.IsChecked == true, matchPath = MatchPathBox.IsChecked == true;
        int pageOffset = offset;
        bool entered = false;
        try
        {
            await queryGate.WaitAsync(source.Token); entered = true;
            ResultStatus.Text = "搜索中…";
            var result = await Task.Run(() => current.Search(query, sort, descending, matchPath,
                pageOffset, PageSize, source.Token), source.Token);
            if (closing || source.IsCancellationRequested || !ReferenceEquals(index, current)) return;
            total = result.TotalCount;
            ResultsGrid.ItemsSource = result.Rows.Select(r => new ResultView(r)).ToArray();
            ResultStatus.Text = $"精确匹配 {total:N0} 个 · 第 {pageOffset / PageSize + 1:N0} 页 · 查询 {result.Elapsed.TotalMilliseconds:F2} ms · 索引 {index.Count:N0} 个";
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

    private async Task RunLoadAsync(Func<CancellationToken, IProgress<string>, SearchIndex> load, string description)
    {
        if (loadCancellation is not null) return;
        var source = new CancellationTokenSource(); loadCancellation = source;
        SetLoading(true); TaskStatus.Text = description;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (!closing && ReferenceEquals(loadCancellation, source)) TaskStatus.Text = text;
            });
            var loaded = await Task.Run(() => load(source.Token, progress), source.Token);
            if (source.IsCancellationRequested || closing) return;
            queryCancellation?.Cancel(); index = loaded; offset = 0;
            TaskStatus.Text = $"已载入 {loaded.Count:N0} 个对象。静态索引；文件系统变化需要重新扫描或导入。";
            await SearchAsync();
        }
        catch (OperationCanceledException) { if (!closing) TaskStatus.Text = "任务已取消，当前索引继续可用。"; }
        catch (Exception error) { if (!closing) TaskStatus.Text = error.Message; }
        finally { SetLoading(false); loadCancellation = null; source.Dispose(); }
    }

    private void SetLoading(bool loading) => LoadButton.IsEnabled = ImportButton.IsEnabled = SaveButton.IsEnabled = DemoButton.IsEnabled = !loading;

    private async void OpenSnapshot(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "NTFS Search 索引|*.ntidx;*.bak|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true)
            await RunLoadAsync((token, progress) => SnapshotStore.Load(dialog.FileName, token, progress), "校验并加载索引；原索引仍可查询…");
    }

    private async void ImportEfu(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Everything 文件清单|*.efu;*.csv|所有文件|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        await RunLoadAsync((token, progress) =>
        {
            using var reader = File.OpenText(dialog.FileName);
            return EfuImporter.Read(reader, token, stages: progress);
        }, "导入文件清单并建立排序索引；原索引仍可查询…");
    }

    private async void SaveSnapshot(object sender, RoutedEventArgs e)
    {
        if (index is null || loadCancellation is not null) return;
        var dialog = new SaveFileDialog { Filter = "NTFS Search 索引|*.ntidx", DefaultExt = ".ntidx" };
        if (dialog.ShowDialog(this) != true) return;
        var current = index;
        await RunLoadAsync((token, _) => { SnapshotStore.Save(current, dialog.FileName, token); return current; }, "保存索引…");
    }

    private async void LoadDemo(object sender, RoutedEventArgs e) => await RunLoadAsync((_, _) => DemoData.Create(), "载入演示数据…");
    private void QueryChanged(object sender, RoutedEventArgs e) { if (!ready) return; offset = 0; _ = SearchAsync(); }
    private void SortChanged(object sender, SelectionChangedEventArgs e) => QueryChanged(sender, e);
    private void CancelTasks(object sender, RoutedEventArgs e) { queryCancellation?.Cancel(); loadCancellation?.Cancel(); }
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
    {
        if (ResultsGrid.SelectedItem is ResultView row) TryAction(() => Clipboard.SetText(row.FullPath));
    }
    private void TryAction(Action action) { try { action(); } catch (Exception error) { TaskStatus.Text = error.Message; } }
    private void OnClosing(object? sender, CancelEventArgs e) { closing = true; queryCancellation?.Cancel(); loadCancellation?.Cancel(); }

    internal async Task SmokeTestAsync()
    {
        QueryBox.Text = "\"hello world\""; SortBox.SelectedIndex = 2; DescendingBox.IsChecked = true;
        await SearchAsync();
        var rows = ResultsGrid.ItemsSource.Cast<ResultView>().ToArray();
        if (total != 59 || rows.Length != 59 || rows[0].Size < rows[^1].Size)
            throw new InvalidOperationException("WPF 搜索与排序自检失败。");
        Measure(new Size(1120, 760)); Arrange(new Rect(0, 0, 1120, 760));
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
