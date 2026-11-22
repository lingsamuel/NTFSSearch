using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

// 排列的生命周期跟随当前列或单次外部查询。卷更新后，缓存不阻碍已下线的索引视图回收。
internal sealed class SearchSortSelection(SortField field)
{
    private readonly object gate = new();
    private readonly ConditionalWeakTable<object, Entry> orders = new();
    private HashSet<SearchIndex>? currentOwners;
    internal SortField Field { get; } = field;

    internal void UpdateOwners(IEnumerable<SearchIndex> layers)
    {
        var next = layers.Where(index => !index.PrecomputeSort).Select(index => index.SortSource).ToHashSet();
        lock (gate)
        {
            if (currentOwners is not null)
                foreach (var owner in currentOwners)
                    if (!next.Contains(owner)) orders.Remove(owner);
            currentOwners = next;
        }
    }

    internal bool Contains(object owner) => orders.TryGetValue(owner, out var entry) && Volatile.Read(ref entry.Value) is not null;

    internal T Get<T>(object owner, Func<T> build, CancellationToken token) where T : class
    {
        Entry? entry;
        lock (gate)
            entry = owner is SearchIndex index && currentOwners is not null && !currentOwners.Contains(index)
                ? null : orders.GetValue(owner, static _ => new Entry());
        // 已开始的请求可以完成自己的快照。被替换的基线可能还用于身份解析，不能把排列重新放回当前列。
        if (entry is null) return build();
        if (Volatile.Read(ref entry.Value) is T ready) return ready;
        entry.Gate.Wait(token);
        try
        {
            if (entry.Value is T current) return current;
            var created = build();
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref entry.Value, created);
            return created;
        }
        finally { entry.Gate.Release(); }
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal object? Value;
    }
}
