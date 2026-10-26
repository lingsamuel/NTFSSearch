using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class NamePredicateCacheTests
{
    [Fact]
    public void MatchingWorkspaceKeepsOnlySameOrdinalPredicateAndNeverBlocksFallback()
    {
        var builder = new IndexBuilder();
        foreach (string name in new[] { "Readme.md", "readme.txt", "other.bin", "file.JPG", "孤\ud800" }) builder.Add(@"C:\", name);
        var index = builder.Build();
        Assert.Equal(2, index.Search("read").TotalCount);
        byte[] first = index.Names.TryRentWorkspace("read", false)!;
        Assert.NotNull(first); Assert.Contains(first, value => value != 0);
        var contents = first.ToArray();
        try
        {
            Assert.Null(index.Names.TryRentWorkspace("other", false));
            Assert.Equal(1, index.Search("other").TotalCount);
            Assert.Equal(contents, first);
        }
        finally { index.Names.ReturnWorkspace(); }
        var same = index.Names.TryRentWorkspace("read", false)!;
        Assert.Same(first, same); Assert.Equal(contents, same); index.Names.ReturnWorkspace();
        var changed = index.Names.TryRentWorkspace("Read", false)!;
        Assert.Same(first, changed); Assert.All(changed, value => Assert.Equal(0, value)); index.Names.ReturnWorkspace();
        Assert.Equal(2, index.Search(@"C:\Read").TotalCount);
        var prefix = index.Names.TryRentWorkspace("Read", true)!;
        Assert.Contains(prefix, value => value != 0); index.Names.ReturnWorkspace();
        var contains = index.Names.TryRentWorkspace("Read", false)!;
        Assert.All(contains, value => Assert.Equal(0, value)); index.Names.ReturnWorkspace();
        var view = index.WithView([1UL], [@"C:\read\"], default);
        Assert.Equal(4, view.Search("read").TotalCount);
        string[] queries = ["read", "other", "JPG", "孤\ud800", @"C:\r"];
        long[] totals = [2, 1, 1, 1, 2];
        Parallel.For(0, 1000, i => Assert.Equal(totals[i % 5], index.Search(queries[i % 5]).TotalCount));
    }

    [Fact]
    public void CancelledMatchingReturnsWorkspaceAndNextQueryRemainsExact()
    {
        var builder = new IndexBuilder(20000);
        string prefix = string.Concat(Enumerable.Repeat("αβγδ", 50));
        for (int i = 0; i < 20000; i++) builder.Add(@"Q:\", prefix + $"{i:D5}.txt");
        var index = builder.Build();
        Assert.Equal(0, index.Search("ωφψ", limit: 0).TotalCount);
        bool cancelled = false;
        for (int attempt = 0; attempt < 10 && !cancelled; attempt++)
        {
            var reset = index.Names.TryRentWorkspace("reset-" + attempt, false)!; index.Names.ReturnWorkspace();
            using var token = new CancellationTokenSource(); token.CancelAfter(1);
            try { index.Search("ωφψ", limit: 0, cancellationToken: token.Token); }
            catch (OperationCanceledException) { cancelled = true; }
        }
        Assert.True(cancelled);
        Assert.NotNull(index.Names.TryRentWorkspace("ωφψ", false)); index.Names.ReturnWorkspace();
        Assert.Equal(0, index.Search("ωφψ", limit: 0).TotalCount);
        Assert.Equal(20000, index.Search("β", limit: 0).TotalCount);
        Assert.Equal(0, index.Search(@"Q:\β", limit: 0).TotalCount);
    }
}
