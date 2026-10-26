using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    internal readonly NameArena Names;
    internal readonly IndexRecord[] Records;
    internal readonly long[] BigSizes;
    internal readonly string[] Directories;
    private readonly SortPermutation[] orders = new SortPermutation[4];
    private readonly int[] knownCounts = new int[4];
    private int[][]? corrections;
    private int[]? correctionKnown;
    private bool[]? correctionDirectories;
    internal bool HasOrderCorrections => corrections is not null;
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
            fileReferences: FileReferences, directoryReferences: DirectoryReferences, hiddenRows: hidden, storageId: storageId ?? StorageId, bigSizes: BigSizes);
        view.identitySource = IdentitySource;
        if (!ReferenceEquals(directories, Directories))
        {
            // 以最初快照排序为参照累计修正，只保留受影响ID，查询时归并两个有序流。
            // 小目录改名不需要复制四份全卷排序数组。
            var source = IdentitySource;
            var changed = new bool[directories.Length];
            for (int d = 0; d < changed.Length; d++) changed[d] = directories[d] != source.Directories[d];
            var affected = new List<int>();
            for (int id = 0; id < Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                if (changed[Records[id].Directory]) affected.Add(id);
            }
            if (affected.Count > 0)
            {
                view.corrections = new int[4][]; view.correctionKnown = new int[4]; view.correctionDirectories = changed;
                for (int field = 0; field < 4; field++)
                {
                    token.ThrowIfCancellationRequested();
                    var sorted = affected.ToArray();
                    SortIndex.Sort(sorted, (a, b) => view.Compare(a, b, (SortField)field), token);
                    view.corrections[field] = sorted;
                    int known = sorted.Length;
                    while (known > 0 && !IsKnown(Records[sorted[known - 1]], (SortField)field)) known--;
                    view.correctionKnown[field] = known;
                }
            }
        }
        else
        {
            view.corrections = corrections; view.correctionKnown = correctionKnown; view.correctionDirectories = correctionDirectories;
        }
        return view;
    }

    internal SearchIndex(NameArena names, IndexRecord[] records, string[] directories,
        CancellationToken cancellationToken, IProgress<string>? progress = null, SortPermutation[]? storedOrders = null, int[]? storedKnown = null,
        ulong[]? fileReferences = null, ulong[]? directoryReferences = null, ulong[]? hiddenRows = null, string? storageId = null, long[]? bigSizes = null)
    {
        Names = names; Records = records; Directories = directories;
        BigSizes = bigSizes ?? [];
        FileReferences = fileReferences; DirectoryReferences = directoryReferences; HiddenRows = hiddenRows;
        StorageId = storageId ?? Guid.NewGuid().ToString("N");
        VisibleCount = Count - (hiddenRows?.Sum(w => System.Numerics.BitOperations.PopCount(w)) ?? 0);
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
            if ((field == SortField.Size && !Records.Any(r => r.HasSize))
                || (field == SortField.Modified && !Records.Any(r => r.HasModified)))
            {
                orders[(int)field] = orders[(int)SortField.Path];
                knownCounts[(int)field] = 0;
                continue;
            }
            int[] order = SortPermutation.UsesPackedStorage(Count)
                ? temporaryOrder ??= new int[Count] : IndexArrays.Persistent<int>(Count);
            for (int id = 0; id < Count; id++) order[id] = id;
            bool sorted = true;
            for (int i = 1; i < Count; i++)
            {
                if ((i & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (Compare(order[i - 1], order[i], field) > 0) { sorted = false; break; }
            }
            if (!sorted)
            {
                // 四列复用同一份临时排列，避免大对象堆留下四份待回收的巨型数组。
                if (Count > 1_000_000) sortScratch ??= new int[Count];
                order = SortIndex.Sort(order, (a, b) => Compare(a, b, field), cancellationToken, sortScratch);
            }
            orders[(int)field] = SortPermutation.TakeSorted(order, cancellationToken);
            int known = Count;
            while (known > 0 && !IsKnown(Records[order[known - 1]], field)) known--;
            knownCounts[(int)field] = known;
        }
        progress?.Report("排序索引已就绪。");
    }

    internal ReadOnlySpan<char> Name(int id, Span<char> scratch) => Names.Get(Records[id].NameId, scratch);
    internal ReadOnlyMemory<char> NameMemory(int id) => Names.GetMemory(Records[id].NameId);
    internal long Size(int id)
    {
        uint code = Records[id].SizeCode;
        return code <= int.MaxValue ? code : code == uint.MaxValue ? -1 : BigSizes[(int)(code & int.MaxValue)];
    }

    public SearchRow GetRow(int id)
    {
        ref readonly var r = ref Records[id];
        Span<char> scratch = stackalloc char[255];
        string name = Name(id, scratch).ToString();
        return new(id, name, Directories[r.Directory] + name, r.HasSize ? Size(id) : null,
            r.HasModified ? r.Modified : null, r.IsDirectory);
    }

    public IndexStatistics Statistics => new(Count, Directories.Length, Names.CapacityBytes,
        (long)Count * Marshal.SizeOf<IndexRecord>() + (long)BigSizes.Length * sizeof(long), orders.Distinct<SortPermutation>(ReferenceEqualityComparer.Instance).Sum(a => a.StorageBytes),
        Directories.Sum(p => (long)p.Length * sizeof(char) + 32));

    internal SortPermutation SortOrder(SortField field) => orders[(int)field];
    internal IEnumerable<int> EnumerateSortOrder(SortField field, CancellationToken token)
        => EnumerateOrdered(field, false, token);

    private IEnumerable<int> EnumerateOrdered(SortField field, bool descending, CancellationToken token)
    {
        int f = (int)field;
        if (corrections is null)
        {
            var order = orders[f];
            int known = knownCounts[f];
            for (int position = 0; position < Count; position++)
            {
                if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
                yield return order[descending && position < known ? known - 1 - position : position];
            }
            yield break;
        }
        int old = 0, next = 0, length = corrections[f].Length;
        for (int position = 0; position < Count; position++)
        {
            if ((position & 4095) == 0) token.ThrowIfCancellationRequested();
            while (old < Count && correctionDirectories![Records[OrderedId(field, descending, old)].Directory])
            {
                if ((old & 4095) == 0) token.ThrowIfCancellationRequested();
                old++;
            }
            int corrected = next < length ? corrections[f][descending && next < correctionKnown![f] ? correctionKnown[f] - 1 - next : next] : -1;
            if (next < length && (old == Count || CompareAcross(this, corrected, this, OrderedId(field, descending, old), field, descending) <= 0))
            { next++; yield return corrected; }
            else { yield return OrderedId(field, descending, old); old++; }
        }
    }
    internal int KnownCount(SortField field) => knownCounts[(int)field];

    internal static bool IsKnown(in IndexRecord r, SortField field) => field switch
    {
        SortField.Size => r.HasSize,
        SortField.Modified => r.HasModified,
        _ => true
    };

    internal int Compare(int a, int b, SortField field) => CompareAcross(this, a, this, b, field, false);

    internal static int CompareAcross(SearchIndex left, int a, SearchIndex right, int b, SortField field, bool descending)
    {
        ref readonly var x = ref left.Records[a]; ref readonly var y = ref right.Records[b];
        bool ak = IsKnown(x, field), bk = IsKnown(y, field);
        if (ak != bk) return ak ? -1 : 1;
        int compare = field switch
        {
            SortField.Name => CompareNames(left, a, right, b),
            SortField.Size => ak ? left.Size(a).CompareTo(right.Size(b)) : 0,
            SortField.Modified => ak ? x.Modified.CompareTo(y.Modified) : 0,
            _ => 0
        };
        if (compare == 0) compare = ComparePath(left, a, right, b);
        if (compare == 0) compare = a.CompareTo(b);
        return descending && ak && bk ? -Math.Sign(compare) : compare;
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
    private int OrderedId(SortField field, bool descending, int position)
    {
        int known = knownCounts[(int)field];
        return orders[(int)field][descending && position < known ? known - 1 - position : position];
    }

}
