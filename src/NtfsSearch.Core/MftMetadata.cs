using System.Buffers.Binary;
using DiscUtils.Ntfs;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

internal readonly record struct MftMetadata(ulong FileReference, ushort LinkCount, long? Size, long? ModifiedUtcTicks,
    int LongNameCount, bool HasAttributeList, long? CreatedUtcTicks)
{
    internal bool RequiresLinkEnumeration => LongNameCount > 1 || HasAttributeList;
    internal static long? UtcTicksFromFileTime(long fileTime) =>
        fileTime is > 0 and <= 2650467743999999999L ? DateTime.FromFileTimeUtc(fileTime).Ticks : null;

    internal static MftMetadata? Parse(ReadOnlySpan<byte> source, ulong slot)
    {
        byte[] storage = source.ToArray();
        return ParseInPlace(storage, slot);
    }

    internal static MftMetadata? ParseInPlace(Span<byte> record, ulong slot)
    {
        if (record.Length < 48 || !record[..4].SequenceEqual("FILE"u8)) return null;
        int usaOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        int usaCount = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        if (usaCount < 2 || usaOffset < 8 || usaOffset + usaCount * 2 > record.Length || record.Length % (usaCount - 1) != 0)
            throw new InvalidDataException("MFT 更新序列数组无效。");
        int sectorSize = record.Length / (usaCount - 1);
        if (sectorSize < 512 || (sectorSize & (sectorSize - 1)) != 0) throw new InvalidDataException("MFT 扇区大小无效。");
        ushort marker = BinaryPrimitives.ReadUInt16LittleEndian(record[usaOffset..]);
        for (int i = 1; i < usaCount; i++)
        {
            int end = i * sectorSize - 2;
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[end..]) != marker) throw new InvalidDataException("MFT 记录存在撕裂扇区，需要重试扫描。");
            record.Slice(usaOffset + i * 2, 2).CopyTo(record[end..]);
        }
        if ((BinaryPrimitives.ReadUInt16LittleEndian(record[22..]) & 1) == 0 || BinaryPrimitives.ReadUInt64LittleEndian(record[32..]) != 0) return null;
        int used = BinaryPrimitives.ReadInt32LittleEndian(record[24..]);
        int offset = BinaryPrimitives.ReadUInt16LittleEndian(record[20..]);
        if (used < 48 || used > record.Length || offset < 48 || offset > used - 4) throw new InvalidDataException("MFT 属性边界无效。");
        long? size = null, modified = null, created = null;
        int longNames = 0; bool hasAttributeList = false;
        while (offset <= used - 4)
        {
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(record[offset..]);
            if (type == uint.MaxValue) break;
            if (used - offset < 24) throw new InvalidDataException("MFT 属性头不完整。");
            var attribute = record.Slice(offset, used - offset);
            int length = BinaryPrimitives.ReadInt32LittleEndian(attribute[4..]);
            if (length < 24 || length > attribute.Length || (length & 7) != 0) throw new InvalidDataException("MFT 属性长度无效。");
            attribute = attribute[..length];
            bool nonresident = attribute[8] != 0;
            if (type == 0x20) hasAttributeList = true;
            if (type == 0x30)
            {
                // header LinkCount也包括DOS别名，只有独立长名才需要逐对象补链。
                if (nonresident) throw new InvalidDataException("FILE_NAME属性必须驻留。");
                int content = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
                uint contentLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                if (content < 24 || content > length || contentLength > length - content) throw new InvalidDataException("MFT FILE_NAME属性越界。");
                if (RawMftState.ValidateNameContent(attribute.Slice(content, (int)contentLength)) != 2) longNames++;
            }
            if (type == 0x10 && !nonresident)
            {
                int content = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
                uint contentLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                if (content < 24 || contentLength < 16 || content > length - 16 || contentLength > length - content) throw new InvalidDataException("MFT 标准信息越界。");
                created = UtcTicksFromFileTime(BinaryPrimitives.ReadInt64LittleEndian(attribute[content..]));
                modified = UtcTicksFromFileTime(BinaryPrimitives.ReadInt64LittleEndian(attribute[(content + 8)..]));
            }
            if (type == 0x80 && attribute[9] == 0)
            {
                if (!nonresident)
                {
                    int content = BinaryPrimitives.ReadUInt16LittleEndian(attribute[20..]);
                    uint contentLength = BinaryPrimitives.ReadUInt32LittleEndian(attribute[16..]);
                    if (content < 24 || content > length || contentLength > length - content) throw new InvalidDataException("MFT驻留数据越界。");
                    size = contentLength;
                }
                else
                {
                    if (length < 64) throw new InvalidDataException("MFT 非驻留数据头不完整。");
                    if (BinaryPrimitives.ReadInt64LittleEndian(attribute[16..]) == 0) size = BinaryPrimitives.ReadInt64LittleEndian(attribute[48..]);
                }
                if (size < 0) throw new InvalidDataException("MFT 数据长度无效。");
            }
            offset += length;
        }
        return new(((ulong)BinaryPrimitives.ReadUInt16LittleEndian(record[16..]) << 48) | slot,
            BinaryPrimitives.ReadUInt16LittleEndian(record[18..]), size, modified, longNames, hasAttributeList, created);
    }
}

