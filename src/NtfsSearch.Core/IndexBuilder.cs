namespace NtfsSearch.Core;

public sealed class IndexBuilder
{
    private readonly NameArena names;
    private readonly SizeTable sizes = new();
    private IndexRecord[] records;
    private int count;
    private ulong[]? fileReferences;
    private long[]? creationTimes;
    private ulong[]? knownCreationTimes;
    private bool identityComplete = true;
    private readonly List<ulong> directoryReferences = [];
    private readonly List<string> directories = [];
    private readonly Dictionary<string, int> directoryIds = new(StringComparer.Ordinal);
    private readonly bool fixedSize;
    private bool built;

    public IndexBuilder(int capacity = 0) : this(capacity, fixedSize: false) { }

    internal IndexBuilder(int capacity, bool fixedSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        this.fixedSize = fixedSize;
        names = new((int)Math.Clamp((long)capacity * 32, 256, NameArena.ChunkChars));
        records = fixedSize ? IndexArrays.Persistent<IndexRecord>(capacity) : new IndexRecord[capacity];
        if (fixedSize) fileReferences = IndexArrays.Persistent<ulong>(capacity);
    }

    public void Add(string parentPath, ReadOnlySpan<char> name, long? size = null,
        long? modifiedUtcTicks = null, bool isDirectory = false, ulong? fileReference = null, ulong? parentReference = null,
        long? createdUtcTicks = null)
    {
        if (built) throw new InvalidOperationException("索引已冻结。请创建新的构建器。");
        ArgumentException.ThrowIfNullOrEmpty(parentPath);
        ValidateRow(name, size);
        if (!directoryIds.TryGetValue(parentPath, out int directory))
        {
            var normalized = parentPath.Replace('/', '\\');
            if (!normalized.EndsWith('\\')) normalized += '\\';
            if (!directoryIds.TryGetValue(normalized, out directory))
            {
                directory = AppendDirectory(normalized, parentReference.GetValueOrDefault());
                directoryIds.Add(normalized, directory);
            }
            if (!directoryIds.ContainsKey(parentPath)) directoryIds.Add(parentPath, directory);
        }
        if (!fileReference.HasValue || !parentReference.HasValue) identityComplete = false;
        if (parentReference.HasValue && directoryReferences[directory] != parentReference.Value)
            throw new ArgumentException("同一路径的父目录身份不一致。", nameof(parentReference));
        AppendRow(directory, name, size, modifiedUtcTicks, isDirectory, fileReference, createdUtcTicks);
    }

    internal int DirectoryCount => directories.Count;

    // 导入已有索引的目录编号已绑定文件身份，因此保留同路径的不同身份。
    internal int AddDirectory(string parentPath, ulong reference)
    {
        if (!parentPath.EndsWith('\\') || parentPath.Contains('/')) throw new InvalidDataException("父目录路径无效。");
        return AppendDirectory(parentPath, reference);
    }

    internal void AddRecord(int directory, ReadOnlySpan<char> name, long? size, long? modifiedUtcTicks, bool isDirectory, ulong reference,
        long? createdUtcTicks = null)
    {
        ValidateRow(name, size);
        AppendRow(directory, name, size, modifiedUtcTicks, isDirectory, reference, createdUtcTicks);
    }

    private int AppendDirectory(string path, ulong reference)
    {
        int directory = directories.Count;
        directories.Add(path);
        directoryReferences.Add(reference);
        return directory;
    }

    private static void ValidateRow(ReadOnlySpan<char> name, long? size)
    {
        if (name.IsEmpty || name.Contains('\\') || name.Contains('/'))
            throw new ArgumentException("名称必须是单个非空路径分量。", nameof(name));
        if (size < 0) throw new ArgumentOutOfRangeException(nameof(size));
        if (name.Length > ushort.MaxValue) throw new ArgumentException("名称过长。", nameof(name));
    }

