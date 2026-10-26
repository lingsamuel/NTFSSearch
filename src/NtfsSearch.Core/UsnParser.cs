using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

public delegate void UsnVisitor(ulong fileReference, ulong parentReference, ReadOnlySpan<char> name, uint attributes);

public static class UsnParser
{
    public static IReadOnlyList<JournalRecord> ReadJournal(ReadOnlySpan<byte> buffer)
    {
        var records = new List<JournalRecord>();
        VisitRecords(buffer, (record, name) => records.Add(new(
            BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(record[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
            BinaryPrimitives.ReadUInt32LittleEndian(record[52..]), name.ToString())));
        return records;
    }

    private delegate void RecordVisitor(ReadOnlySpan<byte> record, ReadOnlySpan<char> name);

    public static void Visit(ReadOnlySpan<byte> buffer, UsnVisitor visitor) =>
        VisitRecords(buffer, (record, name) => visitor(
            BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), name,
            BinaryPrimitives.ReadUInt32LittleEndian(record[52..])));

    private static void VisitRecords(ReadOnlySpan<byte> buffer, RecordVisitor visitor)
    {
        if (buffer.Length < 8) throw new InvalidDataException("USN 缓冲区缺少游标。");
        int offset = 8;
        while (offset < buffer.Length)
        {
            var record = buffer[offset..];
            if (record.Length < 60) throw new InvalidDataException("USN 记录头不完整。");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(record);
            if (length < 60 || length > record.Length) throw new InvalidDataException("USN 记录长度越界。");
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 2)
                throw new NotSupportedException("原型目前仅支持 V2 USN 记录，已停止扫描，避免静默遗漏文件。");
            int bytes = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            int nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if ((bytes & 1) != 0 || (nameOffset & 1) != 0 || nameOffset < 60 || nameOffset > length || bytes > length - nameOffset)
                throw new InvalidDataException("USN 名称字段越界。");
            visitor(record[..(int)length], MemoryMarshal.Cast<byte, char>(record.Slice(nameOffset, bytes)));
            offset += (int)length;
        }
    }
}
