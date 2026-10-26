using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NtfsSearch.Core;

namespace NtfsSearch.App;

public partial class MainWindow
{
    private const int RefreshCoalesceMilliseconds = 200;
    private readonly QueryRefreshState queryState = new();
    private readonly ObservableCollection<ResultView> displayedRows = [];
    private readonly DispatcherTimer refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(RefreshCoalesceMilliseconds) };
    private Task queryTask = Task.CompletedTask;

    private void InitializeSearch()
    {
        ResultsGrid.ItemsSource = displayedRows;
        refreshTimer.Tick += (_, _) =>
        {
            refreshTimer.Stop();
            queryState.RefreshWindowElapsed();
            _ = StartQueryPump();
        };
    }

    private void RequestCatalogRefresh()
    {
        if (!ready || closing) return;
        queryState.RequestRefresh();
        // 已启动的窗口不延期：持续文件变化时也必须定期发布已完成的查询快照。
        if (!refreshTimer.IsEnabled) refreshTimer.Start();
    }

    private Task SearchAsync()
    {
        if (!ready || closing) return Task.CompletedTask;
        queryState.RequestUser();
        refreshTimer.Stop();
        queryCancellation?.Cancel();
        return StartQueryPump();
    }

    private Task StartQueryPump()
    {
        if (queryTask.IsCompleted) queryTask = RunQueriesAsync();
        return queryTask;
    }

    private async Task RunQueriesAsync()
    {
        while (queryState.TryStart(out var request))
        {
            refreshTimer.Stop();
            string query = QueryBox.Text;
            var sort = (SortField)SortBox.SelectedIndex;
            bool descending = DescendingBox.IsChecked == true, matchPath = MatchPathBox.IsChecked == true;
            int pageOffset = offset;
            using var source = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            queryCancellation = source;
            SearchResult? result = null;
            Exception? failure = null;
            try
            {
                if (request.UserInitiated) ResultStatus.Text = "搜索中…";
                while (true)
                {
                    result = await Task.Run(() => catalog.Search(query, sort, descending, matchPath,
                        pageOffset, PageSize, source.Token), source.Token);
                    source.Token.ThrowIfCancellationRequested();
                    if (pageOffset == 0 || pageOffset < result.TotalCount) break;
                    // 删除可能使最后一页失效，仅退到仍存在的最后一页，不跳回首页。
                    pageOffset = result.TotalCount == 0 ? 0 : (int)((result.TotalCount - 1) / PageSize * PageSize);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure = error; }
            finally { queryCancellation = null; }

            // SearchCatalog已绑定不可变快照；后台版本变化只安排后继刷新，不丢弃刚完成的结果。
            bool accepted = queryState.Complete(request, !source.IsCancellationRequested && failure is null ? result?.Elapsed : null);
            if (!accepted || closing || source.IsCancellationRequested) continue;
            if (failure is not null)
            {
                if (request.UserInitiated) ResultStatus.Text = failure.Message;
                else TaskStatus.Text = failure.Message;
                continue;
            }
            if (result is null) continue;
            total = result.TotalCount;
            offset = pageOffset;
            ApplyResultRows(result.Rows, preserveView: !request.UserInitiated);
            string timing = queryState.UserElapsed is TimeSpan elapsed ? $" · 用户查询 {elapsed.TotalMilliseconds:F2} ms" : "";
            ResultStatus.Text = catalog.Count == 0 ? "暂无已完成的磁盘，读取完成后会自动显示结果。"
                : $"精确匹配 {total:N0} 个 · 第 {pageOffset / PageSize + 1:N0} 页{timing} · 已索引 {catalog.Count:N0} 个";
        }
    }

    private void CancelSearch()
    {
        queryState.Cancel();
        refreshTimer.Stop();
        queryCancellation?.Cancel();
    }

    private void ApplyResultRows(IReadOnlyList<SearchRow> rows, bool preserveView)
    {
        var previous = displayedRows.ToDictionary(row => row.FullPath, StringComparer.Ordinal);
        var next = rows.Select(row => previous.TryGetValue(row.FullPath, out var old) && old.HasSameValues(row)
            ? old : new ResultView(row)).ToArray();
        if (displayedRows.SequenceEqual(next)) return;

        string? selectedPath = preserveView ? (ResultsGrid.SelectedItem as ResultView)?.FullPath : null;
        var scroll = FindResultsScrollViewer(ResultsGrid);
        double vertical = preserveView ? scroll?.VerticalOffset ?? 0 : 0;
        double horizontal = preserveView ? scroll?.HorizontalOffset ?? 0 : 0;
        // 保留未变化的行对象；只移动、插入或移除本页实际变化的行，避免重设ItemsSource。
        for (int i = 0; i < next.Length; i++)
        {
            if (i < displayedRows.Count && ReferenceEquals(displayedRows[i], next[i])) continue;
            int oldIndex = displayedRows.IndexOf(next[i]);
            if (oldIndex >= 0) displayedRows.Move(oldIndex, i);
            else displayedRows.Insert(i, next[i]);
        }
        while (displayedRows.Count > next.Length) displayedRows.RemoveAt(displayedRows.Count - 1);
        ResultsGrid.SelectedItem = selectedPath is null ? null : next.FirstOrDefault(row => row.FullPath == selectedPath);
        if (scroll is null) return;
        ResultsGrid.UpdateLayout();
        scroll.ScrollToVerticalOffset(vertical);
        scroll.ScrollToHorizontalOffset(horizontal);
    }

    private static ScrollViewer? FindResultsScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindResultsScrollViewer(child) is { } descendant) return descendant;
        }
        return null;
    }

    private sealed class ResultView(SearchRow row)
    {
        public string Name => row.Name;
        public string FullPath => row.FullPath;
        public long? Size => row.Size;
        public string SizeText => row.Size is long size ? $"{size:N0}" : "—";
        public string ModifiedText => row.ModifiedUtcTicks is long ticks && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";

        // Id只在所属索引层内有效；相同文件被增量层替换后不能据此判定显示内容变化。
        public bool HasSameValues(SearchRow other) => row.Name == other.Name && row.FullPath == other.FullPath
            && row.Size == other.Size && row.ModifiedUtcTicks == other.ModifiedUtcTicks && row.IsDirectory == other.IsDirectory;
    }
}
