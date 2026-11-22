using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class ExitSnapshotTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, $"exit-snapshot-{Guid.NewGuid():N}");
    private string SnapshotPath => Path.Combine(directory, ExitSnapshotStore.FileName);

    [Fact]
    public void RestoresOnlyVisibleRowsWithOriginalPathsMetadataAndIdentity()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "old", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\old\", "nested", isDirectory: true, fileReference: 11, parentReference: 10);
        builder.Add(@"C:\old\nested\", "报告\ud800.txt", 4_000_000_000, 0, fileReference: 12, parentReference: 11);
        builder.Add(@"C:\", "LINK.TXT", 4_000_000_000, 0, fileReference: 12, parentReference: 5);
        builder.Add(@"C:\", "deleted.txt", 9, 51, fileReference: 21, parentReference: 5);
        var live = new LiveVolumeIndex(builder.Build(), new(7, 100));
        live.Apply(new LiveTransaction(new(7, 100), new(7, 200),
            [new(10, true, [new(5, @"C:\", "moved")]), new(21, false, []), new(9, false, [new(5, @"C:\", "added.bin")], 1, 50)]));

        var volume = new NtfsVolume("volume-c", @"C:\", 123, "标签\ud800", ["disk-0", "disk-1"]);
        ExitSnapshotStore.Save(SnapshotPath, [new(volume, live.Position!, live.Layers)]);
        var restored = Assert.Single(ExitSnapshotStore.Load(SnapshotPath));
        var index = Assert.Single(restored.Layers);

        Assert.Equal(volume with { PhysicalDisks = null }, restored.Volume with { PhysicalDisks = null });
        Assert.Equal(volume.PhysicalDisks, restored.Volume.PhysicalDisks);
        Assert.Equal(live.Position, restored.Position);
        Assert.Equal(5, index.Count);
        Assert.True(index.HasIdentity);
        Assert.Equal(2, index.FindFileRows(12).Count);
        Assert.Equal(new ulong[] { 5, 11 }, index.FindFileRows(12).Select(id => index.DirectoryReferences![index.Records[id].Directory]).Order());

        var before = new SearchCatalog();
        before.PublishLayers(volume.Id, live.Layers);
        foreach (var sort in Enum.GetValues<SortField>())
            foreach (bool descending in new[] { false, true })
                Assert.Equal(before.Search("", sort, descending).Rows.Select(r => r with { Id = 0 }),
                    index.Search("", sort, descending).Rows.Select(r => r with { Id = 0 }));

        var resumed = new LiveVolumeIndex(index, restored.Position);
        resumed.Apply(new LiveTransaction(new(7, 200), new(7, 201), [new(11, true, [new(10, @"C:\moved\", "second")])]));
        var after = new SearchCatalog();
        after.PublishLayers(volume.Id, resumed.Layers);
        Assert.Equal("C:\\moved\\second\\报告\ud800.txt", Assert.Single(after.Search("报告").Rows).FullPath);
    }

    [Fact]
    public void ReportsLoadingAndSortingForEachRestoredVolume()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "first"), Volume("D", "second")]);
        var stages = new System.Collections.Concurrent.ConcurrentQueue<(string VolumeId, bool Sorting, int Rows)>();
        var restored = ExitSnapshotStore.LoadVolumes(SnapshotPath, default, 2,
            (volume, sorting, rows) => stages.Enqueue((volume.Id, sorting, rows))).ToArray();

        Assert.Equal(2, restored.Length);
        foreach (var volume in restored)
            Assert.Equal(new[] { (volume.Volume.Id, false, 1), (volume.Volume.Id, true, 1) },
                stages.Where(stage => stage.VolumeId == volume.Volume.Id));
    }

    [Fact]
    public void SkipsCorruptVolumeAndKeepsLaterIndependentVolume()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "healthy-before.txt"), Volume("D", "broken-middle-entry.txt"), Volume("E", "healthy-after.txt")]);
        var bytes = File.ReadAllBytes(SnapshotPath);
        var (offset, length) = CompressedBlock(bytes, 1);
        bytes[offset + length / 2] ^= 0x01;
        File.WriteAllBytes(SnapshotPath, bytes);

        Assert.Equal(new[] { "C", "E" }, ExitSnapshotStore.LoadVolumes(SnapshotPath, maxConcurrency: 2).Select(v => v.Volume.Id).Order());
    }

    [Fact]
    public void InvalidZstdFrameIsIsolatedEvenWithAValidStoredChecksum()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "first"), Volume("D", "second")]);
        byte[] bytes = File.ReadAllBytes(SnapshotPath);
        var (offset, length) = CompressedBlock(bytes, 0);
        bytes[offset] ^= 0x80;
        SHA256.HashData(bytes.AsSpan(offset, length), bytes.AsSpan(12 + 16, 32));
        int directoryEnd = 12 + 2 * 48;
        SHA256.HashData(bytes.AsSpan(0, directoryEnd), bytes.AsSpan(directoryEnd, 32));
        File.WriteAllBytes(SnapshotPath, bytes);

        Assert.Equal("D", Assert.Single(ExitSnapshotStore.LoadVolumes(SnapshotPath, maxConcurrency: 2)).Volume.Id);
    }

    [Fact]
    public void KeepsCompleteVolumesBeforeTruncatedPayload()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "first"), Volume("D", "second")]);
        using (var file = File.OpenWrite(SnapshotPath)) file.SetLength(file.Length - 1);

        Assert.Equal("C", Assert.Single(ExitSnapshotStore.Load(SnapshotPath)).Volume.Id);
    }

    [Fact]
    public void RejectsCorruptVolumeDirectory()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "first")]);
        using (var file = File.OpenWrite(SnapshotPath)) file.WriteByte(0);

        Assert.Throws<InvalidDataException>(() => ExitSnapshotStore.Load(SnapshotPath));
    }

    [Fact]
    public void CancellationPreservesCompleteSnapshotAndSuccessfulPublishKeepsOneFile()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "old")]);
        var previous = File.ReadAllBytes(SnapshotPath);
        using var cancellation = new CancellationTokenSource();
        var progress = new List<(int Completed, int Total)>();

        Assert.Throws<OperationCanceledException>(() => ExitSnapshotStore.Save(SnapshotPath,
            [Volume("C", "new"), Volume("D", "other")], cancellation.Token,
            (completed, total) => { progress.Add((completed, total)); cancellation.Cancel(); }));
        Assert.Equal(new[] { (1, 2) }, progress);
        Assert.Equal(previous, File.ReadAllBytes(SnapshotPath));
        Assert.Equal(SnapshotPath, Assert.Single(Directory.GetFiles(directory)));

        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "new")]);
        var current = Assert.Single(ExitSnapshotStore.Load(SnapshotPath));
        Assert.Equal("new", Assert.Single(Assert.Single(current.Layers).Search("").Rows).Name);
        Assert.Equal(SnapshotPath, Assert.Single(Directory.GetFiles(directory)));
    }

    [Fact]
    public void StoresACommonParentPathOnlyOnceAcrossLayers()
    {
        const string parent = @"C:\very-long-common-parent-directory\";
        var a = new IndexBuilder();
        var b = new IndexBuilder();
        a.Add(parent, "a.txt", fileReference: 10, parentReference: 99);
        a.Add(parent, "b.txt", fileReference: 11, parentReference: 99);
        b.Add(parent, "c.txt", fileReference: 12, parentReference: 99);
        ExitSnapshotStore.Save(SnapshotPath, [new(new("C", @"C:\", 0, ""), new(2, 5), [a.Build(), b.Build()])]);

        byte[] bytes = ReadPayload(File.ReadAllBytes(SnapshotPath));
        byte[] encodedParent = Encoding.ASCII.GetBytes(parent);
        int first = bytes.AsSpan().IndexOf(encodedParent);
        Assert.True(first >= 0);
        Assert.Equal(-1, bytes.AsSpan(first + encodedParent.Length).IndexOf(encodedParent));
    }

    [Fact]
    public void PreservesDistinctParentIdentitiesFromDifferentLayers()
    {
        const string parent = @"C:\existing-path\";
        var a = new IndexBuilder();
        var b = new IndexBuilder();
        a.Add(parent, "a.txt", fileReference: 10, parentReference: 99);
        b.Add(parent, "b.txt", fileReference: 11, parentReference: (1UL << 48) | 99);
        ExitSnapshotStore.Save(SnapshotPath, [new(new("C", @"C:\", 0, ""), new(2, 5), [a.Build(), b.Build()])]);

        var restored = Assert.Single(Assert.Single(ExitSnapshotStore.Load(SnapshotPath)).Layers);
        Assert.Equal(new ulong[] { 99, (1UL << 48) | 99 }, restored.DirectoryReferences);
        Assert.Equal(new[] { parent + "a.txt", parent + "b.txt" }, restored.Search("", SortField.Path).Rows.Select(r => r.FullPath));
    }

    [Fact]
    public void StoresAsciiNamesAndSparseMetadataCompactlyWithoutLosingTheirValues()
    {
        const int rows = 1000;
        const ulong sequence = 47UL << 48;
        var builder = new IndexBuilder(rows);
        for (ulong i = 0; i < rows; i++)
            builder.Add(@"C:\", "ShortASCII.TXT", i % 2 == 0 ? null : 1200, null,
                fileReference: sequence | (i + 100), parentReference: 5);
        ExitSnapshotStore.Save(SnapshotPath, [new(new("C", @"C:\", 0, ""), new(2, 5), [builder.Build()])]);

        byte[] bytes = File.ReadAllBytes(SnapshotPath);
        Assert.InRange(ReadPayload(bytes).Length, 19000, 23000);
        Assert.True(bytes.Length < 5000, $"压缩快照长度为 {bytes.Length} 字节。");
        var restored = Assert.Single(Assert.Single(ExitSnapshotStore.Load(SnapshotPath)).Layers);
        Assert.Equal(rows, restored.Count);
        Assert.Null(restored.GetRow(Assert.Single(restored.FindFileRows(sequence | 100))).Size);
        var known = restored.GetRow(Assert.Single(restored.FindFileRows(sequence | 101)));
        Assert.Equal("ShortASCII.TXT", known.Name);
        Assert.Equal(1200, known.Size);
        Assert.Null(known.ModifiedUtcTicks);
    }

    [Fact]
    public void ParallelRestoreKeepsReadingItsOriginalSnapshotAfterReplacement()
    {
        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "previous-c"), Volume("D", "previous-d"), Volume("E", "previous-e")]);
        var names = new List<string>();
        using var views = ExitSnapshotStore.LoadVolumes(SnapshotPath, maxConcurrency: 2).GetEnumerator();
        Assert.True(views.MoveNext());
        names.Add(Assert.Single(views.Current.Layers[0].Search("").Rows).Name);

        ExitSnapshotStore.Save(SnapshotPath, [Volume("C", "current")]);
        while (views.MoveNext()) names.Add(Assert.Single(views.Current.Layers[0].Search("").Rows).Name);

        Assert.Equal(new[] { "previous-c", "previous-d", "previous-e" }, names.Order());
        Assert.Equal("current", Assert.Single(Assert.Single(ExitSnapshotStore.Load(SnapshotPath)).Layers[0].Search("").Rows).Name);
    }

    private static (int Offset, int Length) CompressedBlock(byte[] file, int volume)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(8));
        int offset = 12 + count * 48 + 32;
        for (int index = 0; index < volume; index++)
            offset += checked((int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(12 + index * 48)));
        int length = checked((int)BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(12 + volume * 48)));
        return (offset, length);
    }

    private static byte[] ReadPayload(byte[] bytes)
    {
        var (offset, length) = CompressedBlock(bytes, 0);
        using var input = new MemoryStream(bytes, offset, length);
        using var decompressed = new ZstdSharp.DecompressionStream(input);
        using var output = new MemoryStream();
        decompressed.CopyTo(output);
        return output.ToArray();
    }

    private static ExitSnapshotVolume Volume(string id, string name)
    {
        string root = $"{id}:\\";
        var builder = new IndexBuilder();
        builder.Add(root, name, fileReference: 10, parentReference: 5);
        return new(new(id, root, 0, ""), new(7, 100), [builder.Build()]);
    }

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
