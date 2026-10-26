using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class DirectoryResolverTests
{
    [Fact]
    public void NewParentsAreResolvedOnceAndUnreachableRecordsDoNotFailVolume()
    {
        var entries = new Dictionary<ulong, (ulong Parent, string Name)>
        { [11] = (5, "known"), [12] = (99, "child"), [13] = (13, "cycle") };
        int calls = 0;
        var resolver = new DirectoryPathResolver('C', entries, id =>
        { calls++; return id == 99 ? @"C:\created\" : null; });
        Assert.Equal(@"C:\known\", resolver.Resolve(11));
        Assert.Equal(@"C:\created\child\", resolver.Resolve(12));
        Assert.Equal(@"C:\created\child\", resolver.Resolve(12));
        Assert.Equal(1, calls);
        Assert.Null(resolver.Resolve(0)); Assert.Null(resolver.Resolve(0));
        Assert.Equal(2, calls);
        Assert.Null(resolver.Resolve(13));
    }
}
