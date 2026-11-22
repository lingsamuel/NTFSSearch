using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    internal readonly NameArena Names;
    internal readonly IndexRecord[] Records;
    internal IndexMetadata Metadata { get; }
    // 静态快照可能早于某个字段。实时更新仍按 Metadata 采集，另存退出快照时才检查已有覆盖。
    internal IndexMetadata AvailableMetadata { get; }
    internal bool PrecomputeSort { get; }
    private SearchIndex? sortSource;
    internal SearchIndex SortSource => sortSource ?? this;
    private SearchIndex? correctionBaseline;
    internal SearchIndex BaselineSortSource => correctionBaseline ?? SortSource;
    internal readonly uint[]? SizeCodes;
    internal readonly long[]? ModifiedTimes;
    internal readonly long[] BigSizes;
    // 两列同时存在。独立位图标记缺失值，时间列保留完整的64位值域。
    internal readonly long[]? CreationTimes;
    internal readonly ulong[]? KnownCreationTimes;
    internal readonly string[] Directories;
    private readonly SortPermutation[] orders = new SortPermutation[4];
    private readonly int[] knownCounts = new int[4];
    private int[][]? corrections;
    private int[]? correctionKnown;
    private int[]? correctionRows;
    private bool[]? correctionDirectories;
    internal bool HasOrderCorrections => corrections is not null || correctionRows is not null;
    public int Count => Records.Length;
    public int VisibleCount { get; private set; }
    public bool HasIdentity => FileReferences is not null && DirectoryReferences is not null;
    internal readonly ulong[]? FileReferences;
    internal readonly ulong[]? DirectoryReferences;
    internal readonly ulong[]? HiddenRows;
    internal readonly string StorageId;
    private SearchIndex? identitySource;
    internal SearchIndex IdentitySource => identitySource ?? this;
    internal byte[]? CheckpointFingerprint;
    internal byte[]? CanonicalCheckpointFingerprint;
    internal bool IsVisible(int id) => HiddenRows is null || (HiddenRows[id / 64] & (1UL << (id % 64))) == 0;
    internal static int CompareReference(ulong a, ulong b)
    {
        int c = (a & 0x0000FFFFFFFFFFFFUL).CompareTo(b & 0x0000FFFFFFFFFFFFUL);
        return c != 0 ? c : a.CompareTo(b);
    }
    public IReadOnlyList<int> FindFileRows(ulong reference)
    {
        var range = FindFileRange(reference);
        return range.Count == 0 ? Array.Empty<int>() : new RowRange(range.Start, range.Count);
    }
    internal (int Start, int Count) FindFileRange(ulong reference)
    {
        if (FileReferences is null) return (0, 0);
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (CompareReference(FileReferences[mid], reference) < 0) lo = mid + 1; else hi = mid;
        }
        int end = lo;
        while (end < Count && FileReferences[end] == reference) end++;
        return (lo, end - lo);
    }
    internal IReadOnlyList<int> FindRecordRows(ulong reference)
    {
        if (FileReferences is null) return Array.Empty<int>();
        ulong record = reference & 0x0000FFFFFFFFFFFFUL;
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if ((FileReferences[mid] & 0x0000FFFFFFFFFFFFUL) < record) lo = mid + 1; else hi = mid;
        }
        int end = lo;
        while (end < Count && (FileReferences[end] & 0x0000FFFFFFFFFFFFUL) == record) end++;
        return new RowRange(lo, end - lo);
    }
    private sealed class RowRange(int start, int count) : IReadOnlyList<int>
    {
        public int Count => count;
        public int this[int index] => index >= 0 && index < count ? start + index : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<int> GetEnumerator() { for (int i = 0; i < count; i++) yield return start + i; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    internal SearchIndex WithView(ulong[]? hidden, string[] directories, CancellationToken token, string? storageId = null)
    {
        var view = new SearchIndex(Names, Records, directories, token, storedOrders: orders, storedKnown: knownCounts,
            fileReferences: FileReferences, directoryReferences: DirectoryReferences, hiddenRows: hidden, storageId: storageId ?? StorageId, bigSizes: BigSizes,
            creationTimes: CreationTimes, knownCreationTimes: KnownCreationTimes,
            sizeCodes: SizeCodes, modifiedTimes: ModifiedTimes, metadata: Metadata, precomputeSort: PrecomputeSort,
            availableMetadata: AvailableMetadata);
        view.identitySource = IdentitySource;
        if (!ReferenceEquals(directories, Directories))
        {
            // 以完整排列所在视图为参照累计修正。小目录改名只需排序受影响的行。
            var source = PrecomputeSort ? IdentitySource : BaselineSortSource;
            var changed = new bool[directories.Length];
            for (int d = 0; d < changed.Length; d++) changed[d] = directories[d] != source.Directories[d];
            var affected = new List<int>();
            for (int id = 0; id < Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                if (!changed[Records[id].Directory]) continue;
                // 大范围改名直接换用新基线，避免同时缓存两份接近整卷的排列。
                if (!PrecomputeSort && affected.Count >= Count / 2) return view;
                affected.Add(id);
            }
            if (!PrecomputeSort)
            {
                if (affected.Count == 0) view.sortSource = source;
                else
                {
                    view.correctionBaseline = source;
                    view.correctionRows = affected.ToArray();
                    view.correctionDirectories = changed;
                }
            }
            else if (affected.Count > 0)
            {
                view.corrections = new int[4][]; view.correctionKnown = new int[4]; view.correctionDirectories = changed;
                for (int field = 0; field < 4; field++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!HasSortableMetadata((SortField)field))
                    {
                        view.corrections[field] = view.corrections[(int)SortField.Path];
                        continue;
                    }
                    var sorted = view.BuildOrderCorrection(affected.ToArray(), (SortField)field, token);
                    view.corrections[field] = sorted.Order;
                    view.correctionKnown[field] = sorted.Known;
                }
            }
        }
        else
        {
            if (!PrecomputeSort) view.sortSource = SortSource;
            view.corrections = corrections; view.correctionKnown = correctionKnown; view.correctionDirectories = correctionDirectories;
            view.correctionRows = correctionRows; view.correctionBaseline = correctionBaseline;
        }
        return view;
    }

    internal SearchIndex(NameArena names, IndexRecord[] records, string[] directories,
        CancellationToken cancellationToken, IProgress<string>? progress = null, SortPermutation[]? storedOrders = null, int[]? storedKnown = null,
        ulong[]? fileReferences = null, ulong[]? directoryReferences = null, ulong[]? hiddenRows = null, string? storageId = null, long[]? bigSizes = null,
        long[]? creationTimes = null, ulong[]? knownCreationTimes = null, uint[]? sizeCodes = null, long[]? modifiedTimes = null,
        IndexMetadata metadata = IndexMetadata.All, bool precomputeSort = true, IndexMetadata? availableMetadata = null)
    {
        Names = names; Records = records; Directories = directories;
        Metadata = metadata;
        AvailableMetadata = metadata & (availableMetadata ?? IndexMetadata.All);
        PrecomputeSort = precomputeSort;
        SizeCodes = sizeCodes; ModifiedTimes = modifiedTimes; BigSizes = bigSizes ?? [];
        CreationTimes = creationTimes; KnownCreationTimes = knownCreationTimes;
        FileReferences = fileReferences; DirectoryReferences = directoryReferences; HiddenRows = hiddenRows;
        StorageId = storageId ?? Guid.NewGuid().ToString("N");
        VisibleCount = Count - (hiddenRows?.Sum(w => System.Numerics.BitOperations.PopCount(w)) ?? 0);
        if (!precomputeSort) return;
        if (storedOrders is not null && storedKnown is not null)
        {
            Array.Copy(storedOrders, orders, 4); Array.Copy(storedKnown, knownCounts, 4);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("恢复缓存排序并校验…");
            return;
        }
        int[]? sortScratch = null, temporaryOrder = null;
        foreach (var field in Enum.GetValues<SortField>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"建立 {field} 排序（{Count:N0} 条，可取消）…");
            if (!HasSortableMetadata(field))
            {
                orders[(int)field] = orders[(int)SortField.Path];
                continue;
            }
            var sorted = BuildSortOrder(field, cancellationToken, ref sortScratch, ref temporaryOrder);
            orders[(int)field] = sorted.Order;
            knownCounts[(int)field] = sorted.Known;
        }
        progress?.Report("排序索引已就绪。");
    }

    private bool HasSortableMetadata(SortField field) => field switch
    {
        SortField.Size => SizeCodes is not null && Array.Exists(SizeCodes, static code => code != uint.MaxValue),
        SortField.Modified => ModifiedTimes is not null && Array.Exists(Records, static row => row.HasModified),
        _ => true
    };

    internal bool IsSortAvailable(SortField field) => field switch
    {
        SortField.Size => (Metadata & IndexMetadata.FileSize) != 0,
        SortField.Modified => (Metadata & IndexMetadata.ModifiedTime) != 0,
        _ => true
    };

    private sealed record RowSortOrder(SortPermutation Order, int Known);

    private RowSortOrder BuildSortOrder(SortField field, CancellationToken token, Action? checkpoint = null)
    {
        int[]? scratch = null, temporary = null;
        return BuildSortOrder(field, token, ref scratch, ref temporary, checkpoint);
    }

    private RowSortOrder BuildSortOrder(SortField field, CancellationToken token,
        ref int[]? sortScratch, ref int[]? temporaryOrder, Action? checkpoint = null)
    {
        int[] order = SortPermutation.UsesPackedStorage(Count)
            ? temporaryOrder ??= new int[Count] : IndexArrays.Persistent<int>(Count);
        for (int id = 0; id < Count; id++)
        {
            if ((id & 65535) == 0) Checkpoint();
            order[id] = id;
        }
        bool sorted = true;
        for (int i = 1; i < Count; i++)
        {
            if ((i & 65535) == 0) Checkpoint();
            if (Compare(order[i - 1], order[i], field) > 0) { sorted = false; break; }
        }
        if (!sorted)
        {
            // 多列预计算复用临时排列和归并缓冲，减少构建期间分配的大数组。
            if (Count > 1_000_000) sortScratch ??= new int[Count];
            order = SortIndex.Sort(order, (a, b) => Compare(a, b, field), token, sortScratch, checkpoint: checkpoint);
        }
        var frozen = SortPermutation.TakeSorted(order, token);
        return new(frozen, CountKnownRows(order, field, token, checkpoint));

        void Checkpoint()
        {
            token.ThrowIfCancellationRequested();
            checkpoint?.Invoke();
        }
    }

    internal ReadOnlySpan<char> Name(int id, Span<char> scratch) => Names.Get(Records[id].NameId, scratch);
    internal ReadOnlyMemory<char> NameMemory(int id) => Names.GetMemory(Records[id].NameId);
    internal bool HasSize(int id) => SizeCodes is not null && SizeCodes[id] != uint.MaxValue;
    internal bool HasNonzeroSize(int id) => SizeCodes is not null && SizeCodes[id] is > 0 and < uint.MaxValue;
    internal bool HasModified(int id) => Records[id].HasModified;
    internal long Modified(int id) => ModifiedTimes?[id] ?? 0;
    internal long Size(int id)
    {
        uint code = SizeCodes?[id] ?? uint.MaxValue;
        return code <= int.MaxValue ? code : code == uint.MaxValue ? -1 : BigSizes[(int)(code & int.MaxValue)];
    }

    internal long? CreatedUtcTicks(int id) => ReadCreationTime(CreationTimes, KnownCreationTimes, id);
    internal static long? ReadCreationTime(long[]? times, ulong[]? known, int id)
        => known is not null && (known[id / 64] & (1UL << (id % 64))) != 0 ? times![id] : null;

    public SearchRow GetRow(int id)
    {
        ref readonly var r = ref Records[id];
        Span<char> scratch = stackalloc char[255];
        string name = Name(id, scratch).ToString();
        return new(id, name, Directories[r.Directory] + name, HasSize(id) ? Size(id) : null,
            HasModified(id) ? Modified(id) : null, r.IsDirectory) { CreatedUtcTicks = CreatedUtcTicks(id) };
    }

    public IndexStatistics Statistics => new(Count, Directories.Length, Names.CapacityBytes,
        (long)Count * Unsafe.SizeOf<IndexRecord>() + (long)BigSizes.Length * sizeof(long)
            + (long)(SizeCodes?.Length ?? 0) * sizeof(uint) + (long)(ModifiedTimes?.Length ?? 0) * sizeof(long)
            + (long)(CreationTimes?.Length ?? 0) * sizeof(long) + (long)(KnownCreationTimes?.Length ?? 0) * sizeof(ulong),
        PrecomputeSort ? orders.Distinct<SortPermutation>(ReferenceEqualityComparer.Instance).Sum(a => a.StorageBytes) : 0,
        Directories.Sum(p => (long)p.Length * sizeof(char) + 32));

    internal (SortPermutation Order, int Known) ResolveSortOrder(SortField field, CancellationToken token,
        SearchSortSelection? selection = null, Action? beforeColdSort = null)
    {
        if (correctionRows is null) return ResolveBaselineSortOrder(field, token, selection, beforeColdSort);
        // 需要连续排列的调用方仍可取得对应视图的完整顺序。普通查询直接归并两路。
        beforeColdSort?.Invoke();
        var sorted = SortSource.BuildSortOrder(field, token);
        return (sorted.Order, sorted.Known);
    }

    internal (SortPermutation Order, int Known) ResolveBaselineSortOrder(SortField field, CancellationToken token,
        SearchSortSelection? selection = null, Action? beforeColdSort = null)
    {
        int f = (int)field;
        if (PrecomputeSort) return (orders[f], knownCounts[f]);
        var source = BaselineSortSource;
        if (selection is null) beforeColdSort?.Invoke();
        var sorted = selection is null ? source.BuildSortOrder(field, token)
            : selection.Get(source, () => source.BuildSortOrder(field, token), token, beforeColdSort);
        return (sorted.Order, sorted.Known);
    }

    internal void WarmSort(SearchSortSelection selection, object session, CancellationToken token, Action? checkpoint)
    {
        var source = BaselineSortSource;
        selection.Warm(source, () => source.BuildSortOrder(selection.Field, token, checkpoint), token, session);
        if (correctionRows is not null)
        {
            var view = SortSource;
            selection.Warm(view, () => view.BuildSparseCorrection(selection.Field, token, checkpoint), token, session);
        }
    }

    internal bool IsSortCached(SearchSortSelection selection) => selection.Contains(BaselineSortSource)
        && (correctionRows is null || selection.Contains(SortSource));

    private sealed record RowSortCorrection(int[] Order, int Known);

    private RowSortCorrection BuildSparseCorrection(SortField field, CancellationToken token, Action? checkpoint = null)
        => BuildOrderCorrection((int[])correctionRows!.Clone(), field, token, checkpoint);

    private RowSortCorrection BuildOrderCorrection(int[] rows, SortField field, CancellationToken token, Action? checkpoint = null)
    {
        checkpoint?.Invoke();
        var order = SortIndex.Sort(rows, (a, b) => Compare(a, b, field), token, checkpoint: checkpoint);
        return new(order, CountKnownRows(order, field, token, checkpoint));
    }

    private int CountKnownRows(int[] order, SortField field, CancellationToken token, Action? checkpoint)
    {
        int known = order.Length;
        while (known > 0 && !IsKnown(order[known - 1], field))
        {
            if ((known & 65535) == 0)
            {
                token.ThrowIfCancellationRequested();
                checkpoint?.Invoke();
            }
            known--;
        }
        return known;
    }

    private (int[] Order, int Known) ResolveOrderCorrection(SortField field, CancellationToken token,
        SearchSortSelection? selection, Action? beforeColdSort)
    {
        int f = (int)field;
        if (corrections is not null) return (corrections[f], correctionKnown![f]);
        var view = SortSource;
        if (selection is null) beforeColdSort?.Invoke();
        var sorted = selection is null ? view.BuildSparseCorrection(field, token)
            : selection.Get(view, () => view.BuildSparseCorrection(field, token), token, beforeColdSort);
        return (sorted.Order, sorted.Known);
    }

    internal SortPermutation SortOrder(SortField field, CancellationToken token = default) => ResolveSortOrder(field, token).Order;
    internal IEnumerable<int> EnumerateSortOrder(SortField field, CancellationToken token, SearchSortSelection? selection = null)
        => EnumerateOrdered(field, false, token, selection: selection);

    private IEnumerable<int> EnumerateOrdered(SortField field, bool descending, CancellationToken token, bool? knownOnly = null,
        SearchSortSelection? selection = null, Action? beforeColdSort = null)
    {
        var (order, known) = ResolveBaselineSortOrder(field, token, selection, beforeColdSort);
        int start = knownOnly == false ? known : 0;
        int end = knownOnly == true ? known : Count;
        if (!HasOrderCorrections)
        {
            for (int position = start; position < end; position++)
            {
                if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
                yield return ReadOrderId(order, known, descending, position);
            }
            yield break;
        }
        var (changed, changedKnown) = ResolveOrderCorrection(field, token, selection, beforeColdSort);
        int old = start, next = knownOnly == false ? changedKnown : 0;
        int correctionsEnd = knownOnly == true ? changedKnown : changed.Length;
        for (int position = 0; old < end || next < correctionsEnd; position++)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            while (old < end && correctionDirectories![Records[ReadOrderId(order, known, descending, old)].Directory])
            {
                if ((old & 4095) == 0) token.ThrowIfCancellationRequested();
                old++;
            }
            int corrected = next < correctionsEnd ? changed[descending && next < changedKnown ? changedKnown - 1 - next : next] : -1;
            if (next < correctionsEnd && (old == end || CompareAcross(this, corrected, this, ReadOrderId(order, known, descending, old), field, descending) <= 0))
            { next++; yield return corrected; }
            else if (old < end) { yield return ReadOrderId(order, known, descending, old); old++; }
        }
    }
    internal int KnownCount(SortField field) => ResolveBaselineSortOrder(field, default).Known;

    internal bool IsKnown(int id, SortField field) => field switch
    {
        SortField.Size => HasSize(id),
        SortField.Modified => HasModified(id),
        _ => true
    };

    internal int Compare(int a, int b, SortField field) => CompareAcross(this, a, this, b, field, false);

    internal static int CompareAcross(SearchIndex left, int a, SearchIndex right, int b, SortField field, bool descending)
    {
        bool ak = left.IsKnown(a, field), bk = right.IsKnown(b, field);
        if (ak != bk) return ak ? -1 : 1;
        int compare = field switch
        {
            SortField.Name => CompareNames(left, a, right, b),
            SortField.Size => ak ? left.Size(a).CompareTo(right.Size(b)) : 0,
            SortField.Modified => ak ? left.Modified(a).CompareTo(right.Modified(b)) : 0,
            _ => 0
        };
        compare = ComparePathAndRow(left, a, right, b, compare);
        return descending && ak && bk ? -Math.Sign(compare) : compare;
    }

    internal Int128? EffectiveSize(int id, DirectorySizeOrder? directories)
        => Records[id].IsDirectory ? directories?.GetSize(id) : HasSize(id) ? Size(id) : null;

    internal static int CompareSizes(SearchIndex left, int a, Int128? leftSize,
        SearchIndex right, int b, Int128? rightSize, bool descending = false)
    {
        if (leftSize.HasValue != rightSize.HasValue) return leftSize.HasValue ? -1 : 1;
        int comparison = ComparePathAndRow(left, a, right, b,
            leftSize.HasValue ? leftSize.Value.CompareTo(rightSize.GetValueOrDefault()) : 0);
        return descending && leftSize.HasValue ? -Math.Sign(comparison) : comparison;
    }

    private static int ComparePathAndRow(SearchIndex left, int a, SearchIndex right, int b, int comparison)
    {
        if (comparison == 0) comparison = ComparePath(left, a, right, b);
        return comparison == 0 ? a.CompareTo(b) : comparison;
    }

    private static int CompareNames(SearchIndex left, int a, SearchIndex right, int b)
    {
        if (ReferenceEquals(left.Names, right.Names) && left.Records[a].NameId == right.Records[b].NameId) return 0;
        Span<char> leftScratch = stackalloc char[255], rightScratch = stackalloc char[255];
        return left.Name(a, leftScratch).CompareTo(right.Name(b, rightScratch), StringComparison.OrdinalIgnoreCase);
    }

    // 比较两个“父路径 + 名称”视图。父路径有前缀关系时继续跨边界比较，不能只按父目录排序。
    private static int ComparePath(SearchIndex left, int a, SearchIndex right, int b)
    {
        scoped ReadOnlySpan<char> ap = left.Directories[left.Records[a].Directory], bp = right.Directories[right.Records[b].Directory];
        Span<char> leftScratch = stackalloc char[255], rightScratch = stackalloc char[255];
        bool nameLeftUsed = false, nameRightUsed = false;
        while (true)
        {
            if (ap.IsEmpty && !nameLeftUsed) { ap = left.Name(a, leftScratch); nameLeftUsed = true; }
            if (bp.IsEmpty && !nameRightUsed) { bp = right.Name(b, rightScratch); nameRightUsed = true; }
            if (ap.IsEmpty || bp.IsEmpty) return ap.IsEmpty ? bp.IsEmpty ? 0 : -1 : 1;
            int length = Math.Min(ap.Length, bp.Length);
            int c = ap[..length].CompareTo(bp[..length], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            ap = ap[length..]; bp = bp[length..];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadOrderId(SortPermutation order, int known, bool descending, int position)
        => order[descending && position < known ? known - 1 - position : position];

}
