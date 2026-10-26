using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

// 名称偏移使用 64 位，避免亿级名称区跨过 4 GiB 后截断；元数据未知用标志表示。
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct IndexRecord(long NameOffset, long Size, long Modified,
    int Directory, ushort NameLength, ushort Flags)
{
    public bool IsDirectory => (Flags & 1) != 0;
    public bool HasSize => (Flags & 2) != 0;
    public bool HasModified => (Flags & 4) != 0;
}

public enum SortField { Name, Path, Size, Modified }

public sealed record SearchRow(int Id, string Name, string FullPath, long? Size,
    long? ModifiedUtcTicks, bool IsDirectory);

public sealed record SearchResult(long TotalCount, IReadOnlyList<SearchRow> Rows, TimeSpan Elapsed);

public sealed record IndexStatistics(int Entries, int Directories, long NameBytes,
    long RecordBytes, long SortBytes, long EstimatedDirectoryBytes)
{
    public long EstimatedPayloadBytes => NameBytes + RecordBytes + SortBytes + EstimatedDirectoryBytes;
}
