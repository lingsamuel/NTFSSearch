using System.Diagnostics;

namespace NtfsSearch.Core;

public sealed class SearchCatalog
{
    private readonly object gate = new();
    private readonly Dictionary<string, SearchIndex> indexes = new(StringComparer.Ordinal);
    private KeyValuePair<string, SearchIndex>[] published = [];
    private long version;
    public long Count => Volatile.Read(ref published).Sum(p => (long)p.Value.Count);
    public int VolumeCount => Volatile.Read(ref published).Length;
    public long Version => Interlocked.Read(ref version);
    public event Action? Changed;

    public void Publish(string volumeId, SearchIndex index)
    {
        lock (gate)
        {
            indexes[volumeId] = index;
            Volatile.Write(ref published, indexes.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
            Interlocked.Increment(ref version);
        }
        Changed?.Invoke();
    }

    public void RemoveExcept(IEnumerable<string> volumeIds)
    {
        var keep = volumeIds.ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        lock (gate)
        {
            foreach (string key in indexes.Keys.Where(k => !keep.Contains(k)).ToArray()) changed |= indexes.Remove(key);
            if (changed)
            {
                Volatile.Write(ref published, indexes.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray());
                Interlocked.Increment(ref version);
            }
        }
        if (changed) Changed?.Invoke();
    }

    public SearchResult Search(string text, SortField sort = SortField.Name, bool descending = false,
        bool matchPath = true, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        SearchIndex.ValidateRequest(text, sort, offset, limit, cancellationToken);
        var timer = Stopwatch.StartNew();
        var sources = Volatile.Read(ref published);
        var cursors = new List<Cursor>();
        var queries = new List<PreparedSearch>();
        var queue = new PriorityQueue<Cursor, Priority>(Comparer<Priority>.Create((a, b) =>
        {
            int comparison = SearchIndex.CompareAcross(a.Index, a.Id, b.Index, b.Id, sort, descending);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.Volume, b.Volume);
        }));
        try
        {
            long total = 0;
            foreach (var source in sources)
            {
                // 每卷只匹配和计数一次。查询绑定发布时的数组，后续卷版本变化不会改变本次结果。
                var query = source.Value.Prepare(text, matchPath, cancellationToken);
                queries.Add(query); total += query.TotalCount;
                if (query.TotalCount == 0 || limit == 0) continue;
                var cursor = new Cursor(source.Key, source.Value, query.Ids(sort, descending, cancellationToken).GetEnumerator());
                cursors.Add(cursor);
                if (cursor.Ids.MoveNext()) queue.Enqueue(cursor, cursor.Priority);
            }
            var rows = new List<SearchRow>(Math.Min(limit, 1024));
            long skipped = 0;
            while (rows.Count < limit && queue.TryDequeue(out var cursor, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skipped++ >= offset) rows.Add(cursor.Index.GetRow(cursor.Ids.Current));
                if (rows.Count == limit) break;
                if (cursor.Ids.MoveNext()) queue.Enqueue(cursor, cursor.Priority);
            }
            return new(total, rows, timer.Elapsed);
        }
        finally
        {
            foreach (var cursor in cursors) cursor.Ids.Dispose();
            foreach (var query in queries) query.Dispose();
        }
    }

    private readonly record struct Priority(string Volume, SearchIndex Index, int Id);
    private sealed class Cursor(string volume, SearchIndex index, IEnumerator<int> ids)
    {
        public SearchIndex Index { get; } = index;
        public IEnumerator<int> Ids { get; } = ids;
        public Priority Priority => new(volume, Index, Ids.Current);
    }
}
