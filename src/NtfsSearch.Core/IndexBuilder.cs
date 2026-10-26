namespace NtfsSearch.Core;

public sealed class IndexBuilder
{
    private readonly NameArena names;
    private IndexRecord[] records;
    private int count;
    private ulong[]? fileReferences;
    private bool identityComplete = true;
    private readonly List<ulong> directoryReferences = [];
    private readonly List<string> directories = [];
    private readonly Dictionary<string, int> directoryIds = new(StringComparer.Ordinal);
    private bool built;

    public IndexBuilder(int capacity = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        names = new((int)Math.Clamp((long)capacity * 32, 256, NameArena.ChunkChars));
        records = new IndexRecord[capacity];
    }

    public void Add(string parentPath, ReadOnlySpan<char> name, long? size = null,
        long? modifiedUtcTicks = null, bool isDirectory = false, ulong? fileReference = null, ulong? parentReference = null)
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
                directoryReferences.Add(parentReference.GetValueOrDefault());
                directoryIds.Add(normalized, directory);
            }
            if (!directoryIds.ContainsKey(parentPath)) directoryIds.Add(parentPath, directory);
        }
        if (!fileReference.HasValue || !parentReference.HasValue) identityComplete = false;
        if (parentReference.HasValue && directoryReferences[directory] != parentReference.Value)
            throw new ArgumentException("同一路径的父目录身份不一致。", nameof(parentReference));
        ushort flags = (ushort)((isDirectory ? 1 : 0) | (size.HasValue && !isDirectory ? 2 : 0)
            | (modifiedUtcTicks.HasValue ? 4 : 0));
        if (count == records.Length) Array.Resize(ref records, Math.Max(16, checked(count + Math.Max(count / 2, 1))));
        if (fileReference.HasValue)
        {
            fileReferences ??= new ulong[records.Length];
            if (fileReferences.Length != records.Length) Array.Resize(ref fileReferences, records.Length);
            fileReferences[count] = fileReference.Value;
        }
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
        ulong[]? references = count == 0 ? [] : null;
        if (identityComplete && fileReferences is not null)
        {
            Array.Resize(ref fileReferences, count);
            references = fileReferences;
            bool sorted = true;
            for (int i = 1; i < count; i++)
                if (SearchIndex.CompareReference(references[i - 1], references[i]) > 0) { sorted = false; break; }
            if (!sorted) Array.Sort(references, array, Comparer<ulong>.Create(SearchIndex.CompareReference));
        }
        return new(names, array, directories.ToArray(), cancellationToken, progress,
            fileReferences: references, directoryReferences: references is null ? null : directoryReferences.ToArray());
    }
}
