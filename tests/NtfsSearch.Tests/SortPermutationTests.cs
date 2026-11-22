using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class SortPermutationTests
{
    [Fact]
    public void OnDemandSortingMatchesPrecomputedPagesAcrossVolumesAndDirections()
    {
        var eager = new SearchCatalog();
        var lazy = new SearchCatalog();
        foreach (string root in new[] { @"C:\", @"D:\" })
        {
            var all = new IndexBuilder(2050);
            var selected = new IndexBuilder(2050, fixedSize: false, precomputeSort: false);
            for (int id = 0; id < 2050; id++)
                foreach (var builder in new[] { all, selected })
                    builder.Add(root, $"file-{2049 - id:D4}.txt", id % 4 == 0 ? null : id % 7,
                        id % 5 == 0 ? null : id % 13);
            eager.Publish(root, all.Build());
            var index = selected.Build();
            Assert.Equal(0, index.Statistics.SortBytes);
            lazy.Publish(root, index);
        }

        foreach (var sort in new[] { SortField.Size, SortField.Path, SortField.Modified, SortField.Name, SortField.Size })
            foreach (bool descending in new[] { false, true })
            {
                const int offset = 3300, limit = 81;
                var expected = eager.SearchDesktop("", sort, descending, true, offset, limit, default);
                var actual = lazy.SearchDesktop("", sort, descending, true, offset, limit, default);
                Assert.Equal(expected.TotalCount, actual.TotalCount);
                Assert.Equal(expected.Rows, actual.Rows);
            }
    }

    [Fact]
    public void OnDemandOrderReusesVisibilityOnlyViewsAndReleasesReplacedPaths()
    {
        var builder = new IndexBuilder(2, fixedSize: false, precomputeSort: false);
        builder.Add(@"C:\old\", "a.txt");
        builder.Add(@"C:\second\", "b.txt");
        var original = builder.Build();
        var selection = new SearchSortSelection(SortField.Path);
        selection.UpdateOwners([original]);
        var order = original.ResolveSortOrder(SortField.Path, default, selection).Order;

        var hidden = original.WithView([1UL], original.Directories, default);
        selection.UpdateOwners([hidden]);
        Assert.Same(order, hidden.ResolveSortOrder(SortField.Path, default, selection).Order);

        var moved = hidden.WithView(null, [@"C:\third\", @"C:\second\"], default);
        selection.UpdateOwners([moved]);
        Assert.Same(original, moved.IdentitySource);
        Assert.False(selection.Contains(original));
        Assert.Equal(1, moved.ResolveSortOrder(SortField.Path, default, selection).Order[0]);
        Assert.Equal(0, original.ResolveSortOrder(SortField.Path, default, selection).Order[0]);
        Assert.False(selection.Contains(original));
    }

    [Fact]
    public void CodecPreservesByteBoundariesAndLast24BitId()
    {
        var values = new int[1 << 24];
        int[] expected = [0, 255, 256, 65535, 65536, 0xFFFFFF];
        for (int i = 0; i < expected.Length - 1; i++) values[i] = expected[i];
        values[^1] = expected[^1];
        var order = SortPermutation.TakeSorted(values);
        Assert.Equal(values.Length, order.Count);
        Assert.Equal((long)values.Length * 3 + 1, order.StorageBytes);
        for (int i = 0; i < expected.Length - 1; i++) Assert.Equal(expected[i], order[i]);
        Assert.Equal(expected[^1], order[values.Length - 1]);
        Assert.Throws<IndexOutOfRangeException>(() => order[-1]);
        Assert.Throws<IndexOutOfRangeException>(() => order[order.Count]);
    }

    [Fact]
    public void MoreThan24BitRowsUseRealInt32Storage()
    {
        Assert.False(SortPermutation.UsesPackedStorage(0));
        Assert.True(SortPermutation.UsesPackedStorage(1 << 24));
        Assert.False(SortPermutation.UsesPackedStorage((1 << 24) + 1));
        Assert.False(SortPermutation.UsesPackedStorage(100_000_000));
        var values = new int[(1 << 24) + 1]; values[^1] = 1 << 24;
        var order = SortPermutation.TakeSorted(values);
        Assert.Equal((long)values.Length * sizeof(int), order.StorageBytes);
        Assert.Equal(1 << 24, order[values.Length - 1]);
        Assert.Equal(0, SortPermutation.TakeSorted([]).Count);
    }

    [Fact]
    public void StreamReaderPacksInt32AcrossBlocksAndChecksPermutationBeforeTruncation()
    {
        const int count = 65539;
        int[] values = Enumerable.Range(0, count).Reverse().ToArray();
        using var stream = new MemoryStream(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
        var order = SortPermutation.Read(stream, count, new byte[(count + 7) / 8], default);
        Assert.Equal((long)count * 3 + 1, order.StorageBytes);
        for (int i = 0; i < count; i++) Assert.Equal(values[i], order[i]);
        foreach (int invalid in new[] { -1, count, 0x1000000, 0x1000000 | (count - 1), values[1] })
        {
            values[0] = invalid;
            using var bad = new MemoryStream(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()).ToArray());
            Assert.Throws<InvalidDataException>(() => SortPermutation.Read(bad, count, new byte[(count + 7) / 8], default));
        }
        Assert.Throws<OperationCanceledException>(() => SortPermutation.TakeSorted(values, new(true)));
    }

    [Fact]
    public void CachedPermutationsUseThreeBytesPerRowWithoutDroppingAnyOrder()
    {
        var builder = new IndexBuilder();
        for (int i = 0; i < 2048; i++) builder.Add(@"C:\", $"file{2048 - i:D4}", i, i);
        var index = builder.Build();
        Assert.Equal(4L * (index.Count * 3 + 1), index.Statistics.SortBytes);
    }

    [Fact]
    public void UnknownMetadataAliasesPathAcrossViewsAndSnapshots()
    {
        string path = Path.Combine(Path.GetTempPath(), $"packed-alias-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            for (int i = 0; i < 2048; i++) builder.Add(@"C:\old\", $"file{2048 - i:D4}");
            var index = builder.Build();
            Assert.Equal(2L * (index.Count * 3 + 1), index.Statistics.SortBytes);
            Assert.Same(index.SortOrder(SortField.Path), index.SortOrder(SortField.Size));
            Assert.Same(index.SortOrder(SortField.Path), index.SortOrder(SortField.Modified));
            var view = index.WithView(null, index.Directories, default);
            Assert.Same(index.SortOrder(SortField.Path), view.SortOrder(SortField.Path));
            SnapshotStore.Save(index, path);
            var restored = SnapshotStore.Load(path);
            Assert.Equal(index.Statistics.SortBytes, restored.Statistics.SortBytes);
            Assert.Same(restored.SortOrder(SortField.Path), restored.SortOrder(SortField.Size));
            Assert.Same(restored.SortOrder(SortField.Path), restored.SortOrder(SortField.Modified));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AllOrdersRemainExactAcrossPackedViewsAndSnapshotVersions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"packed-orders-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            var random = new Random(721);
            for (int i = 0; i < 2300; i++)
                builder.Add(i % 2 == 0 ? @"C:\old\" : @"C:\z\", $"file{random.Next(120):D3}",
                    i % 5 == 0 ? null : random.Next(8), i % 7 == 0 ? null : random.Next(8));
            var original = builder.Build();
            CheckOracle(original);
            var directories = original.Directories.Select(d => d.Replace("old", "zzzz", StringComparison.Ordinal)).ToArray();
            var changed = original.WithView(null, directories, default);
            Assert.True(changed.HasOrderCorrections);
            CheckOracle(changed);
            SnapshotStore.Save(changed, path);
            CheckOracle(SnapshotStore.Load(path));
            for (int version = 1; version <= 3; version++)
            {
                LegacySnapshotFixture.Write(original, path, version);
                CheckOracle(SnapshotStore.Load(path));
            }
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void LargePartialMatchesAndMultiBlockSaveKeepEveryPrebuiltOrder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"packed-large-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            for (int i = 0; i < 65539; i++)
                builder.Add(@"C:\", $"file{i:D5}." + (i % 2 == 0 ? "keep" : "skip"), i % 700, i % 313);
            var index = builder.Build();
            CheckOracle(index, "keep");
            SnapshotStore.Save(index, path);
            CheckOracle(SnapshotStore.Load(path), "keep");
        }
        finally { File.Delete(path); }
    }

    private static void CheckOracle(SearchIndex index, string query = "file")
    {
        var all = Enumerable.Range(0, index.Count).Select(index.GetRow).ToArray();
        foreach (SortField field in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
            {
                int Compare(SearchRow a, SearchRow b)
                {
                    bool knownA = field == SortField.Size ? a.Size.HasValue : field != SortField.Modified || a.ModifiedUtcTicks.HasValue;
                    bool knownB = field == SortField.Size ? b.Size.HasValue : field != SortField.Modified || b.ModifiedUtcTicks.HasValue;
                    if (knownA != knownB) return knownA ? -1 : 1;
                    int comparison = field switch
                    {
                        SortField.Name => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name),
                        SortField.Size when knownA => a.Size!.Value.CompareTo(b.Size!.Value),
                        SortField.Modified when knownA => a.ModifiedUtcTicks!.Value.CompareTo(b.ModifiedUtcTicks!.Value),
                        _ => 0
                    };
                    if (comparison == 0) comparison = StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
                    if (comparison == 0) comparison = a.Id.CompareTo(b.Id);
                    return descending && knownA && knownB ? -Math.Sign(comparison) : comparison;
                }
                var expected = all.Order(Comparer<SearchRow>.Create(Compare)).ToArray();
                var actual = index.Search("", field, descending, limit: index.Count);
                Assert.Equal(index.Count, actual.TotalCount);
                Assert.Equal(expected, actual.Rows);
                var matching = expected.Where(row => row.FullPath.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
                var filtered = index.Search(query, field, descending, offset: 1900, limit: 301);
                Assert.Equal(matching.Length, filtered.TotalCount);
                Assert.Equal(matching.Skip(1900).Take(301), filtered.Rows);
            }
    }
}
