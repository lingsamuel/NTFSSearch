using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 基线只存目录行。增量覆盖共享基线，查询可在后台更新期间保留完整的旧视图。
internal sealed class DirectorySizeOrder
{
    private static readonly Int128 Unknown = -1;
    private readonly int[] directoryRows;
    private readonly string[] baselinePaths;
    private readonly Int128[] sizes;
    private readonly int[] order;
    private readonly ImmutableDictionary<int, Int128> overrides;
    private readonly ImmutableSortedSet<Entry> corrections;
    internal SearchIndex Index { get; }
    internal bool HasKnownDirectories => order.Length != 0 || corrections.Count != 0;

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
        var sorted = new int[rows.Length];
        int known = 0;
        for (int slot = 0; slot < rows.Length; slot++)
        {
            if ((slot & 4095) == 0) usage.Checkpoint(token);
            int id = rows[slot];
            sizes[slot] = Read(index, id, usage);
            if (sizes[slot] != Unknown) sorted[known++] = slot;
        }
        Array.Resize(ref sorted, known);
        order = SortIndex.Sort(sorted, (a, b) => SearchIndex.CompareSizes(index, rows[a], sizes[a],
            index, rows[b], sizes[b]), token, maxParallelism: 1, checkpoint: () => usage.Checkpoint(token));
        overrides = ImmutableDictionary<int, Int128>.Empty;
        corrections = ImmutableSortedSet.Create<Entry>(CompareEntries(index));
    }

    private DirectorySizeOrder(SearchIndex index, DirectorySizeOrder baseline, ImmutableDictionary<int, Int128> overrides,
        ImmutableSortedSet<Entry> corrections)
    {
        Index = index;
        directoryRows = baseline.directoryRows;
        baselinePaths = baseline.baselinePaths;
        sizes = baseline.sizes;
        order = baseline.order;
        this.overrides = overrides;
        this.corrections = corrections;
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
        bool pathsChanged = false;
        if (index.Directories != Index.Directories)
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
            return index == Index ? this : new(index, this, overrides, corrections);
        if (affected.Count > rebaseThreshold)
            return new(index, directoryRows, usage, token);

        // 先按旧路径从修正流移除，再用新路径插入。大小相同的移动目录也需要修正位置。
        var sorted = corrections.ToBuilder();
        foreach (int id in affected)
            if (overrides.TryGetValue(id, out var previous) && previous != Unknown)
                sorted.Remove(new(id, previous));
        if (pathsChanged) sorted.KeyComparer = CompareEntries(index);

        var next = overrides.ToBuilder();
        foreach (int id in affected)
        {
            usage.Checkpoint(token);
            Int128 bytes = Read(index, id, usage);
            int parent = index.Records[id].Directory;
            if (bytes == BaseSize(id) && (bytes == Unknown || index.Directories[parent] == baselinePaths[parent]))
                next.Remove(id);
            else
            {
                next[id] = bytes;
                if (bytes != Unknown) sorted.Add(new(id, bytes));
            }
        }
        if (next.Count > rebaseThreshold)
            return new(index, directoryRows, usage, token);
        return new(index, this, next.ToImmutable(), sorted.ToImmutable());
    }

    private static Comparer<Entry> CompareEntries(SearchIndex index)
        => Comparer<Entry>.Create((a, b) => SearchIndex.CompareSizes(index, a.Row, a.Bytes, index, b.Row, b.Bytes));

    private static Int128 Read(SearchIndex index, int id, SpaceUsageIndex usage)
        => index.IsVisible(id) ? usage.DirectorySize(index.FileReferences![id]) ?? Unknown : Unknown;

    internal IEnumerable<int> Ordered(bool descending, CancellationToken token)
    {
        using var changed = (descending ? corrections.Reverse() : corrections).GetEnumerator();
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
