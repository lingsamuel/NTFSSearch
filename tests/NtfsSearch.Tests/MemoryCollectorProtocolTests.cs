using NtfsSearch.App;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class MemoryCollectorProtocolTests
{
    [Fact]
    public async Task ScanRoundTripRetainsNtfsNamesAndHardLinkIdentitiesAcrossFrames()
    {
        using var pipe = new MemoryStream();
        var sender = new MemoryScanWriter(pipe, CancellationToken.None);
        sender.Start(4);
        const string rawName = "raw\ud800.txt";
        sender.Add(@"E:\one\", rawName, 42, 7, false, 13, 11);
        sender.Add(@"E:\one\", "other.txt", null, null, false, 14, 11);
        sender.Finish();
        sender.Add(@"E:\two\", rawName, 42, 7, false, 13, 12);
        sender.Add(@"E:\one\", "replacement.txt", null, null, false, 15, 21);
        sender.Finish();
        pipe.Position = 0;

        var reader = new CollectorFrameReader(pipe);
        using var start = (await reader.ReadAsync()).Open();
        var records = new MemoryScanReader(start.ReadInt32());
        var first = await reader.ReadAsync();
        var second = await reader.ReadAsync();
        Assert.Equal(CollectorFrameKind.ScanRows, first.Kind);
        Assert.Equal(CollectorFrameKind.ScanRows, second.Kind);
        records.ReadRows(first, CancellationToken.None);
        records.ReadRows(second, CancellationToken.None);
        var index = records.Build(CancellationToken.None);

        Assert.Equal(4, index.Count);
        Assert.Contains(21UL, index.DirectoryReferences!);
        Assert.Equal(new[] { @"E:\one\" + rawName, @"E:\two\" + rawName },
            index.FindFileRows(13).Select(id => index.GetRow(id).FullPath).Order().ToArray());
        Assert.All(index.FindFileRows(13), id => Assert.Equal(42, index.GetRow(id).Size));
    }

    [Fact]
    public async Task IncompleteFrameCannotAdvanceJournal()
    {
        using var pipe = new MemoryStream([(byte)CollectorFrameKind.Changes, 8, 0, 0, 0, 7]);
        var reader = new CollectorFrameReader(pipe);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }
}
