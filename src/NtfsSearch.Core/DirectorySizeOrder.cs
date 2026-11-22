using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 基线只存目录行。增量覆盖共享基线，查询可在后台更新期间保留完整的旧视图。
internal sealed class DirectorySizeOrder
{
    private static readonly Int128 Unknown = -1;
    private readonly int[] directoryRows;
    private readonly string[] baselinePaths;
    private readonly Int128[] sizes;
    private readonly int baselineKnownCount;
    private readonly int knownCount;
    private readonly int[]? order;
    private readonly object? baselineSortOwner;
    private readonly object? correctionSortOwner;
    private readonly ImmutableDictionary<int, Int128> overrides;
    private readonly ImmutableSortedSet<Entry>? corrections;
    internal SearchIndex Index { get; }
    internal bool HasKnownDirectories => knownCount != 0;
    internal object SortOwner => correctionSortOwner ?? baselineSortOwner ?? this;
    internal IEnumerable<object> SortOwners
    {
        get
        {
            if (baselineSortOwner is not null) yield return baselineSortOwner;
            if (correctionSortOwner is not null) yield return correctionSortOwner;
        }
    }

    internal bool IsPrecomputed(SearchSortSelection selection)
        => !HasKnownDirectories || order is not null
            || selection.Contains(baselineSortOwner!)
                && (correctionSortOwner is null || selection.Contains(correctionSortOwner));

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
        baselineKnownCount = knownCount;
        overrides = ImmutableDictionary<int, Int128>.Empty;
        if (index.PrecomputeSort)
        {
            order = BuildBaseline(token, () => usage.Checkpoint(token));
            corrections = ImmutableSortedSet.Create<Entry>(CompareEntries(index));
        }
        else baselineSortOwner = new();
    }

    private DirectorySizeOrder(SearchIndex index, DirectorySizeOrder baseline, ImmutableDictionary<int, Int128> overrides,
        int knownCount, ImmutableSortedSet<Entry>? corrections, bool preserveCorrectionOrder)
    {
        Index = index;
        directoryRows = baseline.directoryRows;
        baselinePaths = baseline.baselinePaths;
        sizes = baseline.sizes;
        baselineKnownCount = baseline.baselineKnownCount;
        order = baseline.order;
        this.overrides = overrides;
        this.knownCount = knownCount;
        this.corrections = corrections;
        baselineSortOwner = baseline.baselineSortOwner;
        if (baselineSortOwner is not null && !overrides.IsEmpty)
            correctionSortOwner = preserveCorrectionOrder ? baseline.correctionSortOwner : new();
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
        if (!samePaths)
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
            return index == Index ? this : new(index, this, overrides, knownCount, corrections, preserveCorrectionOrder: true);
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
            if (bytes == baseline && (bytes == Unknown || index.Directories[parent] == baselinePaths[parent]))
                next.Remove(id);
            else
            {
                next[id] = bytes;
                if (bytes != Unknown) sorted?.Add(new(id, bytes));
            }
        }
        if (next.Count > rebaseThreshold)
            return new(index, directoryRows, usage, token);
        var updated = next.ToImmutable();
        return new(index, this, updated, known, sorted?.ToImmutable(),
            preserveCorrectionOrder: !pathsChanged && ReferenceEquals(updated, overrides));
    }

    private static Comparer<Entry> CompareEntries(SearchIndex index)
        => Comparer<Entry>.Create((a, b) => SearchIndex.CompareSizes(index, a.Row, a.Bytes, index, b.Row, b.Bytes));

    private static Int128 Read(SearchIndex index, int id, SpaceUsageIndex usage)
        => index.IsVisible(id) ? usage.DirectorySize(index.FileReferences![id]) ?? Unknown : Unknown;

    private int[] BuildBaseline(CancellationToken token, Action? checkpoint = null)
    {
        if (baselineKnownCount == 0) return [];
        // 重排已切换路径的视图时，基线仍按原父路径比较，移动行由增量流覆盖。
        var baseline = ReferenceEquals(Index.Directories, baselinePaths) ? Index : Index.WithView(null, baselinePaths, token);
        var sorted = new int[baselineKnownCount];
        int position = 0;
        for (int slot = 0; slot < directoryRows.Length; slot++)
        {
            if ((slot & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
            }
            if (sizes[slot] != Unknown) sorted[position++] = slot;
        }
        return SortIndex.Sort(sorted, (a, b) => SearchIndex.CompareSizes(baseline, directoryRows[a], sizes[a],
            baseline, directoryRows[b], sizes[b]), token, maxParallelism: 1, checkpoint: checkpoint);
    }

    private int[] BuildCorrections(CancellationToken token, Action? checkpoint = null)
    {
        var rows = new List<int>(overrides.Count);
        int checkedRows = 0;
        foreach (var (id, bytes) in overrides)
        {
            if ((checkedRows++ & 4095) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
            }
            if (bytes != Unknown) rows.Add(id);
        }
        return SortIndex.Sort(rows.ToArray(), (a, b) => SearchIndex.CompareSizes(Index, a, overrides[a],
            Index, b, overrides[b]), token, maxParallelism: 1, checkpoint: checkpoint);
    }

    internal void WarmSort(SearchSortSelection selection, object session, CancellationToken token, Action? checkpoint)
    {
        selection.Warm(baselineSortOwner!, () => BuildBaseline(token, checkpoint), token, session);
        if (correctionSortOwner is not null)
            selection.Warm(correctionSortOwner, () => BuildCorrections(token, checkpoint), token, session);
    }

    internal IEnumerable<int> Ordered(bool descending, CancellationToken token, SearchSortSelection? selection = null)
    {
        token.ThrowIfCancellationRequested();
        if (knownCount == 0) yield break;
        // 按需模式的全量基线和稀疏修正都由当前列持有，汇总视图只保存大小与比较所需数据。
        var baseline = order ?? selection?.Get(baselineSortOwner!, () => BuildBaseline(token), token) ?? BuildBaseline(token);
        if (overrides.IsEmpty)
        {
            for (int i = 0; i < baseline.Length; i++)
            {
                if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                yield return directoryRows[baseline[descending ? baseline.Length - 1 - i : i]];
            }
            yield break;
        }

        var corrected = corrections is null ? SelectedCorrections() : descending ? corrections.Reverse() : corrections;
        using var changed = corrected.GetEnumerator();
        bool hasChanged = changed.MoveNext();
        int position = 0;
        while (position < baseline.Length || hasChanged)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            if (position == baseline.Length)
            {
                yield return changed.Current.Row;
                hasChanged = changed.MoveNext();
                continue;
            }
            int slot = baseline[descending ? baseline.Length - 1 - position : position];
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

        IEnumerable<Entry> SelectedCorrections()
        {
            var rows = selection?.Get(correctionSortOwner!, () => BuildCorrections(token), token) ?? BuildCorrections(token);
            for (int i = 0; i < rows.Length; i++)
            {
                int id = rows[descending ? rows.Length - 1 - i : i];
                yield return new(id, overrides[id]);
            }
        }
    }

    private readonly record struct Entry(int Row, Int128 Bytes);
}
