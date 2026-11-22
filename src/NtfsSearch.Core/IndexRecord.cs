using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

// 两个ID的高位存标志；大小用直接值或旁表码，修改时间保留完整64位值域。
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal readonly struct IndexRecord
{
    public readonly long Modified;
    public readonly uint SizeCode;
    private readonly uint nameAndModified;
    private readonly uint directoryAndType;
    public int NameId => (int)(nameAndModified & int.MaxValue);
    public int Directory => (int)(directoryAndType & int.MaxValue);
    public bool IsDirectory => (directoryAndType & 0x80000000U) != 0;
    public bool HasSize => SizeCode != uint.MaxValue;
    public bool HasModified => (nameAndModified & 0x80000000U) != 0;

    internal IndexRecord(int nameId, uint sizeCode, long modified, int directory, bool isDirectory, bool hasModified)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nameId);
        ArgumentOutOfRangeException.ThrowIfNegative(directory);
        SizeCode = isDirectory ? uint.MaxValue : sizeCode; Modified = modified;
        nameAndModified = (uint)nameId | (hasModified ? 0x80000000U : 0);
        directoryAndType = (uint)directory | (isDirectory ? 0x80000000U : 0);
    }
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
