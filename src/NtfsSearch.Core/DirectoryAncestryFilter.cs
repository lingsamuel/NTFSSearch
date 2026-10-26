using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 单卷单写者复用工作区；祖先缓存有界，避免为每次目录变化建立全卷身份字典。
internal sealed class DirectoryAncestryFilter
{
    private const int CacheCapacity = 4096;
    private readonly Dictionary<ulong, bool> cached = new(CacheCapacity);
    private readonly List<ulong> chain = [];
    private readonly HashSet<ulong> visiting = [];

    internal void Reset() => cached.Clear();

    internal bool Contains(ulong reference, ImmutableDictionary<ulong, LiveVolumeIndex.DirectoryNode> graph,
        HashSet<ulong> changed, CancellationToken token)
    {
        chain.Clear(); visiting.Clear();
        ulong cursor = reference;
        bool affected;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (cached.TryGetValue(cursor, out affected)) break;
            if (changed.Contains(cursor)) { affected = true; break; }
            if (!visiting.Add(cursor)) throw new InvalidDataException("目录身份图包含循环。");
            chain.Add(cursor);
            if (!graph.TryGetValue(cursor, out var node) || node.Deleted || node.RootPath is not null)
            { affected = false; break; }
            cursor = node.Parent;
        }
        // 清空满缓存只影响命中率；整条祖先链仍采用本次已求得的同一结果。
        foreach (ulong ancestor in chain)
        {
            if (cached.Count == CacheCapacity) cached.Clear();
            cached[ancestor] = affected;
        }
        return affected;
    }
}
