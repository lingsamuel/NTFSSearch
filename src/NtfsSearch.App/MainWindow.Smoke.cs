using System.Windows;
using System.Windows.Threading;
using NtfsSearch.Core;

namespace NtfsSearch.App;

public partial class MainWindow
{
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
        QueryBox.Text = ""; SortBox.SelectedIndex = 0; DescendingBox.IsChecked = false;
        offset = 400;
        await SearchAsync();
        ResultsGrid.ApplyTemplate();
        ResultsGrid.Measure(new Size(1000, 400));
        ResultsGrid.Arrange(new Rect(0, 0, 1000, 400));
        ResultsGrid.UpdateLayout();
        var scroll = FindResultsScrollViewer(ResultsGrid)
            ?? throw new InvalidOperationException("结果列表未创建滚动视图。");
        scroll.ScrollToVerticalOffset(30);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        double vertical = scroll.VerticalOffset;
        if (vertical <= 0) throw new InvalidOperationException("结果列表滚动测试未建立有效初始位置。");
        var displayed = ResultsGrid.ItemsSource;
        var selected = displayed.Cast<ResultView>().ElementAt(40);
        ResultsGrid.SelectedItem = selected;
        string userStatus = ResultStatus.Text;
        string volumeKey = VolumeCache.Key(new SmokeBackend().Discover().Single());
        catalog.Publish(volumeKey, DemoData.Create());
        await DrainRefreshAsync();
        if (offset != 400 || !ReferenceEquals(displayed, ResultsGrid.ItemsSource)
            || !ReferenceEquals(selected, ResultsGrid.SelectedItem) || ResultStatus.Text != userStatus
            || Math.Abs(scroll.VerticalOffset - vertical) > 0.01)
            throw new InvalidOperationException($"后台索引通知改变了用户查询状态：页码偏移={offset}，结果引用相同={ReferenceEquals(displayed, ResultsGrid.ItemsSource)}，选中项相同={ReferenceEquals(selected, ResultsGrid.SelectedItem)}，原状态={userStatus}，当前状态={ResultStatus.Text}。");

        var original = catalog.Search("", limit: 1000).Rows;
        string removedPath = displayedRows[0].FullPath;
        string addedName = selected.Name + "-新增";
        string addedPath = selected.FullPath[..^selected.Name.Length] + addedName;
        var changed = original.Where(row => row.FullPath != removedPath).Append(new SearchRow(0, addedName, addedPath, 1, null, false)).ToArray();
        catalog.Publish(volumeKey, BuildSmokeIndex(changed));
        await DrainRefreshAsync();
        if (offset != 400 || total != 1000 || !ReferenceEquals(selected, ResultsGrid.SelectedItem)
            || !displayedRows.Any(row => row.FullPath == addedPath) || displayedRows.Any(row => row.FullPath == removedPath)
            || Math.Abs(scroll.VerticalOffset - vertical) > 0.01 || ResultStatus.Text != userStatus)
            throw new InvalidOperationException("后台新增删除未实时反映，或改变了分页、选择、滚动和用户计时。");

        var metadataChanged = changed.Select(row => row.FullPath == selected.FullPath ? row with { Size = row.Size + 1 } : row).ToArray();
        catalog.Publish(volumeKey, BuildSmokeIndex(metadataChanged));
        await DrainRefreshAsync();
        if (ResultsGrid.SelectedItem is not ResultView current || current.FullPath != selected.FullPath || current.Size != selected.Size + 1)
            throw new InvalidOperationException("后台元数据更新丢失了仍存在的选中路径。");
        catalog.Publish(volumeKey, BuildSmokeIndex(metadataChanged.Where(row => row.FullPath != selected.FullPath)));
        await DrainRefreshAsync();
        if (ResultsGrid.SelectedItem is not null || displayedRows.Any(row => row.FullPath == selected.FullPath) || total != 999)
            throw new InvalidOperationException("后台删除未清理选中项或匹配计数。");

        offset = 800;
        await SearchAsync();
        var userElapsed = queryState.UserElapsed;
        catalog.Publish(volumeKey, BuildSmokeIndex(original.Take(650)));
        await DrainRefreshAsync();
        if (offset != 600 || total != 650 || displayedRows.Count != 50 || queryState.UserElapsed != userElapsed)
            throw new InvalidOperationException("末页删除后未退到最后有效页，或覆盖了用户查询耗时。");
    }

    private async Task DrainRefreshAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (refreshTimer.IsEnabled) await Task.Delay(20, deadline.Token);
        await queryTask.WaitAsync(deadline.Token);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
    }

    private static SearchIndex BuildSmokeIndex(IEnumerable<SearchRow> rows)
    {
        var builder = new IndexBuilder();
        foreach (var row in rows)
            builder.Add(row.FullPath[..^row.Name.Length], row.Name, row.Size, row.ModifiedUtcTicks, row.IsDirectory);
        return builder.Build();
    }
}
