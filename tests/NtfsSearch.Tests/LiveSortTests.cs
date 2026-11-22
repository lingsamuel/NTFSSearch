using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class LiveSortTests
{
    [Fact]
    public void ModeChangesKeepTheLastDesktopColumnAndReleaseOtherColumns()
    {
        var catalog = new SearchCatalog();
        catalog.Publish("C", CreateIndex(@"C:\"));
        catalog.SearchDesktop("", SortField.Path, false, true, 0, 5, default);

        Assert.True(catalog.SetPrecomputeSort(true));
        Assert.True(catalog.PrecomputeSort);
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
        Assert.False(catalog.SortIsPrecomputed(SortField.Name));
        catalog.WarmSorts(default);
        Assert.All(Enum.GetValues<SortField>(), field => Assert.True(catalog.SortIsPrecomputed(field)));

        catalog.SearchDesktop("", SortField.Modified, true, true, 0, 5, default);
        catalog.Search("", SortField.Name);
        Assert.True(catalog.SetPrecomputeSort(false));
        Assert.False(catalog.PrecomputeSort);
        Assert.False(catalog.SortIsPrecomputed(SortField.Modified));

        Assert.True(catalog.SetPrecomputeSort(true));
        Assert.True(catalog.SortIsPrecomputed(SortField.Modified));
        Assert.False(catalog.SortIsPrecomputed(SortField.Path));
        Assert.False(catalog.SortIsPrecomputed(SortField.Name));
        Assert.False(catalog.SetPrecomputeSort(true));
    }

    [Fact]
    public void SupersededWarmPassCannotPopulateTheRetainedColumn()
    {
        var catalog = new SearchCatalog();
        catalog.Publish("C", CreateIndex(@"C:\"));
        catalog.SearchDesktop("", SortField.Path, false, true, 0, 0, default);
        catalog.SetPrecomputeSort(true);
        bool switched = false;

        catalog.WarmSorts(default, _ =>
        {
            if (switched) return;
            switched = true;
            catalog.SetPrecomputeSort(false);
            catalog.SetPrecomputeSort(true);
        });

        Assert.True(switched);
        Assert.False(catalog.SortIsPrecomputed(SortField.Path));
        catalog.WarmSorts(default);
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
    }

    [Fact]
    public async Task ConcurrentQueriesCompleteAfterTheirWarmSortIsCanceled()
    {
        var catalog = new SearchCatalog();
        var index = CreateIndex(@"C:\");
        catalog.Publish("C", index);
        catalog.SearchDesktop("", SortField.Path, false, true, 0, 0, default);
        catalog.SetPrecomputeSort(true);
        using var source = new CancellationTokenSource();
        var warming = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var warm = Task.Run(() => catalog.WarmSorts(source.Token, token =>
        {
            warming.TrySetResult();
            token.WaitHandle.WaitOne();
            token.ThrowIfCancellationRequested();
        }));

        Task<SearchResult> http;
        Task<SearchResult> desktop;
        try
        {
            await warming.Task.WaitAsync(TimeSpan.FromSeconds(5));
            http = Task.Run(() => catalog.Search("", SortField.Path, true, offset: 5, limit: 12));
            desktop = Task.Run(() => catalog.SearchDesktop("", SortField.Path, true, true, 5, 12, default));
        }
        finally { source.Cancel(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => warm.WaitAsync(TimeSpan.FromSeconds(5)));
        var results = await Task.WhenAll(http, desktop).WaitAsync(TimeSpan.FromSeconds(5));
        var expected = index.Search("", SortField.Path, true, offset: 5, limit: 12);
        Assert.All(results, actual =>
        {
            Assert.Equal(expected.TotalCount, actual.TotalCount);
            Assert.Equal(expected.Rows, actual.Rows);
        });
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
    }

    [Fact]
    public void EvictionAfterPreparingMatchesReportsColdSortBeforeReadingRows()
    {
        var index = CreateIndex(@"C:\");
        var selection = new SearchSortSelection(SortField.Path);
        var owner = index.SortSource;
        selection.UpdateOwners(new HashSet<object> { owner });
        var session = new object();
        selection.SetWarmSession(session);
        index.WarmSort(selection, session, default, checkpoint: null);
        using var query = index.Prepare("", matchPath: true, default);
        int coldSorts = 0;
        IEnumerable<int> Rows() => query.Ids(SortField.Path, false, default, selection: selection,
            beforeColdSort: () => coldSorts++);

        var expected = Rows().ToArray();
        Assert.Equal(0, coldSorts);
        var pending = Rows();
        selection.UpdateOwners(new HashSet<object>());

        Assert.Equal(expected, pending);
        Assert.Equal(1, coldSorts);
        Assert.False(selection.Contains(owner));
    }

    [Fact]
    public void WarmOrdersFollowCurrentSortSourcesAndSkipDisabledMetadata()
    {
        var catalog = new SearchCatalog();
        var original = CreateIndex(@"C:\", metadata: IndexMetadata.None);
        catalog.Publish("C", original);
        catalog.SetPrecomputeSort(true);
        catalog.WarmSorts(default);

        Assert.True(catalog.SortIsPrecomputed(SortField.Name));
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
        Assert.False(catalog.SortIsPrecomputed(SortField.Modified));
        Assert.False(catalog.SortIsPrecomputed(SortField.Size));

        var hidden = original.WithView([1UL], original.Directories, default);
        catalog.Publish("C", hidden);
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
        var renamedDirectories = hidden.Directories.Select(path => @"C:\new\" + path[3..]).ToArray();
        var replaced = hidden.WithView([1UL], renamedDirectories, default);
        catalog.Publish("C", replaced);
        Assert.False(catalog.SortIsPrecomputed(SortField.Path));
        catalog.WarmSorts(default);
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
        var page = catalog.Search("", SortField.Path, offset: 1);
        Assert.Equal(63, page.TotalCount);
        Assert.All(page.Rows, row => Assert.StartsWith(@"C:\new\", row.FullPath));

        catalog.Publish("C", original);
        Assert.False(catalog.SortIsPrecomputed(SortField.Path));
    }

    [Fact]
    public void SparsePathChangesReuseWarmBaselineOrdersAndKeepPagesExact()
    {
        const int count = 8192;
        const string root = @"C:\middle\";
        const string oldPath = @"C:\z-before\";
        const string newPath = @"C:\a-after\";
        var builder = new IndexBuilder(count, fixedSize: false, precomputeSort: false);
        var oracle = new IndexBuilder(count);
        for (int id = 0; id < count; id++)
        {
            string name = $"file-{id % 128:D3}";
            int? size = id % 4 == 0 ? id % 337 : null;
            int? modified = id % 9 == 0 ? id % 137 : null;
            builder.Add(id == 0 ? oldPath : root, name, size, modified);
            oracle.Add(id == 0 ? newPath : root, name, size, modified);
        }
        var original = builder.Build();
        var catalog = new SearchCatalog();
        catalog.Publish("C", original);
        catalog.SetPrecomputeSort(true);
        catalog.WarmSorts(default);
        var renamed = original.Directories.Select(path => path == oldPath ? newPath : path).ToArray();
        var moved = original.WithView(null, renamed, default);
        catalog.Publish("C", moved);
        var expected = oracle.Build();

        long start = GC.GetAllocatedBytesForCurrentThread();
        catalog.WarmSorts(default);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < count * sizeof(int), $"移动一行后补建排序分配了 {allocated:N0} 字节。");

        foreach (var field in Enum.GetValues<SortField>())
        {
            Assert.True(catalog.SortIsPrecomputed(field));
            Assert.Equal(expected.Search("", field, offset: 0, limit: count).Rows,
                catalog.Search("", field, offset: 0, limit: count).Rows);
            Assert.Equal(expected.Search("", field, descending: true, offset: 19, limit: 193).Rows,
                catalog.Search("", field, descending: true, offset: 19, limit: 193).Rows);
        }

        catalog.Publish("C", original);
        Assert.True(catalog.SortIsPrecomputed(SortField.Path));
        catalog.Publish("C", moved);
        Assert.False(catalog.SortIsPrecomputed(SortField.Path));
    }

    [Fact]
    public void ReplacedDirectoryTotalsDetachTheirWarmOrder()
    {
        const string root = @"C:\";
        var catalog = new SearchCatalog();
        catalog.Publish(root, CreateIndex(root));
        var volume = catalog.FindVolume(root)!;
        var sizes = new DirectorySizeAggregation(root).Update(volume.Layers, default);
        catalog.SetPrecomputeSort(true);
        catalog.WarmSorts(default);
        Assert.True(catalog.SetDirectorySizes(volume, sizes));

        Assert.True(WarmDirectoryOrder());
        Assert.False(WarmDirectoryOrder());

        Assert.True(catalog.SetDirectorySizes(volume, null));
        Assert.True(catalog.SetDirectorySizes(volume, sizes));
        Assert.True(WarmDirectoryOrder());

        catalog.Publish(root, CreateIndex(root));
        Assert.False(catalog.SetDirectorySizes(volume, sizes));

        bool WarmDirectoryOrder()
        {
            bool computed = false;
            catalog.WarmSorts(default, _ => computed = true);
            return computed;
        }
    }

    [Fact]
    public void UnchangedDirectoryTotalsReuseTheirWarmOrderAcrossPublication()
    {
        const string root = @"C:\";
        var catalog = new SearchCatalog();
        var original = CreateIndex(root);
        var aggregation = new DirectorySizeAggregation(root);
        catalog.Publish(root, original);
        var first = catalog.FindVolume(root)!;
        var sizes = aggregation.Update(first.Layers, default);
        Assert.True(catalog.SetDirectorySizes(first, sizes));
        catalog.SetPrecomputeSort(true);
        catalog.WarmSorts(default);

        catalog.Publish(root, original.WithView(null, original.Directories, default));
        var current = catalog.FindVolume(root)!;
        Assert.Null(current.DirectorySizes);
        var updated = aggregation.Update(current.Layers, default);
        Assert.Same(sizes[0].SortOwner, updated[0].SortOwner);
        Assert.True(catalog.SetDirectorySizes(current, updated));

        bool rebuilt = false;
        catalog.WarmSorts(default, _ => rebuilt = true);
        Assert.False(rebuilt);
    }

    [Fact]
    public void SizePagesAcrossVolumesStayExactWhenTheModeChanges()
    {
        var expected = new SearchCatalog();
        var actual = new SearchCatalog();
        foreach (string root in new[] { @"C:\", @"D:\" })
        {
            expected.Publish(root, CreateIndex(root, true));
            actual.Publish(root, CreateIndex(root));
            var volume = expected.FindVolume(root)!;
            Assert.True(expected.SetDirectorySizes(volume, new DirectorySizeAggregation(root).Update(volume.Layers, default)));
            volume = actual.FindVolume(root)!;
            Assert.True(actual.SetDirectorySizes(volume, new DirectorySizeAggregation(root).Update(volume.Layers, default)));
        }

        foreach (bool enabled in new[] { false, true, false, true })
        {
            actual.SetPrecomputeSort(enabled);
            if (enabled) actual.WarmSorts(default);
            foreach (bool descending in new[] { false, true })
            {
                var page = actual.SearchDesktop("", SortField.Size, descending, true, 57, 31, default);
                var oracle = expected.SearchDesktop("", SortField.Size, descending, true, 57, 31, default);
                Assert.Equal(128, page.TotalCount);
                Assert.Equal(oracle.Rows, page.Rows);
                Assert.False(page.DirectorySortPending);
                Assert.Equal(expected.Search("", SortField.Size, descending, offset: 57, limit: 31).Rows,
                    actual.Search("", SortField.Size, descending, offset: 57, limit: 31).Rows);
            }
        }
    }

    private static SearchIndex CreateIndex(string root, bool precomputeSort = false, IndexMetadata metadata = IndexMetadata.All)
    {
        var builder = new IndexBuilder(64, fixedSize: false, metadata, precomputeSort);
        builder.Add(root, "folder", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(root + "folder\\", "child", 5, 2, fileReference: 20, parentReference: 10);
        for (int i = 2; i < 64; i++)
            builder.Add(root, $"file-{64 - i:D2}", i % 4 == 0 ? null : i % 7,
                i % 5 == 0 ? null : i % 13, fileReference: (ulong)i + 30, parentReference: 5);
        return builder.Build();
    }
}