    private void AppendRow(int directory, ReadOnlySpan<char> name, long? size, long? modifiedUtcTicks, bool isDirectory,
        ulong? fileReference, long? createdUtcTicks)
    {
        if (count == records.Length)
        {
            Array.Resize(ref records, Math.Max(16, checked(count + Math.Max(count / 2, 1))));
            if (creationTimes is not null)
            {
                Array.Resize(ref creationTimes, records.Length);
                Array.Resize(ref knownCreationTimes, BitWords(records.Length));
            }
        }
        if (createdUtcTicks is long creation)
        {
            creationTimes ??= fixedSize ? IndexArrays.Persistent<long>(records.Length) : new long[records.Length];
            knownCreationTimes ??= fixedSize ? IndexArrays.Persistent<ulong>(BitWords(records.Length)) : new ulong[BitWords(records.Length)];
            WriteCreationTime(creationTimes, knownCreationTimes, count, creation);
        }
        if (fileReference.HasValue)
        {
            fileReferences ??= new ulong[records.Length];
            if (fileReferences.Length != records.Length) Array.Resize(ref fileReferences, records.Length);
            fileReferences[count] = fileReference.Value;
        }
        records[count++] = new(names.Add(name, isDirectory), sizes.Add(isDirectory ? -1 : size ?? -1), modifiedUtcTicks.GetValueOrDefault(),
            directory, isDirectory, modifiedUtcTicks.HasValue);
    }

    private static int BitWords(int length) => (int)(((long)length + 63) / 64);

    private static void WriteCreationTime(long[] times, ulong[] known, int id, long? value)
    {
        ulong mask = 1UL << (id % 64);
        known[id / 64] = value.HasValue ? known[id / 64] | mask : known[id / 64] & ~mask;
        times[id] = value.GetValueOrDefault();
    }

    public SearchIndex Build(CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (built) throw new InvalidOperationException("索引已冻结。");
        built = true;
        names.Seal();
        // 终态大数组与构建期增长、散列表隔离，避免长期活对象夹住临时LOH空洞。
        var array = fixedSize && records.Length == count ? records : IndexArrays.Freeze(records, count);
        records = [];
        directoryIds.Clear();
        long[]? times = creationTimes is null ? null
            : fixedSize && creationTimes.Length == count ? creationTimes : IndexArrays.Freeze(creationTimes, count);
        ulong[]? known = knownCreationTimes is null ? null
            : fixedSize && knownCreationTimes.Length == BitWords(count) ? knownCreationTimes : IndexArrays.Freeze(knownCreationTimes, BitWords(count));
        creationTimes = null;
        knownCreationTimes = null;
        ulong[]? references = count == 0 ? [] : null;
        if (identityComplete && fileReferences is not null)
        {
            references = fixedSize && fileReferences.Length == count ? fileReferences : IndexArrays.Freeze(fileReferences, count);
            bool sorted = true;
            for (int i = 1; i < count; i++)
                if (SearchIndex.CompareReference(references[i - 1], references[i]) > 0) { sorted = false; break; }
            if (!sorted)
            {
                if (times is null) Array.Sort(references, array, Comparer<ulong>.Create(SearchIndex.CompareReference));
                else SortWithCreationTimes(references, array, times, known!, cancellationToken);
            }
        }
        fileReferences = null;
        return new(names, array, directories.ToArray(), cancellationToken, progress,
            fileReferences: references, directoryReferences: references is null ? null : IndexArrays.Copy<ulong>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(directoryReferences)),
            bigSizes: sizes.Freeze(), creationTimes: times, knownCreationTimes: known);
    }

    private static void SortWithCreationTimes(ulong[] references, IndexRecord[] rows, long[] times, ulong[] known, CancellationToken token)
    {
        // 排序只附带原行号，随后按置换周期移动行和时间列，避免复制两份全卷数据。
        var order = new int[references.Length];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(references, order, Comparer<ulong>.Create(SearchIndex.CompareReference));
        for (int i = 0; i < order.Length; i++)
        {
            if ((i & 65535) == 0) token.ThrowIfCancellationRequested();
            if (order[i] == i) continue;
            IndexRecord row = rows[i];
            long? creation = SearchIndex.ReadCreationTime(times, known, i);
            int destination = i;
            while (order[destination] != i)
            {
                int source = order[destination];
                if ((source & 65535) == 0) token.ThrowIfCancellationRequested();
                rows[destination] = rows[source];
                WriteCreationTime(times, known, destination, SearchIndex.ReadCreationTime(times, known, source));
                order[destination] = destination;
                destination = source;
            }
            rows[destination] = row;
            WriteCreationTime(times, known, destination, creation);
            order[destination] = destination;
        }
    }
}
