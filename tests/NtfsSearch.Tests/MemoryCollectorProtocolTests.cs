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
        sender.Add(@"E:\one\", rawName, 42, 7, false, 13, 11, 5);
        sender.Add(@"E:\one\", "other.txt", null, null, false, 14, 11);
        sender.Finish();
        sender.Add(@"E:\two\", rawName, 42, 7, false, 13, 12, 5);
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
        Assert.All(index.FindFileRows(13), id =>
        {
            var row = index.GetRow(id);
            Assert.Equal(42, row.Size);
            Assert.Equal(5, row.CreatedUtcTicks);
        });
        Assert.Null(index.GetRow(Assert.Single(index.FindFileRows(14))).CreatedUtcTicks);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CollectorRequestPreservesMetadataForInitialAndResumedCollection(bool scan)
    {
        using var pipe = new MemoryStream();
        var request = new MemoryCollectorRequest(new("volume-id", @"E:\", 0, ""), "Data", "journal",
            scan ? "scan" : null, scan ? null : new(19, 240), scan ? 3 : 0, IndexMetadata.ModifiedTime);
        await new CollectorFrameWriter(pipe).WriteAsync(CollectorFrameKind.OpenVolume, request.Write, CancellationToken.None);
        pipe.Position = 0;

        var decoded = MemoryCollectorRequest.Read(await new CollectorFrameReader(pipe).ReadAsync());
        Assert.Equal(request, decoded);
    }

    [Theory]
    [InlineData((int)IndexMetadata.None)]
    [InlineData((int)IndexMetadata.FileSize)]
    [InlineData((int)IndexMetadata.ModifiedTime)]
    [InlineData((int)IndexMetadata.CreationTime)]
    public async Task ScanSendsOnlyRequestedMetadata(int fields)
    {
        var metadata = (IndexMetadata)fields;
        using var pipe = new MemoryStream();
        var sender = new MemoryScanWriter(pipe, CancellationToken.None, metadata);
        sender.Add(@"E:\one\", "name.txt", 42, 7, false, 13, 11, 5);
        sender.Finish();
        pipe.Position = 0;

        // 让接收方保留所有已收到的列，才能验证发出的数据确实没有禁用字段。
        var receiver = new MemoryScanReader(1);
        receiver.ReadRows(await new CollectorFrameReader(pipe).ReadAsync(), CancellationToken.None);
        var index = receiver.Build(CancellationToken.None);
        var row = index.GetRow(Assert.Single(index.FindFileRows(13)));
        Assert.Equal(@"E:\one\name.txt", row.FullPath);
        Assert.Equal(11UL, Assert.Single(index.DirectoryReferences!));
        Assert.Equal(metadata.HasFlag(IndexMetadata.FileSize) ? 42L : (long?)null, row.Size);
        Assert.Equal(metadata.HasFlag(IndexMetadata.ModifiedTime) ? 7L : (long?)null, row.ModifiedUtcTicks);
        Assert.Equal(metadata.HasFlag(IndexMetadata.CreationTime) ? 5L : (long?)null, row.CreatedUtcTicks);
    }

    [Theory]
    [InlineData((int)IndexMetadata.None)]
    [InlineData((int)IndexMetadata.FileSize)]
    [InlineData((int)IndexMetadata.ModifiedTime)]
    [InlineData((int)IndexMetadata.CreationTime)]
    [InlineData((int)IndexMetadata.All)]
    public void JournalEncodingOmitsDisabledValuesAndPreservesUsnAndHardLinks(int fields)
    {
        var metadata = (IndexMetadata)fields;
        IndexedLink[] links = [new(11, @"E:\one\", "raw\ud800.txt"), new(12, @"E:\two\", "link.txt")];
        var start = new JournalPosition(19, 120);
        var end = new JournalPosition(19, 240);
        var change = new CommittedChange(4, new(start, end, [new(13, false, links, 42, 7, 5)]));

        var decoded = ChangeLogStore.Decode(ChangeLogStore.Encode(change, metadata));
        Assert.Equal(change.Sequence, decoded.Sequence);
        Assert.Equal(start, decoded.Transaction.From);
        Assert.Equal(end, decoded.Transaction.To);
        var file = Assert.Single(decoded.Transaction.Files);
        Assert.Equal(13UL, file.FileReference);
        Assert.False(file.IsDirectory);
        Assert.Equal(links, file.Links);
        Assert.Equal(metadata.HasFlag(IndexMetadata.FileSize) ? 42L : (long?)null, file.Size);
        Assert.Equal(metadata.HasFlag(IndexMetadata.ModifiedTime) ? 7L : (long?)null, file.ModifiedUtcTicks);
        Assert.Equal(metadata.HasFlag(IndexMetadata.CreationTime) ? 5L : (long?)null, file.CreatedUtcTicks);
    }

    [Fact]
    public async Task IncompleteFrameCannotAdvanceJournal()
    {
        using var pipe = new MemoryStream([(byte)CollectorFrameKind.Changes, 8, 0, 0, 0, 7]);
        var reader = new CollectorFrameReader(pipe);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReadAsync());
    }
}
