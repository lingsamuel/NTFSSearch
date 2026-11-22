using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 基线只存目录行。增量覆盖共享基线，查询可在后台更新期间保留完整的旧视图。
internal sealed class DirectorySizeOrder
{
    private static readonly Int128 Unknown = -1;
    private readonly int[] directoryRows;
    private readonly string[] baselinePaths;
    private readonly Int128[] sizes;
    private readonly int knownCount;
    private readonly int[]? order;
    private readonly object? sortOwner;
    private readonly ImmutableDictionary<int, Int128> overrides;
    private readonly ImmutableSortedSet<Entry>? corrections;
    internal SearchIndex Index { get; }
    internal bool HasKnownDirectories => knownCount != 0;
    internal object SortOwner => sortOwner ?? this;

    internal static DirectorySizeOrder Create(SearchIndex index, SpaceUsageIndex usage, CancellationToken token)
    {
        var rows = new List<int>();
        for (int id = 0; id < index.Count; id++)
        {
            if ((id & 4095) == 0) usage.Checkpoint(token);
            if (index.Records[id].IsDirectory) rows.Add(id);
        }
        return new(index, rows.ToArray(), usage, token);
    }

    private DirectorySizeOrder(SearchIndex index, int[] rows, SpaceUsageIndex usage, CancellationToken token)
    {
        Index = index;
        directoryRows = rows;
        baselinePaths = index.Directories;
        usage.Checkpoint(token);
        sizes = IndexArrays.Persistent<Int128>(rows.Length);
        for (int slot = 0; slot < rows.Length; slot++)
        {
            if ((slot & 4095) == 0) usage.Checkpoint(token);
            sizes[slot] = Read(index, rows[slot], usage);
            if (sizes[slot] != Unknown) knownCount++;
        }
        overrides = ImmutableDictionary<int, Int128>.Empty;
        if (index.PrecomputeSort)
        {
            order = BuildOrder(token, () => usage.Checkpoint(token));
            corrections = ImmutableSortedSet.Create<Entry>(CompareEntries(index));
        }
        else sortOwner = new();
    }

    private DirectorySizeOrder(SearchIndex index, DirectorySizeOrder baseline, ImmutableDictionary<int, Int128> overrides,
        int knownCount, ImmutableSortedSet<Entry>? corrections, object? previousSortOwner = null)
    {
        Index = index;
        directoryRows = baseline.directoryRows;
        baselinePaths = baseline.baselinePaths;
        sizes = baseline.sizes;
        order = baseline.order;
        this.overrides = overrides;
        this.knownCount = knownCount;
        this.corrections = corrections;
        sortOwner = index.PrecomputeSort ? null : previousSortOwner ?? new object();
    }

    internal Int128? GetSize(int row)
    {
        Int128 bytes = overrides.TryGetValue(row, out var changed) ? changed : BaseSize(row);
        return bytes == Unknown ? null : bytes;
    }

    private Int128 BaseSize(int row)
    {
        int slot = Array.BinarySearch(directoryRows, row);
        return slot >= 0 ? sizes[slot] : Unknown;
    }

