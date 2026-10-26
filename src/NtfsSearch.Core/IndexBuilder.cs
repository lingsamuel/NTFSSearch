namespace NtfsSearch.Core;

public sealed class IndexBuilder
{
    private readonly NameArena names = new();
    private IndexRecord[] records;
    private int count;
    private readonly List<string> directories = [];
    private readonly Dictionary<string, int> directoryIds = new(StringComparer.Ordinal);
    private bool built;

    public IndexBuilder(int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        records = new IndexRecord[capacity];
    }

    public void Add(string parentPath, ReadOnlySpan<char> name, long? size = null,
        long? modifiedUtcTicks = null, bool isDirectory = false)
    {
        if (built) throw new InvalidOperationException("索引已冻结。请创建新的构建器。");
        ArgumentException.ThrowIfNullOrEmpty(parentPath);
        if (name.IsEmpty || name.Contains('\\') || name.Contains('/'))
            throw new ArgumentException("名称必须是单个非空路径分量。", nameof(name));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (name.Length > ushort.MaxValue) throw new ArgumentException("名称过长。", nameof(name));
        if (!directoryIds.TryGetValue(parentPath, out int directory))
        {
            var normalized = parentPath.Replace('/', '\\');
            if (!normalized.EndsWith('\\')) normalized += '\\';
            if (!directoryIds.TryGetValue(normalized, out directory))
            {
                directory = directories.Count;
                directories.Add(normalized);
                directoryIds.Add(normalized, directory);
            }
            if (!directoryIds.ContainsKey(parentPath)) directoryIds.Add(parentPath, directory);
        }
        ushort flags = (ushort)((isDirectory ? 1 : 0) | (size.HasValue && !isDirectory ? 2 : 0)
            | (modifiedUtcTicks.HasValue ? 4 : 0));
        if (count == records.Length) Array.Resize(ref records, Math.Max(16, checked(count + Math.Max(count / 2, 1))));
        records[count++] = new(names.Add(name), size.GetValueOrDefault(), modifiedUtcTicks.GetValueOrDefault(),
            directory, (ushort)name.Length, flags);
    }

    public SearchIndex Build(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (built) throw new InvalidOperationException("索引已冻结。");
        built = true;
        names.Seal();
        // 转移底层记录数组，避免在亿级构建收尾时再复制一份记录。
        if (records.Length != count) Array.Resize(ref records, count);
        var array = records;
        records = [];
        directoryIds.Clear();
        return new(names, array, directories.ToArray(), cancellationToken, progress);
    }
}
