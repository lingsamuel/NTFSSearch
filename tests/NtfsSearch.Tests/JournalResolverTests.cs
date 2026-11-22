using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class JournalResolverTests
{
    private static readonly string Data = Path.Combine(Path.GetTempPath(), "resolver-data");
    private static readonly string Internal = Path.GetFullPath(Data).Replace('/', '\\').TrimEnd('\\') + '\\';
    private static JournalBatch Batch(long from, ulong reference, params (ulong Parent, uint Reason)[] records)
        => new(new(1, from), new(1, from + 1), records.Select(r => new JournalRecord(reference, r.Parent, from, r.Reason, 16, "name")).ToArray());

    [Fact]
    public void MovingPublicObjectIntoDataPublishesEmptyLinks()
    {
        var resolver = new JournalChangeResolver(Data, id => new(id, true, [new(99, Internal, "moved")]));
        resolver.MarkInternal(99);
        var result = resolver.Resolve(Batch(0, 10, (5, 0x1000), (99, 0x2000)), new(1, 0));
        Assert.Empty(Assert.Single(result.Files).Links);
    }

    [Fact]
    public void InternalCreationWritesAndAtomicRenamesDoNotCreateFeedbackTransactions()
    {
        int reads = 0;
        var resolver = new JournalChangeResolver(Data, id => { reads++; return new(id, false, [new(99, Internal, "temporary")]); });
        resolver.MarkInternal(99);
        Assert.Empty(resolver.Resolve(Batch(0, 10, (99, 0x100)), new(1, 0)).Files);
        Assert.Empty(resolver.Resolve(Batch(1, 10, (99, 0x1000), (99, 0x2000)), new(1, 1)).Files);
        Assert.Empty(resolver.Resolve(Batch(2, 10, (99, 0x3)), new(1, 2)).Files);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void PortableFilesStayOutOfJournalWhileOtherFilesInSameDirectoryRemainSearchable()
    {
        const ulong file = 10, parent = 99;
        const string staging = "settings.json.0123456789abcdef0123456789abcdef.tmp";
        string name = DesktopSettings.FileName;
        var resolver = new JournalChangeResolver(Data, id => new(id, false, [new(parent, Internal, name)]), parent);

        var atomicRename = new JournalBatch(new(1, 0), new(1, 1),
            [new(file, parent, 0, 0x1000, 0, staging), new(file, parent, 0, 0x2000, 0, name)]);
        Assert.Empty(resolver.Resolve(atomicRename, new(1, 0)).Files);
        Assert.Empty(resolver.FilterState(new(file, false, [new(parent, Internal, staging)])).Links);

        name = "notes.txt";
        var visible = resolver.Resolve(Batch(1, file, (parent, 0x2000)), new(1, 1));
        Assert.Equal(name, Assert.Single(Assert.Single(visible.Files).Links).Name);
        Assert.Single(resolver.FilterState(new(file, false, [new(parent, Internal, name)])).Links);
    }

    [Fact]
    public void LastExternalHardLinkRemovalPublishesDeleteAndStillObservesExternalMetadata()
    {
        bool hybrid = true; int reads = 0;
        var resolver = new JournalChangeResolver(Data, id =>
        {
            reads++; return new(id, false, hybrid ? [new(99, Internal, "inside"), new(5, @"C:\", "outside")] : [new(99, Internal, "inside")], 123);
        });
        resolver.MarkInternal(99);
        Assert.Single(resolver.Resolve(Batch(0, 10, (99, 0x10000)), new(1, 0)).Files);
        Assert.Single(resolver.Resolve(Batch(1, 10, (5, 0x3)), new(1, 1)).Files);
        hybrid = false;
        Assert.Empty(Assert.Single(resolver.Resolve(Batch(2, 10, (99, 0x10000)), new(1, 2)).Files).Links);
        Assert.Empty(resolver.Resolve(Batch(3, 10, (99, 0x3)), new(1, 3)).Files);
        Assert.Equal(3, reads);
    }

    [Fact]
    public void RenameSplitAcrossBatchesRetainsExternalOrigin()
    {
        bool outside = true;
        var resolver = new JournalChangeResolver(Data, id => new(id, true,
            outside ? [new(5, @"C:\", "before")] : [new(99, Internal, "after")]));
        resolver.MarkInternal(99);
        Assert.Single(resolver.Resolve(Batch(0, 10, (5, 0x1000)), new(1, 0)).Files);
        outside = false;
        Assert.Empty(Assert.Single(resolver.Resolve(Batch(1, 10, (99, 0x2000)), new(1, 1)).Files).Links);
    }
}
