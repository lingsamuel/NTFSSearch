using System.Buffers.Binary;
using System.Runtime.InteropServices;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class NativeJournalTests
{
    [Fact]
    public void JournalRecordsPreserveGenerationReasonAndUtf16()
    {
        byte[] buffer = Record(0x1234000000004567, 0xabcd000000000005, 123, 0x200, "链\ud800.txt");
        var records = UsnParser.ReadJournal(buffer);
        var record = Assert.Single(records);
        Assert.Equal(0x1234000000004567UL, record.FileReference);
        Assert.Equal(0xabcd000000000005UL, record.ParentReference);
        Assert.Equal(123, record.Usn);
        Assert.Equal(0x200U, record.Reason);
        Assert.Equal("链\ud800.txt", record.Name);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8 + 58), 59);
        Assert.Throws<InvalidDataException>(() => UsnParser.ReadJournal(buffer));
    }

    [Fact]
    public void ReadRequestPinsVersionTwoAndSupportsNativeSizes()
    {
        foreach (int size in new[] { 40, 44, 48 })
        {
            var request = NativeJournal.BuildReadRequest(new(99, 123), size);
            Assert.Equal(size, request.Length);
            Assert.Equal(123, BinaryPrimitives.ReadInt64LittleEndian(request));
            Assert.Equal(99UL, BinaryPrimitives.ReadUInt64LittleEndian(request.AsSpan(32)));
            if (size >= 44) Assert.Equal(0x00020002U, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(40)));
        }
    }

    [Fact]
    public void HardLinksUseCharacterLengthAndNativeEightByteAlignment()
    {
        const string name = "硬\ud800.txt";
        byte[] buffer = new byte[8 + 20 + name.Length * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)buffer.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(16), 0x5678000000000012);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), name.Length);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(buffer.AsSpan(28));
        var link = Assert.Single(NativeFileState.ParseLinks(buffer));
        Assert.Equal(0x5678000000000012UL, link.ParentReference);
        Assert.Equal(name, link.Name);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(24), int.MaxValue);
        Assert.Throws<InvalidDataException>(() => NativeFileState.ParseLinks(buffer));
    }

    [Fact]
    public void MftFixupRejectsTornSectorAndPreservesSequence()
    {
        byte[] record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(48), 0xabcd);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(510), 0xabcd);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(1022), 0xabcd);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(56), uint.MaxValue);
        var metadata = MftMetadata.Parse(record, 77);
        Assert.Equal(0x123400000000004dUL, metadata!.Value.FileReference);
        Assert.Equal(3, metadata.Value.LinkCount);
        record[510] = 0;
        Assert.Throws<InvalidDataException>(() => MftMetadata.Parse(record, 77));
    }

    [Fact]
    public void RejectsTruncatedResidentDataAndInvalidJournalRanges()
    {
        var info = new JournalInfo(7, 100, 300, 50);
        Assert.Throws<JournalInvalidatedException>(() => info.Validate(new(8, 200)));
        Assert.Throws<JournalInvalidatedException>(() => info.Validate(new(7, 99)));
        info.Validate(new(7, 100));
        info.Validate(new(7, 300));
        var data = new byte[1024];
        "FILE"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(20), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(24), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 0x80);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(60), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), 100);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(76), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(80), uint.MaxValue);
        Assert.Throws<InvalidDataException>(() => MftMetadata.Parse(data, 42));
    }

    [Theory]
    [InlineData(@"H:\app\Data", @"H:\app\", "Data", true)]
    [InlineData(@"H:\app\Data", @"H:\app\Data\", "snapshot.bin", true)]
    [InlineData(@"H:\app\Data", @"H:\app\Database\", "a", false)]
    [InlineData(@"H:\app\Data", @"C:\app\Data\", "a", false)]
    [InlineData(@"H:\app\Data", @"H:\app\", "data", true)]
    public void ExclusionMatchesWholeDirectoryAndDescendants(string excluded, string parent, string name, bool expected)
    {
        Assert.Equal(expected, NativePathExclusion.Contains(excluded, parent, name));
    }

    [Fact]
    public void SubtreeWalkStreamsDirectoriesFirstAndDeduplicatesFullReferences()
    {
        var root = new NativeSubtree.Node("root", 0x1000000000005, true, false);
        var directory = new NativeSubtree.Node("directory", 0x1000000000006, true, false);
        var file = new NativeSubtree.Node("file", 0x1000000000007, false, false);
        var link = new NativeSubtree.Node("link", file.FileReference, false, false);
        var reused = new NativeSubtree.Node("reused", 0x2000000000007, false, false);
        var junction = new NativeSubtree.Node("junction", 0x1000000000008, true, true);
        int visitedChildren = 0;
        IEnumerable<NativeSubtree.Node> Children(NativeSubtree.Node node)
        {
            visitedChildren++;
            if (node == root) return new[] { directory, link, reused, junction };
            if (node == directory) return new[] { file };
            throw new InvalidOperationException("不得递归文件或reparse目录。");
        }
        var result = NativeSubtree.Walk(root, Children,
            node => new(node.FileReference, node.IsDirectory, [new(5, @"C:\\", node.Path)]), default);
        using var enumerator = result.GetEnumerator();
        Assert.True(enumerator.MoveNext()); Assert.Equal(root.FileReference, enumerator.Current.FileReference);
        Assert.Equal(0, visitedChildren);
        var references = new List<ulong> { enumerator.Current.FileReference };
        while (enumerator.MoveNext()) references.Add(enumerator.Current.FileReference);
        Assert.Equal(new[] { root.FileReference, directory.FileReference, file.FileReference, reused.FileReference, junction.FileReference }, references);
        Assert.Equal(2, visitedChildren);
    }

    [Fact]
    public void InvalidOpenIsDeletionOnlyAfterSlotGenerationOrLinkCountConfirmsAbsence()
    {
        const ulong requested = 0x1234000000000042;
        byte[] buffer = new byte[12 + 1024];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0x42);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(8), 1024);
        "FILE"u8.CopyTo(buffer.AsSpan(12));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(28), 0x1234);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(34), 1);
        Assert.False(MftReferenceProbe.ConfirmsMissingReference(buffer, requested));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(30), 0);
        Assert.True(MftReferenceProbe.ConfirmsMissingReference(buffer, requested));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(30), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(28), 0x1235);
        Assert.True(MftReferenceProbe.ConfirmsMissingReference(buffer, requested));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0x41);
        Assert.True(MftReferenceProbe.ConfirmsMissingReference(buffer, requested));
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, 0x43);
        Assert.Throws<InvalidDataException>(() => MftReferenceProbe.ConfirmsMissingReference(buffer, requested));
        Assert.Throws<InvalidDataException>(() => MftReferenceProbe.ConfirmsMissingReference(buffer[..30], requested));
    }

    [Fact]
    public void BackupPrivilegeRejectsPartialAssignmentEvenWhenApiReturnsSuccess()
    {
        NativePrivileges.ValidateAdjustment(true, 0);
        var missing = Assert.Throws<System.ComponentModel.Win32Exception>(() => NativePrivileges.ValidateAdjustment(true, 1300));
        Assert.Equal(1300, missing.NativeErrorCode);
        Assert.Contains("SeBackupPrivilege", missing.Message);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => NativePrivileges.ValidateAdjustment(false, 5));
    }

    [Fact]
    public void RawMftLinksFollowAttributeListWithGenerationChecksAndIgnoreDosAliases()
    {
        const ulong file = 0x1000000000042, extension = 0x2000000000043, parent = 0x3000000000005;
        var first = new RawMftState.Record(file, 0, true, false,
            [new(1, parent, 1, "主\ud800.txt"), new(2, parent, 2, "SHORT~1.TXT")],
            [new(file, 1), new(file, 2), new(extension, 7)], true, 100, 200);
        var second = new RawMftState.Record(extension, file, true, false,
            [new(7, parent, 0, "另一链接.txt")], [], false, null, null);
        RawMftState.Record Load(ulong id) => id == file ? first : second;
        var links = RawMftState.Links(Load, first);
        Assert.Equal(new[] { "主\ud800.txt", "另一链接.txt" }, links.Select(l => l.Name));
        Assert.All(links, link => Assert.Equal(parent, link.ParentReference));
        Assert.Throws<InvalidDataException>(() => RawMftState.Links(_ => second with { Reference = extension + (1UL << 48) }, first));
        Assert.Throws<InvalidDataException>(() => RawMftState.Links(_ => second with { BaseReference = file + 1 }, first));
    }

    [Fact]
    public void RawMftFallbackDoesNotTreatGenericOpenParameterErrorsAsSupported()
    {
        Assert.True(NativeFileState.SupportsRawFallback("ReadLinks/class46", 87));
        Assert.True(NativeFileState.SupportsRawFallback("OpenById", 32));
        Assert.False(NativeFileState.SupportsRawFallback("OpenById", 87));
        Assert.False(NativeFileState.SupportsRawFallback("GetInfo", 87));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void MftCandidatesExcludeDosAliasesButRetainLongLinksAndAttributeLists(bool secondLongName, bool attributeList, bool expected)
    {
        byte[] record = new byte[1024];
        "FILE"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 48);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(16), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(18), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(20), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(22), 1);
        int offset = 56;
        AddName(1, "a"); AddName(secondLongName ? (byte)1 : (byte)2, "b");
        if (attributeList)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x20);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset + 4), 24);
            offset += 24;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), uint.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(24), offset + 8);
        var metadata = MftMetadata.Parse(record, 42)!.Value;
        Assert.Equal(secondLongName ? 2 : 1, metadata.LongNameCount);
        Assert.Equal(attributeList, metadata.HasAttributeList);
        Assert.Equal(expected, metadata.RequiresLinkEnumeration);
        void AddName(byte space, string name)
        {
            int contentLength = 66 + name.Length * 2;
            int length = (24 + contentLength + 7) / 8 * 8;
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(offset), 0x30);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset + 4), length);
            BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(offset + 16), contentLength);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(offset + 20), 24);
            var content = record.AsSpan(offset + 24, contentLength);
            content[64] = (byte)name.Length; content[65] = space;
            MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(content[66..]);
            offset += length;
        }
    }

    internal static byte[] Record(ulong frn, ulong parent, long usn, uint reason, string name)
    {
        byte[] data = new byte[8 + 60 + name.Length * 2];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), data.Length - 8);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), frn);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), parent);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(32), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(48), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(64), (ushort)(name.Length * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(66), 60);
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(data.AsSpan(68));
        return data;
    }
}
