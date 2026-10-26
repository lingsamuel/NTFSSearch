using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class SearchTests
{
    private static SearchIndex Sample()
    {
        var b = new IndexBuilder();
        b.Add(@"C:\hello world\", "Report.TXT", 30, 300);
        b.Add(@"D:\work\", "hello-other-world.txt", 10, 100);
        b.Add(@"D:\work\", "hello world.txt", 20, 200);
        b.Add(@"D:\工作\", "报告 ÄÖ.txt", null, null);
        b.Add(@"D:\work\", "folder", null, 50, true);
        return b.Build();
    }

    [Fact]
    public void SpacesAreAndAndQuotedSpacesAreLiteral()
    {
        var index = Sample();
        Assert.Equal(3, index.Search("hello world").TotalCount);
        Assert.Equal(2, index.Search("\"hello world\"").TotalCount);
        Assert.Equal(2, index.Search("hello world", matchPath: false).TotalCount);
        Assert.Equal(1, index.Search("\"hello world\"", matchPath: false).TotalCount);
    }

    [Theory]
    [InlineData("work\\hello", 2)]
    [InlineData("\\工作\\报告", 1)]
    [InlineData("äö", 1)]
    [InlineData("\"hello world", 2)]
    [InlineData("no-such-name", 0)]
    public void PathsUnicodeAndIncompleteInput(string query, int count) =>
        Assert.Equal(count, Sample().Search(query).TotalCount);

    [Fact]
    public void AllSortsAndPagesMatchIndependentOracle()
    {
        var index = Sample();
        foreach (var field in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
            {
                var rows = Enumerable.Range(0, index.Count).Select(index.GetRow).ToArray();
                Func<SearchRow, object?> key = field switch
                {
                    SortField.Name => r => r.Name,
                    SortField.Path => r => r.FullPath,
                    SortField.Size => r => r.Size,
                    _ => r => r.ModifiedUtcTicks
                };
                var known = rows.Where(r => key(r) is not null);
                var unknown = rows.Where(r => key(r) is null);
                var comparer = Comparer<object?>.Create((a, b) => a is string x && b is string y
                    ? StringComparer.OrdinalIgnoreCase.Compare(x, y) : Comparer<object?>.Default.Compare(a, b));
                var ordered = descending ? known.OrderByDescending(key, comparer) : known.OrderBy(key, comparer);
                var expected = ordered.Concat(unknown.OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase))
                    .Select(r => r.FullPath).ToArray();
                var actual = index.Search("", field, descending, offset: 1, limit: 3);
                Assert.Equal(5, actual.TotalCount);
                Assert.Equal(expected.Skip(1).Take(3), actual.Rows.Select(r => r.FullPath));
            }
    }

    [Fact]
    public void PathOrderingUsesWholePathNotOnlyParentDirectory()
    {
        var b = new IndexBuilder();
        foreach (var (parent, name) in new[] { (@"C:\", "z"), (@"C:\a\", "file"), (@"C:\", "a.txt"), (@"C:\", "a") })
            b.Add(parent, name);
        var index = b.Build();
        Assert.Equal(new[] { @"C:\a", @"C:\a.txt", @"C:\a\file", @"C:\z" },
            index.Search("", SortField.Path).Rows.Select(r => r.FullPath));
    }

    [Fact]
    public void CancellationAndBoundsAreEnforced()
    {
        var index = Sample();
        Assert.Throws<OperationCanceledException>(() => index.Search("a", cancellationToken: new(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("", offset: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("", limit: -1));
        Assert.Empty(index.Search("", offset: 100).Rows);
    }

    [Fact]
    public void FilteredLargeResultUsesGlobalSort()
    {
        var b = new IndexBuilder(30_000);
        for (int i = 0; i < 30_000; i++) b.Add(@"C:\data\", $"file{i:D6}.txt", 30_000 - i, i);
        var index = b.Build();
        var result = index.Search("txt", SortField.Size, offset: 20_000, limit: 3);
        Assert.Equal(30_000, result.TotalCount);
        Assert.Equal(new long?[] { 20_001, 20_002, 20_003 }, result.Rows.Select(r => r.Size));
    }

    [Fact]
    public void DirectorySpellingIsPreservedInCaseSensitiveNtfsTrees()
    {
        var b = new IndexBuilder();
        b.Add(@"C:\Case\", "a.txt"); b.Add(@"C:\case\", "b.txt");
        var index = b.Build();
        Assert.Equal(@"C:\case\b.txt", index.GetRow(1).FullPath);
    }

    [Fact]
    public void FilteredResultsAndTiesMatchIndependentPathOracle()
    {
        var random = new Random(42);
        var b = new IndexBuilder();
        for (int i = 0; i < 4000; i++)
            b.Add($"C:\\dir{random.Next(30):D2}\\child{random.Next(5)}\\", $"file{random.Next(100):D3} hello world.txt", random.Next(4), random.Next(4));
        var index = b.Build();
        var all = Enumerable.Range(0, index.Count).Select(index.GetRow).ToArray();
        foreach (string query in new[] { "dir01 file", "child2\\file0", "\"hello world\"", "missing" })
            foreach (bool descending in new[] { false, true })
            {
                var terms = query.Trim('"').Split(query.StartsWith('"') ? '\0' : ' ');
                var matches = all.Where(r => terms.All(t => r.FullPath.Contains(t, StringComparison.OrdinalIgnoreCase)));
                var expected = descending
                    ? matches.OrderByDescending(r => r.FullPath, StringComparer.OrdinalIgnoreCase).ThenByDescending(r => r.Id)
                    : matches.OrderBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Id);
                var actual = index.Search(query, SortField.Path, descending, offset: 5, limit: 31);
                Assert.Equal(matches.LongCount(), actual.TotalCount);
                Assert.Equal(expected.Skip(5).Take(31).Select(r => r.Id), actual.Rows.Select(r => r.Id));
            }
    }

    [Fact]
    public void CancellationDuringSortIsObservedWithinBoundedComparisons()
    {
        using var source = new CancellationTokenSource();
        int comparisons = 0;
        var values = Enumerable.Range(0, 100_000).Reverse().ToArray();
        Assert.Throws<OperationCanceledException>(() => SortIndex.Sort(values, (a, b) =>
        {
            if (++comparisons == 100) source.Cancel();
            return a.CompareTo(b);
        }, source.Token));
        Assert.InRange(comparisons, 100, 4096);
    }

    [Fact]
    public void ParallelSortMatchesOracleAcrossPartitionBoundaries()
    {
        var values = Enumerable.Range(0, 1_100_003).Select(i => (int)((long)i * 7919 % 1_100_003)).ToArray();
        int[] sorted = SortIndex.Sort(values, (a, b) => a.CompareTo(b), default);
        Assert.Equal(Enumerable.Range(0, values.Length), sorted);
    }

    [Fact]
    public void ParallelSortPreservesCancellationException()
    {
        using var source = new CancellationTokenSource();
        var values = Enumerable.Range(0, 1_100_003).Reverse().ToArray();
        int calls = 0;
        Assert.Throws<OperationCanceledException>(() => SortIndex.Sort(values, (a, b) =>
        {
            if (Interlocked.Increment(ref calls) == 100) source.Cancel();
            return a.CompareTo(b);
        }, source.Token));
    }

    [Fact]
    public void SharedMergeBufferCanBeReusedAcrossColumns()
    {
        var scratch = new int[1_100_100];
        for (int column = 0; column < 2; column++)
        {
            var values = Enumerable.Range(0, 1_100_003).Reverse().ToArray();
            var result = SortIndex.Sort(values, (a, b) => a.CompareTo(b), default, scratch);
            Assert.Same(values, result);
            Assert.Equal(Enumerable.Range(0, values.Length), result);
        }
    }
}
