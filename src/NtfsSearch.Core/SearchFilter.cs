namespace NtfsSearch.Core;

/// <summary>元数据筛选范围。大小含上下限，UTC 时间范围含起点、不含终点。</summary>
public sealed record SearchFilter(
    long? MinSizeBytes = null, long? MaxSizeBytes = null,
    long? ModifiedFromUtcTicks = null, long? ModifiedBeforeUtcTicks = null,
    long? CreatedFromUtcTicks = null, long? CreatedBeforeUtcTicks = null)
{
    private bool HasSizeRange => MinSizeBytes.HasValue || MaxSizeBytes.HasValue;
    private bool HasModifiedRange => ModifiedFromUtcTicks.HasValue || ModifiedBeforeUtcTicks.HasValue;
    private bool HasCreatedRange => CreatedFromUtcTicks.HasValue || CreatedBeforeUtcTicks.HasValue;
    internal bool IsEmpty => !HasSizeRange && !HasModifiedRange && !HasCreatedRange;

    internal void Validate()
    {
        if (MinSizeBytes < 0 || MaxSizeBytes < 0 || MinSizeBytes > MaxSizeBytes)
            throw new ArgumentException("文件大小范围无效。");
        if (ModifiedFromUtcTicks > ModifiedBeforeUtcTicks)
            throw new ArgumentException("修改时间范围无效。");
        if (CreatedFromUtcTicks > CreatedBeforeUtcTicks)
            throw new ArgumentException("创建时间范围无效。");
    }

    internal bool Matches(SearchIndex index, int id, in IndexRecord record)
    {
        if (HasSizeRange)
        {
            if (!record.HasSize) return false;
            long size = index.Size(id);
            if (size < MinSizeBytes || size > MaxSizeBytes) return false;
        }
        if (HasModifiedRange && (!record.HasModified ||
            record.Modified < ModifiedFromUtcTicks || record.Modified >= ModifiedBeforeUtcTicks))
            return false;
        if (HasCreatedRange)
        {
            if (index.CreatedUtcTicks(id) is not long created) return false;
            if (created < CreatedFromUtcTicks || created >= CreatedBeforeUtcTicks) return false;
        }
        return true;
    }
}
