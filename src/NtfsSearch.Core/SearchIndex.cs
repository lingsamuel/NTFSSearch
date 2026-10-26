using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    internal readonly NameArena Names;
    internal readonly IndexRecord[] Records;
    internal readonly string[] Directories;
    private readonly int[][] orders = new int[4][];
    private readonly int[] knownCounts = new int[4];
    public int Count => Records.Length;

    internal SearchIndex(NameArena names, IndexRecord[] records, string[] directories,
        CancellationToken cancellationToken, IProgress<string>? progress = null, int[][]? storedOrders = null, int[]? storedKnown = null)
    {
        Names = names; Records = records; Directories = directories;
        if (storedOrders is not null && storedKnown is not null)
        {
            Array.Copy(storedOrders, orders, 4); Array.Copy(storedKnown, knownCounts, 4);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("恢复缓存排序并校验…");
            return;
        }
        int[]? sortScratch = null;
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
            int[] order = Enumerable.Range(0, Count).ToArray();
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
            orders[(int)field] = order;
            int known = Count;
            while (known > 0 && !IsKnown(Records[order[known - 1]], field)) known--;
            knownCounts[(int)field] = known;
        }
        progress?.Report("排序索引已就绪。");
    }

    internal ReadOnlySpan<char> Name(int id) => Names.Get(Records[id].NameOffset, Records[id].NameLength);

    public SearchRow GetRow(int id)
    {
        ref readonly var r = ref Records[id];
        string name = Name(id).ToString();
        return new(id, name, Directories[r.Directory] + name, r.HasSize ? r.Size : null,
            r.HasModified ? r.Modified : null, r.IsDirectory);
    }

    public IndexStatistics Statistics => new(Count, Directories.Length, Names.CapacityBytes,
        (long)Count * Marshal.SizeOf<IndexRecord>(), orders.Distinct<int[]>(ReferenceEqualityComparer.Instance).Sum(a => (long)a.Length * sizeof(int)),
        Directories.Sum(p => (long)p.Length * sizeof(char) + 32));

    internal int[] SortOrder(SortField field) => orders[(int)field];
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
            SortField.Name => left.Name(a).CompareTo(right.Name(b), StringComparison.OrdinalIgnoreCase),
            SortField.Size => ak ? x.Size.CompareTo(y.Size) : 0,
            SortField.Modified => ak ? x.Modified.CompareTo(y.Modified) : 0,
            _ => 0
        };
        if (compare == 0) compare = ComparePath(left, a, right, b);
        if (compare == 0) compare = a.CompareTo(b);
        return descending && ak && bk ? -Math.Sign(compare) : compare;
    }

    // 比较两个“父路径 + 名称”视图。父路径有前缀关系时继续跨边界比较，不能只按父目录排序。
    private static int ComparePath(SearchIndex left, int a, SearchIndex right, int b)
    {
        ReadOnlySpan<char> ap = left.Directories[left.Records[a].Directory], bp = right.Directories[right.Records[b].Directory];
        ReadOnlySpan<char> an = left.Name(a), bn = right.Name(b);
        while (true)
        {
            if (ap.IsEmpty) { ap = an; an = default; }
            if (bp.IsEmpty) { bp = bn; bn = default; }
            if (ap.IsEmpty || bp.IsEmpty) return ap.IsEmpty ? bp.IsEmpty ? 0 : -1 : 1;
            int length = Math.Min(ap.Length, bp.Length);
            int c = ap[..length].CompareTo(bp[..length], StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
            ap = ap[length..]; bp = bp[length..];
        }
    }

    private int OrderedId(SortField field, bool descending, int position)
    {
        int known = knownCounts[(int)field];
        return orders[(int)field][descending && position < known ? known - 1 - position : position];
    }

}