    internal DirectorySizeOrder Update(SearchIndex index, SpaceUsageIndex usage, CancellationToken token)
    {
        int rebaseThreshold = Math.Max(1024, directoryRows.Length / 8);
        var affected = new HashSet<int>();
        bool samePaths = ReferenceEquals(index.Directories, Index.Directories);
        bool pathsChanged = false;
        if (corrections is not null && !samePaths)
        {
            for (int slot = 0; slot < directoryRows.Length; slot++)
            {
                if ((slot & 4095) == 0) usage.Checkpoint(token);
                int id = directoryRows[slot];
                int parent = index.Records[id].Directory;
                if (index.Directories[parent] != Index.Directories[parent]) affected.Add(id);
            }
            pathsChanged = affected.Count != 0;
        }
        foreach (ulong reference in usage.ChangedDirectories)
        {
            usage.Checkpoint(token);
            var rows = reference == 0 ? index.FindRecordRows(5) : index.FindFileRows(reference);
            foreach (int id in rows)
                if (index.Records[id].IsDirectory) affected.Add(id);
        }
        if (affected.Count == 0)
        {
            // 文件元数据可以更新索引视图但不改变目录排序。只共享键，避免新视图持有旧索引。
            return index == Index ? this : new(index, this, overrides, knownCount, corrections, samePaths ? sortOwner : null);
        }
        if (affected.Count > rebaseThreshold)
            return new(index, directoryRows, usage, token);

        var sorted = corrections?.ToBuilder();
        if (sorted is not null)
        {
            // 先按旧路径从修正流移除，再用新路径插入。大小相同的移动目录也需要修正位置。
            foreach (int id in affected)
                if (overrides.TryGetValue(id, out var previous) && previous != Unknown)
                    sorted.Remove(new(id, previous));
            if (pathsChanged) sorted.KeyComparer = CompareEntries(index);
        }

        var next = overrides.ToBuilder();
        int known = knownCount;
        foreach (int id in affected)
        {
            usage.Checkpoint(token);
            Int128 bytes = Read(index, id, usage);
            Int128 baseline = BaseSize(id);
            Int128 previous = overrides.TryGetValue(id, out var changed) ? changed : baseline;
            if ((previous == Unknown) != (bytes == Unknown)) known += bytes == Unknown ? -1 : 1;
            int parent = index.Records[id].Directory;
            if (bytes == baseline && (sorted is null || bytes == Unknown || index.Directories[parent] == baselinePaths[parent]))
                next.Remove(id);
            else
            {
                next[id] = bytes;
                if (bytes != Unknown) sorted?.Add(new(id, bytes));
            }
        }
        if (next.Count > rebaseThreshold)
            return new(index, directoryRows, usage, token);
        return new(index, this, next.ToImmutable(), known, sorted?.ToImmutable());
    }

    private static Comparer<Entry> CompareEntries(SearchIndex index)
        => Comparer<Entry>.Create((a, b) => SearchIndex.CompareSizes(index, a.Row, a.Bytes, index, b.Row, b.Bytes));

    private static Int128 Read(SearchIndex index, int id, SpaceUsageIndex usage)
        => index.IsVisible(id) ? usage.DirectorySize(index.FileReferences![id]) ?? Unknown : Unknown;

    private int[] BuildOrder(CancellationToken token, Action? checkpoint = null)
    {
        if (knownCount == 0) return [];
        var sorted = new int[knownCount];
        int position = 0;
        for (int slot = 0; slot < directoryRows.Length; slot++)
        {
            if ((slot & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
            }
            if (SizeAt(slot) != Unknown) sorted[position++] = slot;
        }
        return SortIndex.Sort(sorted, (a, b) => SearchIndex.CompareSizes(Index, directoryRows[a], SizeAt(a),
            Index, directoryRows[b], SizeAt(b)), token, maxParallelism: 1, checkpoint: checkpoint);

        Int128 SizeAt(int slot) => overrides.TryGetValue(directoryRows[slot], out var changed) ? changed : sizes[slot];
    }

    internal IEnumerable<int> Ordered(bool descending, CancellationToken token, SearchSortSelection? selection = null)
    {
        token.ThrowIfCancellationRequested();
        if (knownCount == 0) yield break;
        if (order is null)
        {
            // 合计视图不持有按需排列。桌面的当前排序列可以在查询之间复用它。
            var sorted = selection?.Get(SortOwner, () => BuildOrder(token), token) ?? BuildOrder(token);
            for (int i = 0; i < sorted.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                yield return directoryRows[sorted[descending ? sorted.Length - 1 - i : i]];
            }
            yield break;
        }

        var corrected = corrections!;
        using var changed = (descending ? corrected.Reverse() : corrected).GetEnumerator();
        bool hasChanged = changed.MoveNext();
        int position = 0;
        while (position < order.Length || hasChanged)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            if (position == order.Length)
            {
                yield return changed.Current.Row;
                hasChanged = changed.MoveNext();
                continue;
            }
            int slot = order[descending ? order.Length - 1 - position : position];
            int row = directoryRows[slot];
            if (overrides.ContainsKey(row)) { position++; continue; }
            if (hasChanged && SearchIndex.CompareSizes(Index, changed.Current.Row, changed.Current.Bytes,
                Index, row, sizes[slot], descending) < 0)
            {
                yield return changed.Current.Row;
                hasChanged = changed.MoveNext();
            }
            else
            {
                yield return row;
                position++;
            }
        }
    }

    private readonly record struct Entry(int Row, Int128 Bytes);
}
