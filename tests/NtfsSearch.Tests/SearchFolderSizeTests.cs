using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class SearchFolderSizeTests
{
    private const string Root = @"C:\";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void LosingTheLastKnownDirectorySizeDoesNotLeaveAKnownOrder(bool precomputeSort)
    {
        var builder = new IndexBuilder(0, fixedSize: false, precomputeSort: precomputeSort);
        builder.Add(Root, "folder", isDirectory: true, fileReference: 10, parentReference: 5);
        var index = builder.Build();
        var live = new LiveVolumeIndex(index, new(1, 0));
        var aggregation = new DirectorySizeAggregation(Root);
        var original = Assert.Single(aggregation.Update(live.Layers, default));
        int directory = Assert.Single(index.FindFileRows(10));
        Assert.Equal(directory, Assert.Single(original.Ordered(false, default)));

        live.Apply(new LiveTransaction(new(1, 0), new(1, 1),
            [new(20, false, [new(10, @"C:\folder\", "unknown")])]));
        var updated = aggregation.Update(live.Layers, default).Single(order => order.Index.Records == index.Records);
        Assert.Null(updated.GetSize(directory));
        Assert.False(updated.HasKnownDirectories);
        Assert.Empty(updated.Ordered(true, default));
        Assert.Equal(0, original.GetSize(directory));
    }

    [Fact]
    public void DirectoryTotalsDeferThePermutationAndReuseTheSelectedSizeOrder()
    {
        const int count = 8192;
        var eager = CreateOrder(true);
        var lazy = CreateOrder(false);
        Assert.True(eager.Allocated - lazy.Allocated >= count * sizeof(int),
            $"目录合计启用预计算分配 {eager.Allocated:N0} 字节，按需模式分配 {lazy.Allocated:N0} 字节。");

        int first = lazy.Order.Index.FindFileRows(100)[0];
        int last = lazy.Order.Index.FindFileRows((ulong)count + 99)[0];
        Assert.Equal(0, lazy.Order.GetSize(first));
        var selection = new SearchSortSelection(SortField.Size);
        Assert.Equal(first, lazy.Order.Ordered(false, default, selection).First());

        long start = GC.GetAllocatedBytesForCurrentThread();
        int descending = lazy.Order.Ordered(true, default, selection).First();
        long reused = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(last, descending);
        Assert.True(reused < count * sizeof(int), $"反向读取已选中的目录大小排序分配了 {reused:N0} 字节。");

        start = GC.GetAllocatedBytesForCurrentThread();
        int independentlySorted = lazy.Order.Ordered(false, default).First();
        long temporary = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(first, independentlySorted);
        Assert.True(temporary >= count * sizeof(int), "未复用当前列的查询需要独立的临时排列。");

        static (DirectorySizeOrder Order, long Allocated) CreateOrder(bool precomputeSort)
        {
            var builder = new IndexBuilder(count, fixedSize: false, precomputeSort: precomputeSort);
            for (int i = 0; i < count; i++)
                builder.Add(Root, $"folder-{i:D5}", isDirectory: true, fileReference: (ulong)i + 100, parentReference: 5);
            var index = builder.Build();
            var usage = SpaceUsageIndex.Create(Root, [index], includeFileRows: false);
            _ = DirectorySizeOrder.Create(index, usage, default);
            long start = GC.GetAllocatedBytesForCurrentThread();
            var order = DirectorySizeOrder.Create(index, usage, default);
            return (order, GC.GetAllocatedBytesForCurrentThread() - start);
        }
    }

    [Fact]
    public void MetadataOnlyFileUpdateReusesSelectedDirectorySizeOrder()
    {
        const int count = 8192;
        var builder = new IndexBuilder(count + 1, fixedSize: false, precomputeSort: false);
        for (int i = 0; i < count; i++)
            builder.Add(Root, $"folder-{i:D5}", isDirectory: true, fileReference: (ulong)i + 100, parentReference: 5);
        builder.Add(@"C:\folder-00000\", "data", 7, 1, fileReference: 10, parentReference: 100);
        var index = builder.Build();
        var live = new LiveVolumeIndex(index, new(1, 0));
        var aggregation = new DirectorySizeAggregation(Root);
        var original = Assert.Single(aggregation.Update(live.Layers, default));
        var selection = new SearchSortSelection(SortField.Size);
        int first = Assert.Single(index.FindFileRows(100));
        Assert.Equal(first, original.Ordered(true, default, selection).First());

        Assert.True(live.Apply(new LiveTransaction(new(1, 0), new(1, 1),
            [new(10, false, [new(100, @"C:\folder-00000\", "data")], 7, 2)])));
        var updated = aggregation.Update(live.Layers, default)[0];
        Assert.NotSame(original.Index, updated.Index);

        long start = GC.GetAllocatedBytesForCurrentThread();
        int largest = updated.Ordered(true, default, selection).First();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.Equal(first, largest);
        Assert.True(allocated < count * sizeof(int), $"仅修改文件时间后读取目录大小排序分配了 {allocated:N0} 字节。");

        Assert.True(live.Apply(new LiveTransaction(new(1, 1), new(1, 2),
            [new(10, false, [new(100, @"C:\folder-00000\", "data")], 0, 3)])));
        var resized = aggregation.Update(live.Layers, default)[0];
        int last = Assert.Single(index.FindFileRows((ulong)count + 99));
        Assert.Equal(last, resized.Ordered(true, default, selection).First());
    }

    [Fact]
    public void DirectoryPathsInvalidateSelectedDirectorySizeOrder()
    {
        var builder = new IndexBuilder(2, fixedSize: false, precomputeSort: false);
        builder.Add(@"C:\alpha\", "folder", isDirectory: true, fileReference: 10, parentReference: 100);
        builder.Add(@"C:\beta\", "folder", isDirectory: true, fileReference: 20, parentReference: 200);
        var index = builder.Build();
        var aggregation = new DirectorySizeAggregation(Root);
        var original = Assert.Single(aggregation.Update([index], default));
        var selection = new SearchSortSelection(SortField.Size);
        int first = Assert.Single(index.FindFileRows(10));
        int second = Assert.Single(index.FindFileRows(20));
        Assert.Equal(new[] { first, second }, original.Ordered(false, default, selection));

        var directories = (string[])index.Directories.Clone();
        directories[index.Records[first].Directory] = @"C:\gamma\";
        var updated = Assert.Single(aggregation.Update([index.WithView(null, directories, default)], default));
        Assert.Equal(new[] { second, first }, updated.Ordered(false, default, selection));
    }

    [Theory]
    [InlineData(10, true)]
    [InlineData(10, false)]
    [InlineData(16_500, true)]
    [InlineData(16_500, false)]
    public void MixesDirectoryAndFileSizesBeforePagingAndRetainsUnknownAtEnd(int files, bool precomputeSort)
    {
        var builder = new IndexBuilder(0, fixedSize: false, precomputeSort: precomputeSort);
        builder.Add(Root, "match-dir", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "match-empty", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(Root, "match-incomplete", isDirectory: true, fileReference: 30, parentReference: 5);
        builder.Add(@"C:\match-dir\", "child", long.MaxValue, fileReference: 40, parentReference: 10);
        builder.Add(@"C:\match-dir\", "hardlink", long.MaxValue, fileReference: 40, parentReference: 10);
        builder.Add(@"C:\match-incomplete\", "unknown", fileReference: 41, parentReference: 30);
        for (int i = 0; i < files; i++)
            builder.Add(Root, $"match-file-{i:D5}", i, fileReference: (ulong)i + 100, parentReference: 5);
        var second = new IndexBuilder(0, fixedSize: false, precomputeSort: precomputeSort);
        second.Add(@"D:\", "match-file-large", long.MaxValue, fileReference: 11, parentReference: 5);
        var catalog = new SearchCatalog();
        catalog.Publish("C", builder.Build());
        catalog.Publish("D", second.Build());

        var pending = Run(true, 0, 3);
        Assert.True(pending.DirectorySortPending);
        Assert.Equal("match-file-large", pending.Rows[0].Name);
        var pendingDirectory = catalog.SearchDesktop("match-dir", SortField.Size, true, false, 0, 1, default);
        PublishSizes(catalog.FindVolume("C")!, new(Root));
        PublishSizes(catalog.FindVolume("D")!, new(@"D:\"));
        Assert.Null(Assert.Single(pendingDirectory.DirectoryRows!).SortSizes);

        var largest = Run(true, 0, 3);
        Assert.False(largest.DirectorySortPending);
        Assert.Equal(files + 4, largest.TotalCount);
        Assert.Equal(new[] { "match-dir", "match-file-large", $"match-file-{files - 1:D5}" }, largest.Rows.Select(r => r.Name));
        var source = Assert.Single(largest.DirectoryRows!);
        Assert.Equal((Int128)long.MaxValue * 2, source.SortSizes![source.Layer].GetSize(source.Row));
        Assert.Equal(new[] { "match-empty", "match-file-00000" }, Run(false, 0, 2).Rows.Select(r => r.Name));
        Assert.Equal(new[] { "match-empty", "match-incomplete" }, Run(true, files + 2, 2).Rows.Select(r => r.Name));
        Assert.Equal("match-incomplete", Run(false, files + 3, 1).Rows[0].Name);
        Assert.Equal("match-file-large", catalog.Search("match", SortField.Size, true, false, limit: 1).Rows[0].Name);

        SearchResult Run(bool descending, int offset, int limit) =>
            catalog.SearchDesktop("match", SortField.Size, descending, false, offset, limit, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PaginatesLargeDirectoryOnlyMatchesInEitherDirection(bool precomputeSort)
    {
        const int count = 17_000;
        var builder = new IndexBuilder(0, fixedSize: false, precomputeSort: precomputeSort);
        for (int i = 0; i < count; i++)
        {
            string name = $"folder-{i:D5}";
            ulong reference = (ulong)i + 100;
            builder.Add(Root, name, isDirectory: true, fileReference: reference, parentReference: 5);
            builder.Add(Root + name + "\\", "data", 7, fileReference: reference + count, parentReference: reference);
        }
        var catalog = new SearchCatalog();
        catalog.Publish("C", builder.Build());
        PublishSizes(catalog.FindVolume("C")!, new(Root));

        Assert.Equal(new[] { "folder-00123", "folder-00124" }, Query(false).Rows.Select(row => row.Name));
        Assert.Equal(new[] { "folder-16876", "folder-16875" }, Query(true).Rows.Select(row => row.Name));
        Assert.Equal(count, Query(true).TotalCount);

        SearchResult Query(bool descending) =>
            catalog.SearchDesktop("folder", SortField.Size, descending, false, 123, 2, default);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncrementalTotalsAndOrderingStayBoundToTheirVolumeViews(bool precomputeSort)
    {
        var builder = new IndexBuilder(0, fixedSize: false, precomputeSort: precomputeSort);
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(Root, "file", 15, fileReference: 25, parentReference: 5);
        builder.Add(@"C:\left\", "nested", isDirectory: true, fileReference: 30, parentReference: 10);
        builder.Add(@"C:\left\nested\", "child", 10, fileReference: 40, parentReference: 30);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var catalog = new SearchCatalog();
        var aggregation = new DirectorySizeAggregation(Root);
        var original = Publish();
        var originalPage = Search();
        Assert.Equal("file", originalPage.Rows[0].Name);

        live.Apply(new LiveTransaction(new(1, 0), new(1, 1),
            [new(40, false, [new(30, @"C:\left\nested\", "child")], 20)]));
        catalog.PublishLayers("C", live.Layers);
        Assert.True(Search().DirectorySortPending);
        PublishSizes(catalog.FindVolume("C")!, aggregation);
        Assert.Equal(new[] { "child", "nested", "left", "file", "right" }, Search().Rows.Select(r => r.Name));
        Assert.Equal(10, original.DirectorySizes![0].GetSize(original.Layers[0].FindFileRows(10)[0]));

        live.Apply(new LiveTransaction(new(1, 1), new(1, 2),
            [new(30, true, [new(20, @"C:\right\", "renamed")])]));
        Publish();
        Assert.Equal(new[] { "child", "renamed", "right", "file", "left" }, Search().Rows.Select(r => r.Name));
        Assert.True(live.CompactIfNeeded());
        Publish();
        Assert.Equal(new[] { "child", "renamed", "right", "file", "left" }, Search().Rows.Select(r => r.Name));
        catalog.RemoveExcept([]);
        Assert.Empty(Search().Rows);

        PublishedSearchVolume Publish()
        {
            catalog.PublishLayers("C", live.Layers);
            var volume = catalog.FindVolume("C")!;
            PublishSizes(volume, aggregation);
            return volume;
        }

        SearchResult Search() => catalog.SearchDesktop("", SortField.Size, true, true, 0, 20, default);
    }

    private static void PublishSizes(PublishedSearchVolume volume, DirectorySizeAggregation aggregation)
        => volume.DirectorySizes = aggregation.Update(volume.Layers, default);
}
