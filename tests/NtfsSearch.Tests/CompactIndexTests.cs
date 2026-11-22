using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class CompactIndexTests
{
    [Fact]
    public void CurrentFormatProbeDistinguishesLegacyWithoutLoadingSnapshot()
    {
        string path = Path.Combine(Path.GetTempPath(), $"current-format-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder(); builder.Add("C:\\工作\\odd\ud800\\", "name\ud800");
            var index = builder.Build();
            SnapshotStore.Save(index, path);
            Assert.True(SnapshotStore.IsCurrentFormat(path));
            Assert.Equal(index.GetRow(0), SnapshotStore.Load(path).GetRow(0));
            LegacySnapshotFixture.Write(index, path);
            Assert.False(SnapshotStore.IsCurrentFormat(path));
            File.WriteAllText(path, "NTS");
            Assert.False(SnapshotStore.IsCurrentFormat(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RecordsUse8BytesAndKeepFullMetadataRange()
    {
        Assert.Equal(8, Marshal.SizeOf<IndexRecord>());
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "known", long.MaxValue, long.MinValue);
        builder.Add(@"C:\", "unknown");
        builder.Add(@"C:\", "directory", long.MaxValue, long.MaxValue, true);
        var index = builder.Build();
        Assert.Equal(long.MaxValue, index.GetRow(0).Size);
        Assert.Equal(long.MinValue, index.GetRow(0).ModifiedUtcTicks);
        Assert.Null(index.GetRow(1).Size);
        Assert.Null(index.GetRow(1).ModifiedUtcTicks);
        Assert.True(index.GetRow(2).IsDirectory);
        Assert.Null(index.GetRow(2).Size);
        Assert.Equal(long.MaxValue, index.GetRow(2).ModifiedUtcTicks);
        var boundary = new IndexRecord(int.MaxValue, int.MaxValue, false, true);
        Assert.Equal(int.MaxValue, boundary.NameId);
        Assert.Equal(int.MaxValue, boundary.Directory);
        Assert.True(boundary.HasModified);
        Assert.False(boundary.IsDirectory);
        var directory = new IndexRecord(int.MaxValue, int.MaxValue, true, false);
        Assert.True(directory.IsDirectory);
        Assert.False(directory.HasModified);
        Assert.Equal(int.MaxValue, directory.Directory);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(1, 12)]
    [InlineData(2, 16)]
    [InlineData(3, 20)]
    [InlineData(4, 16)]
    [InlineData(5, 20)]
    [InlineData(6, 24)]
    [InlineData(7, 28)]
    public void EnabledMetadataControlsPayloadAndValuesAfterIdentitySorting(int flags, int rowBytes)
    {
        var metadata = (IndexMetadata)flags;
        var builder = new IndexBuilder(1, fixedSize: false, metadata);
        builder.Add(@"C:\", "unknown", fileReference: 25, parentReference: 5);
        builder.Add(@"C:\", "large", long.MaxValue, long.MinValue, fileReference: 20, parentReference: 5, createdUtcTicks: long.MaxValue);
        builder.Add(@"C:\", "zero", 0, 0, fileReference: 10, parentReference: 5, createdUtcTicks: long.MinValue);
        var index = builder.Build();
        bool sizeEnabled = metadata.HasFlag(IndexMetadata.FileSize);
        bool modifiedEnabled = metadata.HasFlag(IndexMetadata.ModifiedTime);
        bool creationEnabled = metadata.HasFlag(IndexMetadata.CreationTime);

        Assert.Equal(metadata, index.Metadata);
        Assert.Equal(sizeEnabled, index.SizeCodes is not null);
        Assert.Equal(sizeEnabled, index.BigSizes.Length != 0);
        Assert.Equal(modifiedEnabled, index.ModifiedTimes is not null);
        Assert.Equal(creationEnabled, index.CreationTimes is not null);
        Assert.Equal(creationEnabled, index.KnownCreationTimes is not null);
        Assert.Equal(rowBytes * 3 + (sizeEnabled ? sizeof(long) : 0) + (creationEnabled ? sizeof(ulong) : 0), index.Statistics.RecordBytes);
        Assert.Equal(new[] { "zero", "large", "unknown" }, Enumerable.Range(0, index.Count).Select(id => index.GetRow(id).Name));
        Assert.Equal(sizeEnabled ? 0L : null, index.GetRow(0).Size);
        Assert.Equal(modifiedEnabled ? 0L : null, index.GetRow(0).ModifiedUtcTicks);
        Assert.Equal(creationEnabled ? long.MinValue : null, index.GetRow(0).CreatedUtcTicks);
        Assert.Equal(sizeEnabled ? long.MaxValue : null, index.GetRow(1).Size);
        Assert.Equal(modifiedEnabled ? long.MinValue : null, index.GetRow(1).ModifiedUtcTicks);
        Assert.Equal(creationEnabled ? long.MaxValue : null, index.GetRow(1).CreatedUtcTicks);
        Assert.Null(index.GetRow(2).Size);
        Assert.Null(index.GetRow(2).ModifiedUtcTicks);
        Assert.Null(index.GetRow(2).CreatedUtcTicks);
        Assert.Equal(sizeEnabled ? 1 : 0, index.Search("", filter: new(MaxSizeBytes: 0)).TotalCount);
        Assert.Equal(modifiedEnabled ? 1 : 0, index.Search("", filter: new(ModifiedFromUtcTicks: 0)).TotalCount);
        Assert.Equal(creationEnabled ? 1 : 0, index.Search("", filter: new(CreatedFromUtcTicks: 0)).TotalCount);
    }

    [Fact]
    public void CreationColumnFollowsIdentitySortingAndSurvivesSnapshots()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"compact-creation-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            builder.Add(@"C:\", "file", fileReference: 20, parentReference: 5, createdUtcTicks: long.MaxValue);
            builder.Add(@"C:\", "folder", isDirectory: true, fileReference: 10, parentReference: 5, createdUtcTicks: long.MinValue);
            builder.Add(@"C:\", "missing", fileReference: 15, parentReference: 5);
            builder.Add(@"C:\", "zero", fileReference: 25, parentReference: 5, createdUtcTicks: 0);
            var index = builder.Build();
            Assert.Equal(new ulong[] { 10, 15, 20, 25 }, index.FileReferences);
            long?[] expected = [long.MinValue, null, long.MaxValue, 0];
            Assert.Equal(expected, Enumerable.Range(0, index.Count).Select(id => index.GetRow(id).CreatedUtcTicks));

            SnapshotStore.Save(index, path);
            Assert.True(SnapshotStore.IsCurrentFormat(path));
            SnapshotStore.ValidateChecksum(path);
            var restored = SnapshotStore.Load(path);
            Assert.Equal(expected, Enumerable.Range(0, restored.Count).Select(id => restored.GetRow(id).CreatedUtcTicks));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SmallAndOverflowSizesSurviveSnapshotsAndSortTogether()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compact-sizes-{Guid.NewGuid():N}.ntidx");
        try
        {
            long?[] sizes = [null, 0, int.MaxValue, (long)int.MaxValue + 1, uint.MaxValue, long.MaxValue];
            var builder = new IndexBuilder();
            for (int i = 0; i < sizes.Length; i++) builder.Add(@"C:\", $"name{i}", sizes[i], long.MinValue + i);
            var index = builder.Build();
            Assert.Equal(20L * sizes.Length + 3 * sizeof(long), index.Statistics.RecordBytes);
            Assert.Equal(new[] { (long)int.MaxValue + 1, uint.MaxValue, long.MaxValue }, index.BigSizes);
            SnapshotStore.Save(index, path);
            var restored = SnapshotStore.Load(path);
            for (int i = 0; i < sizes.Length; i++) Assert.Equal(sizes[i], restored.GetRow(i).Size);
            Assert.Equal(sizes.Skip(1).Append(null), restored.Search("", SortField.Size).Rows.Select(row => row.Size));
            Assert.Equal(sizes.Skip(1).Reverse().Append(null), restored.Search("", SortField.Size, true).Rows.Select(row => row.Size));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaticSnapshotRetainsPackedRowsAndOnlyLoadsRequestedColumns(bool includeCreation)
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"metadata-static-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            builder.Add(@"C:\", "z", long.MaxValue, long.MinValue, createdUtcTicks: includeCreation ? 7 : null);
            builder.Add(@"C:\", "a", 0, 0);
            var original = builder.Build();
            SnapshotStore.Save(original, path);
            byte[] bytes = File.ReadAllBytes(path);
            Assert.Equal((includeCreation ? "NTSIDX05"u8 : "NTSIDX04"u8).ToArray(), bytes[..8]);
            int rowStart = 40 + sizeof(int) + @"C:\".Length * sizeof(char)
                + original.Names.Descriptors.Length * sizeof(ulong)
                + original.Names.Chunks.Sum(chunk => sizeof(int) + chunk.Length * sizeof(char))
                + original.Names.AsciiChunks.Sum(chunk => sizeof(int) + chunk.Length)
                + original.BigSizes.Length * sizeof(long);
            var first = bytes.AsSpan(rowStart, 20);
            Assert.Equal(long.MinValue, BinaryPrimitives.ReadInt64LittleEndian(first));
            Assert.Equal(0x80000000U, BinaryPrimitives.ReadUInt32LittleEndian(first[8..]));
            Assert.Equal(0x80000000U, BinaryPrimitives.ReadUInt32LittleEndian(first[12..]));
            Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(first[16..]));
            Assert.Equal(original.Search("").Rows, SnapshotStore.Load(path).Search("").Rows);

            var projected = SnapshotStore.Load(path, IndexMetadata.ModifiedTime);
            Assert.Equal(2 * 16, projected.Statistics.RecordBytes);
            Assert.Equal(new long?[] { long.MinValue, 0 }, projected.Search("", SortField.Modified).Rows.Select(row => row.ModifiedUtcTicks));
            Assert.Null(projected.SizeCodes);
            Assert.Empty(projected.BigSizes);
            Assert.Null(projected.CreationTimes);
            Assert.Null(projected.KnownCreationTimes);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidNameReferencesAreRejectedEvenWithCorrectChecksum(bool legacy)
    {
        string path = Path.Combine(Path.GetTempPath(), $"compact-invalid-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder(); builder.Add(@"C:\", "one");
            if (legacy) LegacySnapshotFixture.Write(builder.Build(), path);
            else SnapshotStore.Save(builder.Build(), path);
            byte[] data = File.ReadAllBytes(path);
            // 单一父路径后是旧名称块，或V4的名称描述符；两者偏移都指向名称引用。
            int directoryEnd = (legacy ? 24 : 40) + 4 + 3 * 2;
            int reference = legacy ? directoryEnd + 4 + 3 * 2 : directoryEnd;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(reference), ulong.MaxValue);
            SHA256.HashData(data.AsSpan(0, data.Length - 32), data.AsSpan(data.Length - 32));
            File.WriteAllBytes(path, data);
            Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void SavingChangedViewDoesNotClaimOriginalLegacyFingerprint()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compact-view-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder(); builder.Add(@"C:\", "one", fileReference: 10, parentReference: 5);
            LegacySnapshotFixture.Write(builder.Build(), path);
            var original = SnapshotStore.Load(path);
            Assert.NotNull(original.CheckpointFingerprint);
            var changed = original.WithView(null, [@"D:\"], default);
            SnapshotStore.Save(changed, path);
            var restored = SnapshotStore.Load(path);
            Assert.Equal(@"D:\one", restored.GetRow(0).FullPath);
            Assert.Null(restored.CheckpointFingerprint);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }

    [Fact]
    public void NamesAreDeduplicatedWithExactUtf16Identity()
    {
        var builder = new IndexBuilder();
        string name = new('x', 1024);
        for (int i = 0; i < 2048; i++) builder.Add(@"C:\", name);
        builder.Add(@"C:\", "Case\ud800");
        builder.Add(@"C:\", "case\ud800");
        builder.Add(@"C:\", "Case\ud801");
        var index = builder.Build();
        Assert.True(index.Statistics.NameBytes < 4096);
        Assert.Equal("Case\ud800", index.GetRow(2048).Name);
        Assert.Equal("case\ud800", index.GetRow(2049).Name);
        Assert.Equal("Case\ud801", index.GetRow(2050).Name);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RealLegacySnapshotsConvertToCompactNamesAndV4(int version)
    {
        string path = Path.Combine(Path.GetTempPath(), $"compact-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            for (int i = 0; i < 128; i++)
                builder.Add(@"C:\", "repeat\ud800" + new string('x', 1024), i == 0 ? null : long.MaxValue,
                    i == 0 ? null : long.MinValue, fileReference: (ulong)i + 10, parentReference: 5);
            var original = builder.Build();
            LegacySnapshotFixture.Write(original, path, version);
            var loaded = SnapshotStore.Load(path);
            Assert.True(loaded.Statistics.NameBytes < 4096);
            Assert.Equal(version == 3, loaded.HasIdentity);
            foreach (SortField field in Enum.GetValues<SortField>())
                foreach (bool descending in new[] { false, true })
                    Assert.Equal(original.Search("repeat", field, descending).Rows, loaded.Search("repeat", field, descending).Rows);
            SnapshotStore.Save(loaded, path);
            Assert.Equal("NTSIDX04"u8.ToArray(), File.ReadAllBytes(path)[..8]);
            var restored = SnapshotStore.Load(path);
            Assert.Equal(loaded.Search("").Rows, restored.Search("").Rows);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }
}

internal static class LegacySnapshotFixture
{
    internal static void Write(SearchIndex index, string path, int version = 3)
    {
        using var content = new MemoryStream();
        using var writer = new BinaryWriter(content, System.Text.Encoding.UTF8, true);
        writer.Write(System.Text.Encoding.ASCII.GetBytes($"NTSIDX0{version}"));
        writer.Write(index.Count); writer.Write(index.Directories.Length);
        int chars = Enumerable.Range(0, index.Count).Sum(i => index.GetRow(i).Name.Length);
        if (chars > NameArena.ChunkChars) throw new InvalidOperationException("测试fixture仅支持单块。");
        writer.Write(chars == 0 ? 0 : 1); writer.Write(chars);
        foreach (string directory in index.Directories)
        { writer.Write(directory.Length); content.Write(MemoryMarshal.AsBytes(directory.AsSpan())); }
        if (chars > 0)
        {
            writer.Write(chars);
            for (int i = 0; i < index.Count; i++) content.Write(MemoryMarshal.AsBytes(index.GetRow(i).Name.AsSpan()));
        }
        long offset = 0;
        for (int i = 0; i < index.Count; i++)
        {
            var row = index.GetRow(i);
            writer.Write(offset); writer.Write(row.Size.GetValueOrDefault()); writer.Write(row.ModifiedUtcTicks.GetValueOrDefault());
            writer.Write(index.Records[i].Directory); writer.Write((ushort)row.Name.Length);
            writer.Write((ushort)((row.IsDirectory ? 1 : 0) | (row.Size.HasValue ? 2 : 0) | (row.ModifiedUtcTicks.HasValue ? 4 : 0)));
            offset += row.Name.Length;
        }
        if (version >= 2)
            foreach (SortField field in Enum.GetValues<SortField>())
            {
                writer.Write(-1); writer.Write(index.KnownCount(field));
                for (int position = 0; position < index.Count; position++) writer.Write(index.SortOrder(field)[position]);
            }
        if (version == 3)
        {
            writer.Write(index.HasIdentity);
            if (index.HasIdentity)
            {
                content.Write(MemoryMarshal.AsBytes(index.FileReferences!.AsSpan()));
                content.Write(MemoryMarshal.AsBytes(index.DirectoryReferences!.AsSpan()));
            }
        }
        byte[] payload = content.ToArray();
        using var file = File.Create(path); file.Write(payload); file.Write(SHA256.HashData(payload));
    }
}
