namespace NtfsSearch.Core;

/// <summary>搜索筛选条件。大小含上下限，UTC 时间范围含起点、不含终点。后缀只限制文件，空字符串表示无后缀。</summary>
public sealed record SearchFilter(
    long? MinSizeBytes = null, long? MaxSizeBytes = null,
    long? ModifiedFromUtcTicks = null, long? ModifiedBeforeUtcTicks = null,
    long? CreatedFromUtcTicks = null, long? CreatedBeforeUtcTicks = null,
    bool IncludeFiles = true, bool IncludeDirectories = true, string[]? FileExtensions = null)
{
    private bool HasSizeRange => MinSizeBytes.HasValue || MaxSizeBytes.HasValue;
    private bool HasModifiedRange => ModifiedFromUtcTicks.HasValue || ModifiedBeforeUtcTicks.HasValue;
    private bool HasCreatedRange => CreatedFromUtcTicks.HasValue || CreatedBeforeUtcTicks.HasValue;
    internal bool IsEmpty => IncludeFiles && IncludeDirectories && FileExtensions is not { Length: > 0 }
        && !HasSizeRange && !HasModifiedRange && !HasCreatedRange;

    internal HashSet<string>? CreateExtensionSet() => IncludeFiles && FileExtensions is { Length: > 0 }
        ? new(FileExtensions, StringComparer.OrdinalIgnoreCase) : null;

    internal void Validate()
    {
        if (MinSizeBytes < 0 || MaxSizeBytes < 0 || MinSizeBytes > MaxSizeBytes)
            throw new ArgumentException("文件大小范围无效。");
        if (ModifiedFromUtcTicks > ModifiedBeforeUtcTicks)
            throw new ArgumentException("修改时间范围无效。");
        if (CreatedFromUtcTicks > CreatedBeforeUtcTicks)
            throw new ArgumentException("创建时间范围无效。");
    }

    internal bool Matches(SearchIndex index, int id)
    {
        if (index.Records[id].IsDirectory ? !IncludeDirectories : !IncludeFiles) return false;
        if (HasSizeRange)
        {
            if (!index.HasSize(id)) return false;
            long size = index.Size(id);
            if (size < MinSizeBytes || size > MaxSizeBytes) return false;
        }
        if (HasModifiedRange)
        {
            if (!index.HasModified(id)) return false;
            long modified = index.Modified(id);
            if (modified < ModifiedFromUtcTicks || modified >= ModifiedBeforeUtcTicks) return false;
        }
        if (HasCreatedRange)
        {
            if (index.CreatedUtcTicks(id) is not long created) return false;
            if (created < CreatedFromUtcTicks || created >= CreatedBeforeUtcTicks) return false;
        }
        return true;
    }
}
