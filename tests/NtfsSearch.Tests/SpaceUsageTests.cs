using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class SpaceUsageTests
{
    private const string Root = @"C:\";

    [Fact]
    public void DirectoryTotalsUseLogicalBytesPerVisiblePath()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(Root, "empty", isDirectory: true, fileReference: 30, parentReference: 5);
        builder.Add(@"C:\left\", "big", long.MaxValue, fileReference: 40, parentReference: 10);
        builder.Add(@"C:\right\", "link", long.MaxValue, fileReference: 40, parentReference: 20);
        builder.Add(@"C:\right\", "unknown", fileReference: 41, parentReference: 20);
        builder.Add(@"C:\right\", "zero", 0, fileReference: 42, parentReference: 20);

        var usage = SpaceUsageIndex.Create(Root, [builder.Build()]);
        var root = usage.Read(0, 200);
        Assert.Equal((Int128)long.MaxValue * 2, root.Bytes);
        Assert.Equal(1, root.UnknownFiles);
        Assert.Equal(new[] { "left", "right" }, root.Children.Select(item => item.Name).Order());
        var right = usage.Read(20, 200);
        Assert.Equal(long.MaxValue, right.Bytes);
        Assert.Equal(1, right.UnknownFiles);
        Assert.Equal(@"C:\right\link", Assert.Single(right.Children).Path);
        Assert.True(usage.Read(30, 200).IsEmpty);
    }

    [Fact]
    public void MovesDeletesAndCompactionFollowThePublishedView()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(@"C:\left\", "nested", isDirectory: true, fileReference: 30, parentReference: 10);
        builder.Add(@"C:\left\nested\", "item", 11, fileReference: 40, parentReference: 30);
        builder.Add(@"C:\left\nested\", "other", 7, fileReference: 41, parentReference: 30);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);

        live.Apply(Change(0, new(30, true, [new(20, @"C:\right\", "renamed")]),
            new(40, false, [new(30, @"C:\right\renamed\", "item")], 20)));
        usage = usage.Update(live.Layers);
        Assert.Equal(0, usage.Read(10, 200).Bytes);
        Assert.Equal(27, usage.Read(20, 200).Bytes);
        var nested = usage.Read(30, 200);
        Assert.Equal(new[] { Root, "right", "renamed" }, nested.Breadcrumbs.Select(crumb => crumb.Name));
        Assert.Equal(20, nested.Children[0].Bytes);
        Assert.Equal(@"C:\right\renamed\other", nested.Children[1].Path);

        Assert.True(live.CompactIfNeeded());
        usage = usage.Update(live.Layers);
        Assert.Equal(27, usage.Read(0, 200).Bytes);
        live.Apply(Change(1, new IndexedFileState(30, true, [])));
        usage = usage.Update(live.Layers);
        Assert.False(usage.Contains(30));
        Assert.True(usage.Read(20, 200).IsEmpty);
        Assert.Equal(0, usage.Read(0, 200).Bytes);
    }

    [Fact]
    public void RemainderRetainsTheExactSizeOfSmallerItems()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "largest", 10, fileReference: 10, parentReference: 5);
        builder.Add(Root, "smaller", 7, fileReference: 11, parentReference: 5);
        builder.Add(Root, "smallest", 2, fileReference: 12, parentReference: 5);

        var root = SpaceUsageIndex.Create(Root, [builder.Build()]).Read(0, 1);
        Assert.Equal(19, root.Bytes);
        Assert.Equal("largest", root.Children[0].Name);
        Assert.Equal(9, root.Children[1].Bytes);
        Assert.Equal(2, root.Children[1].Count);
        Assert.Null(root.Children[1].Path);
    }

    [Fact]
    public void MissingDirectoryRowsRetainTheNearestIndexedAncestor()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(@"C:\left\missing\", "existing", 7, fileReference: 12, parentReference: 11);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);
        Assert.Equal(7, usage.Read(10, 200).Bytes);
        Assert.Equal("missing", Assert.Single(usage.Read(10, 200).Children).Name);

        live.Apply(Change(0, new IndexedFileState(22, false, [new(21, @"C:\right\missing\", "new")], 9)));
        usage = usage.Update(live.Layers);
        Assert.Equal(9, usage.Read(20, 200).Bytes);
        Assert.Equal(new[] { Root, "right", "missing" }, usage.Read(21, 200).Breadcrumbs.Select(crumb => crumb.Name));

        live.Apply(Change(1, new IndexedFileState(22, false, [])));
        usage = usage.Update(live.Layers);
        Assert.True(usage.Read(20, 200).IsEmpty);
        Assert.False(usage.Contains(21));
    }

    [Fact]
    public void UnindexedAncestorsCanAppearAndDisappearThroughFileChanges()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\outer\inner\", "existing", 7, fileReference: 12, parentReference: 11);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);
        Assert.Equal(@"outer\inner", Assert.Single(usage.Read(0, 200).Children).Name);

        live.Apply(Change(0, new IndexedFileState(13, false, [new(10, @"C:\outer\", "new")], 9)));
        usage = usage.Update(live.Layers);
        Assert.Equal("outer", Assert.Single(usage.Read(0, 200).Children).Name);
        Assert.Equal(16, usage.Read(10, 200).Bytes);

        live.Apply(Change(1, new IndexedFileState(13, false, [])));
        usage = usage.Update(live.Layers);
        Assert.False(usage.Contains(10));
        Assert.Equal(@"outer\inner", Assert.Single(usage.Read(0, 200).Children).Name);
    }

    [Fact]
    public void AddressFindsEmptyDirectoriesAndFollowsRenames()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "Games", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\Games\", "Empty Folder", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\Games\", "file.txt", 3, fileReference: 12, parentReference: 10);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);

        Assert.Equal((0UL, false), usage.FindDirectory(Root));
        Assert.Equal((11UL, false), usage.FindDirectory(@"c:\games\empty folder\"));
        Assert.Null(usage.FindDirectory(@"C:\Games\file.txt").Id);
        Assert.Null(usage.FindDirectory(@"D:\Games").Id);
        Assert.True(usage.Read(11, 200).IsEmpty);

        live.Apply(Change(0, new IndexedFileState(10, true, [new(5, Root, "Renamed")])));
        usage = usage.Update(live.Layers);
        Assert.Null(usage.FindDirectory(@"C:\Games\Empty Folder").Id);
        Assert.Equal((11UL, false), usage.FindDirectory(@"C:\Renamed\Empty Folder"));
    }

    [Fact]
    public void AddressPrefersExactCaseAndChecksTheEntirePathForAmbiguity()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "Case", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "case", isDirectory: true, fileReference: 11, parentReference: 5);
        builder.Add(@"C:\case\", "Only", isDirectory: true, fileReference: 12, parentReference: 11);
        builder.Add(@"C:\Case\Missing\Child\", "visible.txt", 1, fileReference: 14, parentReference: 13);
        var usage = SpaceUsageIndex.Create(Root, [builder.Build()]);

        Assert.Equal((10UL, false), usage.FindDirectory(@"C:\Case"));
        Assert.Equal((11UL, false), usage.FindDirectory(@"C:\case"));
        Assert.Equal((null, true), usage.FindDirectory(@"C:\CASE"));
        Assert.Equal((12UL, false), usage.FindDirectory(@"C:\CASE\Only"));
        Assert.Equal((13UL, false), usage.FindDirectory(@"C:\Case\Missing\Child"));
        Assert.Null(usage.FindDirectory(@"C:\Case\Missing").Id);
    }

    [Fact]
    public void LocalAddressPreservesIndexedNamesWithTrailingDotsAndSpaces()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "Game", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "Game.", isDirectory: true, fileReference: 11, parentReference: 5);
        builder.Add(Root, "Game ", isDirectory: true, fileReference: 12, parentReference: 5);
        var usage = SpaceUsageIndex.Create(Root, [builder.Build()]);

        string dotted = Assert.IsType<string>(SpaceUsageIndex.NormalizeLocalPath(@"\\?\C:\Game."));
        string spaced = Assert.IsType<string>(SpaceUsageIndex.NormalizeLocalPath("  \"C:/Game \"  "));
        Assert.Equal((11UL, false), usage.FindDirectory(dotted));
        Assert.Equal((12UL, false), usage.FindDirectory(spaced));
        Assert.Null(SpaceUsageIndex.NormalizeLocalPath(@"\\host\Shared"));
        Assert.Null(SpaceUsageIndex.NormalizeLocalPath("C:Game"));
    }

    [Fact]
    public void FileActivityIncludesBothSidesOfMovesAndWritesWithoutSizeChanges()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(@"C:\left\", "nested", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\left\nested\", "item", 7, fileReference: 15, parentReference: 11);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);
        var parents = new HashSet<ulong>();
        var write = Change(0, new IndexedFileState(15, false, [new(11, @"C:\left\nested\", "item")], 7));
        live.CollectFileParents(write, parents);
        Assert.False(live.Apply(write));
        Assert.Equal(new[] { 10UL }, usage.ActivityFolders(0, parents));

        var move = Change(1, new IndexedFileState(15, false, [new(20, @"C:\right\", "item")], 7),
            new IndexedFileState(16, false, [new(11, @"C:\left\nested\", "new")], 9));
        live.CollectFileParents(move, parents);
        Assert.True(live.Apply(move));
        usage = usage.Update(live.Layers);
        Assert.Equal(new[] { 10UL, 20UL }, usage.ActivityFolders(0, parents).Order());
        Assert.Equal(new[] { 11UL }, usage.ActivityFolders(10, parents));
    }

    [Fact]
    public void DetectsAMovedIntermediateDirectoryEvenWhenTheDeepestDirectoryStillExists()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(@"C:\left\", "middle", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\left\middle\", "deep", isDirectory: true, fileReference: 12, parentReference: 11);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);
        Assert.Null(usage.FindBrokenBranch(0, [10, 11, 12]));

        live.Apply(Change(0, new IndexedFileState(11, true, [new(20, @"C:\right\", "middle")])));
        usage = usage.Update(live.Layers);
        Assert.True(usage.Contains(12));
        Assert.Equal(11UL, usage.FindBrokenBranch(0, [10, 11, 12]));
        Assert.Null(usage.FindBrokenBranch(0, [20, 11, 12]));
    }

    [Fact]
    public void ExclusionsRecalculateAncestorTotalsAndPromoteSmallerItems()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "games", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\games\", "cache", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\games\", "large", 90, fileReference: 12, parentReference: 10);
        builder.Add(@"C:\games\", "small", 6, fileReference: 14, parentReference: 10);
        builder.Add(@"C:\games\", "smaller", 3, fileReference: 15, parentReference: 10);
        builder.Add(@"C:\games\cache\", "nested", 40, fileReference: 16, parentReference: 11);
        builder.Add(@"C:\games\cache\", "unknown", fileReference: 17, parentReference: 11);
        var usage = SpaceUsageIndex.Create(Root, [builder.Build()]);
        SpaceExclusion large = new(@"C:\games\large", 12);
        SpaceExclusion cache = new(@"C:\games\cache", null);
        SpaceExclusion nested = new(@"C:\games\cache\nested", 16);

        var filter = usage.CreateFilter([large, cache, nested]);
        var root = usage.Read(0, 1, filter: filter);
        Assert.Equal(9, root.Bytes);
        Assert.Equal(0, root.UnknownFiles);
        Assert.Equal(9, Assert.Single(root.Children).Bytes);
        var games = usage.Read(10, 1, filter: filter);
        Assert.Equal("small", games.Children[0].Name);
        Assert.Equal(3, games.Children[1].Bytes);
        Assert.Equal(1, games.Children[1].Count);
        Assert.True(usage.IsExcluded(11, filter));

        filter = usage.CreateFilter([large, nested]);
        Assert.False(usage.IsExcluded(11, filter));
        root = usage.Read(0, 1, filter: filter);
        Assert.Equal(9, root.Bytes);
        Assert.Equal(1, root.UnknownFiles);
        Assert.False(usage.Read(11, 1, filter: filter).IsEmpty);
        Assert.Equal(139, usage.Read(0, 1).Bytes);
    }

    [Fact]
    public void AFileExclusionUsesTheLatestSizeOfOnlyTheSelectedHardLink()
    {
        var builder = new IndexBuilder();
        builder.Add(Root, "left", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(Root, "right", isDirectory: true, fileReference: 20, parentReference: 5);
        builder.Add(@"C:\left\", "shared", 9, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\right\", "shared", 9, fileReference: 11, parentReference: 20);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        var usage = SpaceUsageIndex.Create(Root, live.Layers);
        SpaceExclusion entry = new(@"C:\left\shared", 11);

        var filter = usage.CreateFilter([entry]);
        Assert.Equal(9, usage.Read(0, 1, filter: filter).Bytes);
        Assert.True(usage.Read(10, 1, filter: filter).IsEmpty);
        live.Apply(Change(0, new IndexedFileState(11, false,
            [new(10, @"C:\left\", "shared"), new(20, @"C:\right\", "shared")], 23)));
        usage = usage.Update(live.Layers);
        filter = usage.CreateFilter([entry]);
        Assert.Equal(23, usage.Read(0, 1, filter: filter).Bytes);
        Assert.Equal(@"C:\right\shared", Assert.Single(usage.Read(20, 1, filter: filter).Children).Path);

        live.Apply(Change(1, new IndexedFileState(11, false, [new(20, @"C:\right\", "shared")], 23),
            new IndexedFileState(12, false, [new(10, @"C:\left\", "shared")], 50)));
        usage = usage.Update(live.Layers);
        filter = usage.CreateFilter([entry]);
        Assert.Equal(23, usage.Read(0, 1, filter: filter).Bytes);
        Assert.True(usage.Read(10, 1, filter: filter).IsEmpty);
    }

    private static LiveTransaction Change(long from, params IndexedFileState[] files) =>
        new(new(1, from), new(1, from + 1), files);
}