// 数据流映射（含碎片与ATTRIBUTE_LIST）由DiscUtils维护；这里只读取基础记录元数据。
internal sealed class MftMetadataReader : IDisposable
{
    private readonly RawVolumeStream raw;
    private readonly NtfsFileSystem filesystem;
    private readonly Stream mft;
    private readonly int recordSize;
    private readonly byte[] recordBuffer;
    private readonly byte[] block = new byte[1 << 20];
    private long blockStart = -1;
    private int blockLength;

    internal MftMetadataReader(char drive)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        raw = new(drive);
        try
        {
            Span<byte> boot = stackalloc byte[512]; raw.ReadExactly(boot);
            int clusterSize = BinaryPrimitives.ReadUInt16LittleEndian(boot[11..]) * boot[13];
            sbyte encoded = unchecked((sbyte)boot[64]);
            if (encoded < -20) throw new InvalidDataException("不支持的MFT记录大小。");
            recordSize = encoded < 0 ? 1 << -encoded : checked(encoded * clusterSize);
            if (recordSize < 512 || recordSize > block.Length || block.Length % recordSize != 0) throw new InvalidDataException("不支持的MFT记录大小。");
            recordBuffer = new byte[recordSize];
            filesystem = new(raw); mft = filesystem.OpenFile("$MFT", FileMode.Open, FileAccess.Read);
        }
        catch (Exception error)
        {
            mft?.Dispose(); filesystem?.Dispose(); raw.Dispose();
            throw new IOException($"MFT初始化失败，卷{drive}:，只读DiscUtils映射：{error.Message}", error);
        }
    }

    internal IndexedFileState ReadFileState(char drive, ulong reference) => RawMftState.Read(filesystem, drive, reference);

    internal MftMetadata? Read(ulong reference)
    {
        try { return ReadCore(reference); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        {
            throw new IOException($"MFTslot读取/解析失败，FRN=0x{reference:X16}，slot={reference & 0x0000ffffffffffffUL}：{error.Message}", error);
        }
    }

    private MftMetadata? ReadCore(ulong reference)
    {
        ulong slot = reference & 0x0000ffffffffffffUL;
        long offset = checked((long)slot * recordSize);
        if (offset > mft.Length - recordSize) return null;
        long start = offset / block.Length * block.Length;
        if (start != blockStart)
        {
            mft.Position = start;
            blockLength = (int)Math.Min(block.Length, mft.Length - start);
            mft.ReadExactly(block.AsSpan(0, blockLength)); blockStart = start;
        }
        if (offset - start > blockLength - recordSize) throw new InvalidDataException("MFT记录超出数据流。");
        block.AsSpan((int)(offset - start), recordSize).CopyTo(recordBuffer);
        var result = MftMetadata.ParseInPlace(recordBuffer, slot);
        return result?.FileReference == reference ? result : null;
    }
    public void Dispose() { mft.Dispose(); filesystem.Dispose(); raw.Dispose(); }
}

// 卷句柄要求按扇区对齐读；将DiscUtils任意小块读取适配为4KiB对齐读取。
internal sealed class RawVolumeStream : Stream
{
    private readonly SafeFileHandle handle;
    private readonly char drive;
    private readonly byte[] buffer = new byte[64 << 10];
    private long cachedOffset = -1;
    private int cachedLength;
    private long position;
    private readonly long length;
    internal RawVolumeStream(char drive)
    {
        this.drive = char.ToUpperInvariant(drive);
        handle = NativeJournal.Open(drive);
        try
        {
            int read = ReadRaw(buffer.AsSpan(0, 4096), 0);
            if (read < 512) throw new InvalidDataException("NTFS引导扇区不完整。");
            int sector = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(11));
            if (sector is not (512 or 1024 or 2048 or 4096)) throw new InvalidDataException("不支持的NTFS扇区大小。");
            length = checked(BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(40)) * sector);
            if (length < 4096) throw new InvalidDataException("NTFS卷大小无效。");
        }
        catch { handle.Dispose(); throw; }
    }
    public override int Read(byte[] output, int offset, int count) => Read(output.AsSpan(offset, count));
    public override int Read(Span<byte> output)
    {
        int total = 0;
        while (!output.IsEmpty && position < length)
        {
            long start = position / buffer.Length * buffer.Length;
            if (cachedOffset != start)
            {
                // NTFS总扇区数可能不含备份引导扇区；最后一次读仍可向上补齐4KiB。
                int requested = (int)Math.Min(buffer.Length, ((length - start + 4095) / 4096) * 4096);
                cachedLength = ReadRaw(buffer.AsSpan(0, requested), start);
                cachedOffset = start;
            }
            int source = (int)(position - start);
            int count = (int)Math.Min(Math.Min(output.Length, cachedLength - source), length - position);
            if (count <= 0) throw new EndOfStreamException("原始卷读取提前结束。");
            buffer.AsSpan(source, count).CopyTo(output); output = output[count..]; position += count; total += count;
        }
        return total;
    }
    private int ReadRaw(Span<byte> output, long offset)
    {
        try { return RandomAccess.Read(handle, output, offset); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"原始卷{drive}:读取失败，position={offset}，length={output.Length}：{error.Message}", error);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => checked(position + offset), SeekOrigin.End => checked(length + offset), _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
        if (target < 0) throw new IOException("负数卷偏移。");
        return position = target;
    }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) handle.Dispose(); base.Dispose(disposing); }
}
