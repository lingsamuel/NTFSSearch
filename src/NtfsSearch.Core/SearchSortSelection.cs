using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

// 排列由搜索目录的列策略或单次外部查询持有。当前视图移除的排列仍可由已开始的请求读完。
internal sealed class SearchSortSelection(SortField field)
{
    private readonly object gate = new();
    private readonly ConditionalWeakTable<object, Entry> orders = new();
    private IReadOnlySet<object>? currentOwners;
    private object? warmSession;
    internal SortField Field { get; } = field;

    internal void UpdateOwners(IReadOnlySet<object> next)
    {
        lock (gate)
        {
            if (currentOwners is not null)
                foreach (var owner in currentOwners)
                    if (!next.Contains(owner)) orders.Remove(owner);
            currentOwners = next;
        }
    }

    internal void SetWarmSession(object? session)
    {
        lock (gate) Volatile.Write(ref warmSession, session);
    }

    internal bool Contains(object owner) => orders.TryGetValue(owner, out var entry) && Volatile.Read(ref entry.Value) is not null;

    internal T Get<T>(object owner, Func<T> build, CancellationToken token, Action? beforeColdSort = null) where T : class
        => Resolve(owner, build, token, session: null, beforeColdSort)!;

    internal void Warm<T>(object owner, Func<T> build, CancellationToken token, object session) where T : class
        => Resolve(owner, build, token, session);

    private T? Resolve<T>(object owner, Func<T> build, CancellationToken token, object? session,
        Action? beforeColdSort = null) where T : class
    {
        Entry? entry;
        lock (gate)
        {
            if (session is not null && !ReferenceEquals(session, warmSession)) return null;
            entry = currentOwners is null || currentOwners.Contains(owner)
                ? orders.GetValue(owner, static _ => new Entry()) : null;
        }
        // 已开始的请求可以完成自己的快照。被替换的基线可能还用于身份解析，不能把排列重新放回当前列。
        if (entry is not null && Volatile.Read(ref entry.Value) is T ready) return ready;
        beforeColdSort?.Invoke();
        if (entry is null) return session is null ? build() : null;
        entry.Gate.Wait(token);
        try
        {
            if (entry.Value is T current) return current;
            if (session is not null && !ReferenceEquals(session, Volatile.Read(ref warmSession))) return null;
            var created = build();
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                // 切换后保留当前列的已完成排列。旧补建任务即使完成，也不能在该列挂接新排列。
                if ((session is null || ReferenceEquals(session, warmSession))
                    && orders.TryGetValue(owner, out var attached) && ReferenceEquals(entry, attached))
                    Volatile.Write(ref entry.Value, created);
            }
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
