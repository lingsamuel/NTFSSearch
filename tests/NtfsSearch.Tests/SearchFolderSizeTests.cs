using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class SearchFolderSizeTests
{
    private const string Root = @"C:\";

    [Theory]
    [InlineData(10)]
    [InlineData(16_500)]
    public void MixesDirectoryAndFileSizesBeforePagingAndRetainsUnknownAtEnd(int files)
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "match-dir", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "match-empty", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(Root, "match-incomplete", isDirectory: true, fileReference: 30, parentReference: 5);
        builder.Add(@"C:\match-dir\", "child", long.MaxValue, fileReference: 40, parentReference: 10);
        builder.Add(@"C:\match-dir\", "hardlink", long.MaxValue, fileReference: 40, parentReference: 10);
        builder.Add(@"C:\match-incomplete\", "unknown", fileReference: 41, parentReference: 30);
        for (int i = 0; i < files; i++)
            builder.Add(Root, $"match-file-{i:D5}", i, fileReference: (ulong)i + 100, parentReference: 5);
        var second = new IndexBuilder();
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

    [Fact]
    public void PaginatesLargeDirectoryOnlyMatchesInEitherDirection()
    {
        const int count = 17_000;
        var builder = new IndexBuilder();
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

    [Fact]
    public void IncrementalTotalsAndOrderingStayBoundToTheirVolumeViews()
    {
        var builder = new IndexBuilder();
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
