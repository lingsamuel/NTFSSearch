using System.Diagnostics;

namespace NtfsSearch.Core;

public sealed class SearchCatalog
{
    private readonly object gate = new();
    private readonly Dictionary<string, PublishedSearchVolume> indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> generations = new(StringComparer.Ordinal);
    private PublishedSearchVolume[] published = [];
    private long version;
    public long Count => Volatile.Read(ref published).Sum(p => p.Layers.Sum(i => (long)i.VisibleCount));
    public int VolumeCount => Volatile.Read(ref published).Length;
    internal bool Contains(string key) => PublishedLayers(key) is not null;
    internal IReadOnlyList<SearchIndex>? PublishedLayers(string key) => FindVolume(key)?.Layers;
    internal IReadOnlyList<PublishedSearchVolume> PublishedVolumes => Volatile.Read(ref published);
    internal PublishedSearchVolume? FindVolume(string key) => Volatile.Read(ref published).FirstOrDefault(p => p.Id == key);
    public long Version => Interlocked.Read(ref version);
    public event Action? Changed;

    public void Publish(string volumeId, SearchIndex index)
        => PublishLayers(volumeId, [index]);

    public void PublishLayers(string volumeId, IReadOnlyList<SearchIndex> layers, string? generation = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        lock (gate)
        {
            indexes[volumeId] = new(volumeId, layers.ToArray());
            generations[volumeId] = generation;
            PublishSnapshot();
        }
        Changed?.Invoke();
    }

    public bool PublishLiveLayers(string volumeId, string generation, IReadOnlyList<SearchIndex> layers)
    {
        lock (gate)
        {
            if (!generations.TryGetValue(volumeId, out string? current) || current != generation) return false;
            indexes[volumeId] = new(volumeId, layers.ToArray());
            PublishSnapshot();
        }
        Changed?.Invoke(); return true;
    }

    public void RemoveExcept(IEnumerable<string> volumeIds)
    {
        var keep = volumeIds.ToHashSet(StringComparer.Ordinal);
        bool changed = false;
        lock (gate)
        {
            foreach (string key in indexes.Keys.Where(k => !keep.Contains(k)).ToArray()) { changed |= indexes.Remove(key); generations.Remove(key); }
            if (changed) PublishSnapshot();
        }
        if (changed) Changed?.Invoke();
    }

    private void PublishSnapshot()
    {
        Volatile.Write(ref published, indexes.Values.OrderBy(volume => volume.Id, StringComparer.Ordinal).ToArray());
        Interlocked.Increment(ref version);
    }

    public SearchResult Search(string text, SortField sort = SortField.Name, bool descending = false,
        bool matchPath = true, int offset = 0, int limit = 100, CancellationToken cancellationToken = default,
        SearchFilter? filter = null)
        => Search(text, sort, descending, matchPath, offset, limit, cancellationToken, filter, includeDirectorySizes: false);

    internal SearchResult SearchDesktop(string text, SortField sort, bool descending,
        bool matchPath, int offset, int limit, CancellationToken cancellationToken, bool includeDirectorySizes = true)
        => Search(text, sort, descending, matchPath, offset, limit, cancellationToken, null, includeDirectorySizes);

    private SearchResult Search(string text, SortField sort, bool descending, bool matchPath, int offset, int limit,
        CancellationToken cancellationToken, SearchFilter? filter, bool includeDirectorySizes)
    {
        SearchIndex.ValidateRequest(text, sort, offset, limit, cancellationToken);
        filter?.Validate();
        var timer = Stopwatch.StartNew();
        var snapshot = Volatile.Read(ref published);
        var cursors = new List<Cursor>();
        var queries = new List<PreparedSearch>();
        var queue = new PriorityQueue<Cursor, Priority>(Comparer<Priority>.Create((a, b) =>
        {
            int comparison = a.DirectorySizes is null && b.DirectorySizes is null
                ? SearchIndex.CompareAcross(a.Index, a.Id, b.Index, b.Id, sort, descending)
                : SearchIndex.CompareSizes(a.Index, a.Id, a.Size, b.Index, b.Id, b.Size, descending);
            return comparison != 0 ? comparison : StringComparer.Ordinal.Compare(a.VolumeId, b.VolumeId);
        }));
        try
        {
            long total = 0;
            bool pending = false, failed = false;
            bool sortDirectories = includeDirectorySizes && sort == SortField.Size;
            foreach (var volume in snapshot)
            {
                var sizes = sortDirectories ? volume.DirectorySizes : null;
                for (int layer = 0; layer < volume.Layers.Count; layer++)
                {
                    // 匹配与计数绑定本次发布视图。每层只生成一份匹配位图。
                    var index = volume.Layers[layer];
                    var query = index.Prepare(text, matchPath, cancellationToken, filter);
                    queries.Add(query); total += query.TotalCount;
                    if (query.TotalCount == 0 || limit == 0) continue;
                    if (sortDirectories && volume.SupportsDirectorySizes)
                    {
                        pending |= sizes is null;
                        failed |= sizes is { Length: 0 };
                    }
                    var order = sizes is { Length: > 0 } && sizes[layer].HasKnownDirectories ? sizes[layer] : null;
                    var cursor = new Cursor(volume, layer, query.Ids(sort, descending, cancellationToken, order).GetEnumerator(), sizes, order);
                    cursors.Add(cursor);
                    if (cursor.Ids.MoveNext()) queue.Enqueue(cursor, cursor.Priority);
                }
            }
            var rows = new List<SearchRow>(Math.Min(limit, 1024));
            List<DirectorySearchRow>? directoryRows = null;
            long skipped = 0;
            while (rows.Count < limit && queue.TryDequeue(out var cursor, out _))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (skipped++ >= offset)
                {
                    int id = cursor.Ids.Current;
                    var row = cursor.Index.GetRow(id);
                    if (includeDirectorySizes && row.IsDirectory)
                        (directoryRows ??= []).Add(new(rows.Count, cursor.Volume, cursor.Layer, id, cursor.SortSizes));
                    rows.Add(row);
                }
                if (rows.Count == limit) break;
                if (cursor.Ids.MoveNext()) queue.Enqueue(cursor, cursor.Priority);
            }
            return new(total, rows, timer.Elapsed)
            {
                VolumesReady = snapshot.Length, DirectoryRows = directoryRows,
                DirectorySortPending = pending, DirectorySortFailed = failed
            };
        }
        finally
        {
            foreach (var cursor in cursors) cursor.Ids.Dispose();
            foreach (var query in queries) query.Dispose();
        }
    }

    private readonly record struct Priority(string VolumeId, SearchIndex Index, int Id, DirectorySizeOrder? DirectorySizes)
    {
        internal Int128? Size => Index.EffectiveSize(Id, DirectorySizes);
    }

    private sealed class Cursor(PublishedSearchVolume volume, int layer, IEnumerator<int> ids,
        DirectorySizeOrder[]? sortSizes, DirectorySizeOrder? directorySizes)
    {
        public PublishedSearchVolume Volume { get; } = volume;
        public int Layer { get; } = layer;
        public SearchIndex Index { get; } = volume.Layers[layer];
        public IEnumerator<int> Ids { get; } = ids;
        public DirectorySizeOrder[]? SortSizes { get; } = sortSizes;
        public Priority Priority => new(Volume.Id, Index, Ids.Current, directorySizes);
    }
}
