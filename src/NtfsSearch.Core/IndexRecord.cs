using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

[Flags]
internal enum IndexMetadata
{
    None = 0,
    FileSize = 1,
    ModifiedTime = 2,
    CreationTime = 4,
    All = FileSize | ModifiedTime | CreationTime
}

// 两个ID的高位保存目录类型和修改时间的已知标志，其余元数据按需独立存放。
[StructLayout(LayoutKind.Sequential)]
internal readonly struct IndexRecord
{
    private readonly uint nameAndModified;
    private readonly uint directoryAndType;
    public int NameId => (int)(nameAndModified & int.MaxValue);
    public int Directory => (int)(directoryAndType & int.MaxValue);
    public bool IsDirectory => (directoryAndType & 0x80000000U) != 0;
    public bool HasModified => (nameAndModified & 0x80000000U) != 0;

    internal IndexRecord(int nameId, int directory, bool isDirectory, bool hasModified)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nameId);
        ArgumentOutOfRangeException.ThrowIfNegative(directory);
        nameAndModified = (uint)nameId | (hasModified ? 0x80000000U : 0);
        directoryAndType = (uint)directory | (isDirectory ? 0x80000000U : 0);
    }

    internal IndexRecord WithoutModified() => new(NameId, Directory, IsDirectory, hasModified: false);
}

public enum SortField { Name, Path, Size, Modified }

public sealed record SearchRow(int Id, string Name, string FullPath, long? Size,
    long? ModifiedUtcTicks, bool IsDirectory)
{
    public long? CreatedUtcTicks { get; init; }
}

public sealed record SearchResult(long TotalCount, IReadOnlyList<SearchRow> Rows, TimeSpan Elapsed)
{
    public int VolumesReady { get; init; }
    internal List<DirectorySearchRow>? DirectoryRows { get; init; }
    internal bool DirectorySortPending { get; init; }
    internal bool DirectorySortFailed { get; init; }
}

public sealed record IndexStatistics(int Entries, int Directories, long NameBytes,
    long RecordBytes, long SortBytes, long EstimatedDirectoryBytes)
{
    public long EstimatedPayloadBytes => NameBytes + RecordBytes + SortBytes + EstimatedDirectoryBytes;
}
