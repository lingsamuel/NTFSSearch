using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class LiveIndexTests
{
    [Fact]
    public void CreationOnlyChangesReachEveryHardLinkThroughCompaction()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "first", 12, 50, fileReference: 15, parentReference: 5, createdUtcTicks: 37);
        builder.Add(@"C:\", "second", 12, 50, fileReference: 15, parentReference: 5, createdUtcTicks: 37);
        var position = new JournalPosition(7, 100);
        var live = new LiveVolumeIndex(builder.Build(), position);
        var resolver = new JournalChangeResolver(@"D:\Data", id =>
            new(id, false, [new(5, @"C:\", "first"), new(5, @"C:\", "second")], 12, 50, 38));
        var transaction = resolver.Resolve(new(position, new(7, 101), [new(15, 5, 100, 0x8000, 0, "first")]), position);
        var committed = ChangeLogStore.Decode(ChangeLogStore.Encode(new(1, transaction)));

        Assert.True(live.Apply(committed));
        Assert.True(live.CompactIfNeeded());
        var index = Assert.Single(live.Layers);
        Assert.Equal(2, index.FindFileRows(15).Count);
        Assert.All(index.FindFileRows(15), id => Assert.Equal(38, index.GetRow(id).CreatedUtcTicks));
    }

    private static SearchIndex Baseline()
    {
        var b = new IndexBuilder();
        b.Add(@"C:\", "old", isDirectory: true, fileReference: 10, parentReference: 5);
        b.Add(@"C:\old\", "nested", isDirectory: true, fileReference: 11, parentReference: 10);
        b.Add(@"C:\old\nested\", "报告\ud800.txt", 7, 30, fileReference: 12, parentReference: 11);
        b.Add(@"C:\", "z.txt", 7, 30, fileReference: 12, parentReference: 5);
        return b.Build();
    }
    private static LiveTransaction Tx(long from, params IndexedFileState[] files) => new(new(1, from), new(1, from + 1), files);
    private static IndexedFileState File(ulong id, params IndexedLink[] links) => new(id, false, links, 9, 40);
    private static SearchCatalog Catalog(LiveVolumeIndex live)
    {
        var c = new SearchCatalog(); c.PublishLayers("C", live.Layers); return c;
    }
    [Fact]
    public void FullIdentityHardLinksReplaceDeleteRecreateAndDuplicate()
    {
        var baseline = Baseline();
        Assert.Equal(new[] { 2, 3 }, baseline.FindFileRows(12));
        var live = new LiveVolumeIndex(baseline, new(1, 0));
        var tx = Tx(0, File(12, new IndexedLink(5, @"C:\", "new.txt")));
        Assert.True(live.Apply(tx)); Assert.False(live.Apply(tx));
        Assert.Equal(3, Catalog(live).Count);
        Assert.Empty(Catalog(live).Search("z.txt").Rows);
        live.Apply(Tx(1, File(12)));
        Assert.Equal(2, Catalog(live).Count);
        live.Apply(Tx(2, File((1UL << 48) | 12, new IndexedLink(5, @"C:\", "reused.txt"))));
        live.Apply(Tx(3, File(12)));
        Assert.Single(Catalog(live).Search("reused").Rows);
        Assert.Equal(4, baseline.Search("").TotalCount);
        Assert.Same(baseline.Names, live.Layers[0].Names);
        Assert.Same(baseline.Records, live.Layers[0].Records);
    }
    [Fact]
    public void MovingDirectoryUpdatesNestedPathsAndAllSortPagination()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, [new(5, @"C:\", "zz")])));
        var c = Catalog(live);
        Assert.Single(c.Search(@"zz\nested\报告").Rows);
        Assert.Empty(c.Search(@"old\").Rows);
        foreach (SortField field in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
            {
                var all = c.Search("", field, descending).Rows;
                Assert.Equal(all.Skip(1).Take(2), c.Search("", field, descending, offset: 1, limit: 2).Rows);
                if (field == SortField.Path)
                    Assert.Equal(descending ? all.Select(r => r.FullPath).OrderDescending(StringComparer.OrdinalIgnoreCase)
                        : all.Select(r => r.FullPath).Order(StringComparer.OrdinalIgnoreCase), all.Select(r => r.FullPath));
            }
        live.Apply(Tx(1, new IndexedFileState(10, true, [])));
        Assert.Equal(new[] { @"C:\z.txt" }, Catalog(live).Search("").Rows.Select(r => r.FullPath));
    }
    [Fact]
    public void CompactionReleasesObsoleteRowsAndPreservesFutureDirectoryMoves()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        live.Apply(new CommittedChange(9, Tx(0, File(12,
            new IndexedLink(11, @"C:\old\nested\", "updated.txt"),
            new IndexedLink(5, @"C:\", "updated-link.txt")))));
        var before = Catalog(live);
        var expected = before.Search("", SortField.Path).Rows.Select(row => row.FullPath).ToArray();

        Assert.True(live.CompactIfNeeded());
        var current = Assert.Single(live.Layers);
        Assert.Equal(4, current.Count);
        Assert.Equal(9, live.AppliedSequence);
        Assert.Equal(new JournalPosition(1, 1), live.Position);
        Assert.Equal(expected, Catalog(live).Search("", SortField.Path).Rows.Select(row => row.FullPath));

        live.Apply(new CommittedChange(10, Tx(1, new IndexedFileState(10, true, [new(5, @"C:\", "moved")]))));
        Assert.Single(Catalog(live).Search(@"moved\nested\updated.txt").Rows);
        Assert.Single(Catalog(live).Search(@"C:\updated-link.txt").Rows);
        Assert.Equal(expected, before.Search("", SortField.Path).Rows.Select(row => row.FullPath));
    }

    [Fact]
    public void CheckpointRestoresWithoutRewritingBaseAndPreservesUtf16()
    {
        string dir = Path.Combine(Path.GetTempPath(), "live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline();
            var live = new LiveVolumeIndex(baseline, new(1, 0));
            live.Apply(new CommittedChange(7, Tx(0, File(20, new IndexedLink(11, @"C:\old\nested\", "新\ud800.txt")))));
            live.Apply(new CommittedChange(8, Tx(1, new IndexedFileState(10, true, [new(5, @"C:\", "moved")]))));
            live.SaveCheckpoint(Path.Combine(dir, "current"), 8);
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, Path.Combine(dir, "current"));
            Assert.Equal(8, restored.AppliedSequence);
            Assert.Equal(new JournalPosition(1, 2), restored.Position);
            Assert.Equal(Catalog(live).Search("", SortField.Path).Rows.Select(r => r.FullPath), Catalog(restored).Search("", SortField.Path).Rows.Select(r => r.FullPath));
            Assert.False(restored.Apply(new CommittedChange(8, Tx(1))));
            restored.Apply(new CommittedChange(9, Tx(2, File(20))));
            Assert.Empty(Catalog(restored).Search("新").Rows);
            Assert.Same(baseline.Records, restored.Layers[0].Records);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void SnapshotRoundTripRetainsIdentityAndCatalogLayerReplacement()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ntidx");
        try
        {
            SnapshotStore.Save(Baseline(), path);
            var loaded = SnapshotStore.Load(path);
            Assert.True(loaded.HasIdentity);
            Assert.Equal(2, loaded.FindFileRows(12).Count);
            var c = new SearchCatalog(); c.PublishLayers("C", [loaded, loaded]);
            Assert.Equal(1, c.VolumeCount); Assert.Equal(8, c.Count);
            c.Publish("C", loaded); Assert.Equal(4, c.Count);
            c.RemoveExcept([]); Assert.Equal(0, c.Count);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }
    [Fact]
    public void ReusedRecordNumberReplacesOldGenerationWithoutDeletingNewGeneration()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        live.Apply(Tx(0, File((2UL << 48) | 12, new IndexedLink(5, @"C:\", "reused.txt"))));
        Assert.Empty(Catalog(live).Search("z.txt").Rows);
        Assert.Empty(Catalog(live).Search("报告").Rows);
        live.Apply(Tx(1, File(12)));
        Assert.Single(Catalog(live).Search("reused").Rows);
    }

    [Fact]
    public void HardLinkAdditionRemovalAndNoOpProgressAreExact()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        var original = live.Layers[0];
        Assert.False(live.Apply(Tx(0, new IndexedFileState(12, false,
            [new(11, @"C:\old\nested\", "报告\ud800.txt"), new(5, @"C:\", "z.txt")], 7, 30))));
        Assert.Equal(new JournalPosition(1, 1), live.Position);
        Assert.Same(original, live.Layers[0]);
        live.Apply(Tx(1, new IndexedFileState(12, false,
            [new(11, @"C:\old\nested\", "报告\ud800.txt"), new(5, @"C:\", "z.txt"), new(10, @"C:\old\", "extra")], 7, 30)));
        Assert.Equal(5, Catalog(live).Count);
        live.Apply(Tx(2, File(12, new IndexedLink(10, @"C:\old\", "extra"))));
        Assert.Equal(3, Catalog(live).Count);
        Assert.Empty(Catalog(live).Search("z.txt").Rows);
    }

    [Fact]
    public void InvalidDirectoryCycleRollsBackAndCanBeRetried()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        var original = live.Layers[0];
        Assert.Throws<InvalidDataException>(() => live.Apply(Tx(0,
            new IndexedFileState(10, true, [new(11, @"C:\old\nested\", "loop")]))));
        Assert.Same(original, live.Layers[0]);
        Assert.Equal(new JournalPosition(1, 0), live.Position);
        Assert.True(live.Apply(Tx(0, new IndexedFileState(10, true, [new(5, @"C:\", "ok")]))));
        Assert.Single(Catalog(live).Search(@"ok\nested\报告").Rows);
    }

    [Fact]
    public void MultipleCheckpointsReuseSegmentsAndRestoreAfterDirectoryDeletion()
    {
        string dir = Path.Combine(Path.GetTempPath(), "live-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "current");
        try
        {
            var baseline = Baseline();
            var live = new LiveVolumeIndex(baseline, new(1, 0));
            live.Apply(new CommittedChange(1, Tx(0, File(20, new IndexedLink(5, @"C:\", "first")))));
            live.SaveCheckpoint(path, 1);
            string segment = Directory.GetFiles(path + ".segments", "*.ntidx").Single();
            DateTime written = System.IO.File.GetLastWriteTimeUtc(segment);
            live.Apply(new CommittedChange(2, Tx(1, new IndexedFileState(10, true, []))));
            live.SaveCheckpoint(path, 2);
            Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(segment));
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.Empty(Catalog(restored).Search("报告").Rows);
            restored.SaveCheckpoint(path, 2);
            Assert.Equal(written, System.IO.File.GetLastWriteTimeUtc(segment));
            restored.Apply(new CommittedChange(3, Tx(2, File(21, new IndexedLink(5, @"C:\", "second")))));
            restored.SaveCheckpoint(path, 3);
            var twice = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.Equal(3, Catalog(twice).Count);
            Assert.Empty(Catalog(twice).Search("nested").Rows);
            var backup = LiveVolumeIndex.LoadCheckpoint(baseline, path + ".bak");
            Assert.Equal(2, backup.AppliedSequence);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void ManySmallTransactionsKeepSegmentsBoundedAndPagesSorted()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        for (int i = 0; i < 200; i++)
            live.Apply(Tx(i, File((ulong)(100 + i), new IndexedLink(5, @"C:\", $"item{199 - i:D3}"))));
        Assert.InRange(live.Layers.Count, 2, 10);
        var c = Catalog(live);
        Assert.Equal(204, c.Count);
        Assert.Equal(new[] { "item090", "item091", "item092" }, c.Search("item", SortField.Name, offset: 90, limit: 3).Rows.Select(r => r.Name));
        foreach (SortField field in Enum.GetValues<SortField>())
        {
            var all = c.Search("", field, limit: 300).Rows;
            Assert.Equal(all.Skip(100).Take(20), c.Search("", field, offset: 100, limit: 20).Rows);
        }
    }

    [Fact]
    public void DirectoryMoveKeepsBaseOrdersAndUsesSmallCorrectionsAcrossRepeatedMoves()
    {
        var baseline = Baseline();
        var live = new LiveVolumeIndex(baseline, new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, [new(5, @"C:\", "zz")])));
        foreach (SortField field in Enum.GetValues<SortField>())
            Assert.Same(baseline.SortOrder(field), live.Layers[0].SortOrder(field));
        live.Apply(Tx(1, new IndexedFileState(11, true, [new(5, @"C:\", "aa")])));
        foreach (SortField field in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
            {
                var rows = Catalog(live).Search("", field, descending).Rows;
                var rebuilt = new IndexBuilder();
                foreach (var row in rows) rebuilt.Add(row.FullPath[..^row.Name.Length], row.Name, row.Size, row.ModifiedUtcTicks, row.IsDirectory);
                Assert.Equal(rebuilt.Build().Search("", field, descending).Rows.Select(r => r.FullPath), rows.Select(r => r.FullPath));
            }
    }

    [Fact]
    public void EmptyBaselineCanAcceptFirstLiveFile()
    {
        var live = new LiveVolumeIndex(new IndexBuilder().Build(), new(1, 0));
        live.Apply(Tx(0, File(20, new IndexedLink(5, @"C:\", "first"))));
        Assert.Single(Catalog(live).Search("").Rows);
    }

    [Fact]
    public void V2SnapshotRemainsReadableWithoutIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".ntidx");
        try
        {
            var builder = new IndexBuilder(); builder.Add(@"C:\", "legacy");
            LegacySnapshotFixture.Write(builder.Build(), path, 2);
            SnapshotStore.ValidateChecksum(path);
            var loaded = SnapshotStore.Load(path);
            Assert.False(loaded.HasIdentity);
            Assert.Equal("legacy", loaded.Search("").Rows.Single().Name);
        }
        finally { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    }

    [Fact]
    public void DeletedDirectoryWithoutReadableAttributesStillHidesDescendants()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, false, [])));
        Assert.Equal(new[] { @"C:\z.txt" }, Catalog(live).Search("").Rows.Select(r => r.FullPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParentMoveOrDeletionAndChildMoveOutUseFinalGraph(bool deleteParent)
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        live.Apply(Tx(0,
            new IndexedFileState(10, true, deleteParent ? [] : [new(5, @"C:\", "moved")]),
            new IndexedFileState(11, true, [new(5, @"C:\", "escaped")])));
        Assert.Single(Catalog(live).Search(@"escaped\报告").Rows);
        Assert.Empty(Catalog(live).Search(@"old\").Rows);
        Assert.Empty(Catalog(live).Search(@"moved\nested\").Rows);
        foreach (SortField field in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
            {
                var rows = Catalog(live).Search("", field, descending).Rows;
                var builder = new IndexBuilder();
                foreach (var row in rows) builder.Add(row.FullPath[..^row.Name.Length], row.Name, row.Size, row.ModifiedUtcTicks, row.IsDirectory);
                Assert.Equal(builder.Build().Search("", field, descending).Rows.Select(r => r.FullPath), rows.Select(r => r.FullPath));
            }
    }

    [Fact]
    public void DirectoryChangeMatchesExistingPathsWithDifferentCasingAndGenerationReuse()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "old", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\OLD\", "child", fileReference: 11, parentReference: 10);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, [new(5, @"C:\", "renamed")])));
        Assert.Single(Catalog(live).Search(@"renamed\child").Rows);
        live.Apply(Tx(1, new IndexedFileState((1UL << 48) | 10, true, [new(5, @"C:\", "newgeneration")])));
        Assert.Empty(Catalog(live).Search("child").Rows);
        Assert.Single(Catalog(live).Search("newgeneration").Rows);
    }

    [Fact]
    public void MultipleChangedSubtreesRespectPathComponentBoundaries()
    {
        var builder = new IndexBuilder();
        ulong reference = 10;
        foreach (string name in new[] { "a", "aa", "z", "zz" })
        {
            builder.Add(@"C:\", name, isDirectory: true, fileReference: reference, parentReference: 5);
            builder.Add(@"C:\" + name + @"\", "child", fileReference: reference + 100, parentReference: reference);
            reference++;
        }
        var baseline = builder.Build();
        var live = new LiveVolumeIndex(baseline, new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, [new(5, @"C:\", "moved-a")]),
            new IndexedFileState(12, true, [new(5, @"C:\", "moved-z")])));
        Assert.Equal(new[] { @"C:\aa\child", @"C:\moved-a\child", @"C:\moved-z\child", @"C:\zz\child" },
            Catalog(live).Search("child", SortField.Path).Rows.Select(row => row.FullPath));
        for (int d = 0; d < baseline.Directories.Length; d++)
            if (baseline.Directories[d] is @"C:\aa\" or @"C:\zz\")
                Assert.Same(baseline.Directories[d], live.Layers[0].Directories[d]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryChangesHandleBaselinePathsThatDisagreeWithDirectoryRows(bool delete)
    {
        // 两遍MFT采集之间目录改名时，目录行和子文件父路径可以来自不同时间点。
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "current", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\old\", "file", fileReference: 11, parentReference: 10);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, delete ? [] : [new(5, @"C:\", "renamed")])));
        Assert.Empty(Catalog(live).Search(@"old\").Rows);
        if (delete) Assert.Empty(Catalog(live).Search("").Rows);
        else Assert.Single(Catalog(live).Search(@"renamed\file").Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryChangesHandleDescendantsWithIndependentHistoricalPathAliases(bool delete)
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "current", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\old\", "child", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\between\child\", "file", fileReference: 12, parentReference: 11);
        var live = new LiveVolumeIndex(builder.Build(), new(1, 0));
        live.Apply(Tx(0, new IndexedFileState(10, true, delete ? [] : [new(5, @"C:\", "renamed")])));
        Assert.Empty(Catalog(live).Search(@"between\").Rows);
        if (delete) Assert.Empty(Catalog(live).Search("").Rows);
        else Assert.Single(Catalog(live).Search(@"renamed\child\file").Rows);
    }

    [Fact]
    public void CommittedMultipartAppliesZeroSpanPartsExactlyOnceBeforeFinalAdvance()
    {
        var live = new LiveVolumeIndex(Baseline(), new(1, 0));
        var position = new JournalPosition(1, 0);
        var first = new CommittedChange(1, new(position, position,
            [File(20, new IndexedLink(5, @"C:\", "first"))]));
        var second = new CommittedChange(2, new(position, position,
            [File(21, new IndexedLink(5, @"C:\", "second"))]));
        Assert.False(live.Apply(first.Transaction));
        Assert.True(live.Apply(first));
        Assert.False(live.Apply(first));
        Assert.True(live.Apply(second));
        Assert.Equal(position, live.Position);
        Assert.Equal(2, live.AppliedSequence);
        Assert.Equal(6, Catalog(live).Count);
        Assert.True(live.Apply(new CommittedChange(3, Tx(0, File(22, new IndexedLink(5, @"C:\", "final"))))));
        Assert.Equal(new JournalPosition(1, 1), live.Position);
        Assert.Equal(7, Catalog(live).Count);
        Assert.False(live.Apply(second));
    }

    [Fact]
    public void MultipartCheckpointRestoresMidwayAndContinuesWithoutCursorGap()
    {
        string dir = Path.Combine(Path.GetTempPath(), "live-parts-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "current");
        try
        {
            var baseline = Baseline();
            var live = new LiveVolumeIndex(baseline, new(1, 0));
            var position = new JournalPosition(1, 0);
            var first = new CommittedChange(1, new(position, position,
                [File(20, new IndexedLink(5, @"C:\", "first"))]));
            Assert.True(live.Apply(first));
            live.SaveCheckpoint(path, 1);
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.Equal(position, restored.Position);
            Assert.Equal(1, restored.AppliedSequence);
            Assert.False(restored.Apply(first));
            Assert.True(restored.Apply(new CommittedChange(2, new(position, position,
                [File(21, new IndexedLink(5, @"C:\", "second"))]))));
            Assert.False(restored.Apply(new CommittedChange(3, Tx(0))));
            Assert.Equal(new JournalPosition(1, 1), restored.Position);
            Assert.Equal(3, restored.AppliedSequence);
            Assert.Equal(6, Catalog(restored).Count);
            restored.SaveCheckpoint(path, 3);
            Assert.Equal(6, Catalog(LiveVolumeIndex.LoadCheckpoint(baseline, path)).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

}
