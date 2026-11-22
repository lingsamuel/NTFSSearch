using System.Diagnostics;

namespace NtfsSearch.Core;

public sealed class SearchCatalog
{
    private static readonly HashSet<object> NoSortOwners = [];
    private readonly object gate = new();
    private readonly Dictionary<string, PublishedSearchVolume> indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> generations = new(StringComparer.Ordinal);
    private PublishedSearchVolume[] published = [];
    private long version;
    private SearchSortSelection? desktopSort;
    private SearchSortSelection[]? precomputedSorts;
    private HashSet<object> sortOwners = NoSortOwners;
    public long Count => Volatile.Read(ref published).Sum(p => p.Layers.Sum(i => (long)i.VisibleCount));
    public int VolumeCount => Volatile.Read(ref published).Length;
    internal bool Contains(string key) => PublishedLayers(key) is not null;
    internal IReadOnlyList<SearchIndex>? PublishedLayers(string key) => FindVolume(key)?.Layers;
    internal IReadOnlyList<PublishedSearchVolume> PublishedVolumes => Volatile.Read(ref published);
    internal PublishedSearchVolume? FindVolume(string key) => Volatile.Read(ref published).FirstOrDefault(p => p.Id == key);
    public long Version => Interlocked.Read(ref version);
    internal bool PrecomputeSort => Volatile.Read(ref precomputedSorts) is not null;
    public event Action? Changed;

    /// <summary>即时切换当前目录的排序策略，沿用桌面当前列已经完成的排列。</summary>
    internal bool SetPrecomputeSort(bool enabled)
    {
        lock (gate)
        {
            if (enabled == (precomputedSorts is not null)) return false;
            if (enabled)
            {
                var next = Enum.GetValues<SortField>().Select(field =>
                    desktopSort is { } selected && selected.Field == field ? selected : new SearchSortSelection(field)).ToArray();
                foreach (var selection in next)
                {
                    selection.UpdateOwners(sortOwners);
                    selection.SetWarmSession(next);
                }
                Volatile.Write(ref precomputedSorts, next);
            }
            else
            {
                foreach (var selection in precomputedSorts!)
                {
                    selection.SetWarmSession(null);
                    if (selection != desktopSort) selection.UpdateOwners(NoSortOwners);
                }
                Volatile.Write(ref precomputedSorts, null);
            }
            return true;
        }
    }

    // HTTP 只使用普通行的排列。后台补建时，缺少任意卷的排列均按冷查询处理。
    internal bool SortIsPrecomputed(SortField sort)
    {
        lock (gate)
        {
            if (precomputedSorts is not { } selections) return false;
            return IsSortReady(published, selections[(int)sort]);
        }
    }

    private static bool IsSortReady(PublishedSearchVolume[] snapshot, SearchSortSelection selection)
        => snapshot.All(volume => volume.Layers.All(index => index.Count == 0 || index.PrecomputeSort
            || index.IsSortCached(selection)));

    /// <summary>由后台调用，优先补建桌面当前列。检查点必须允许取消，不能等待同列查询结束。</summary>
    internal void WarmSorts(CancellationToken token, Action<CancellationToken>? checkpoint = null)
    {
        SearchSortSelection[] selections;
        PublishedSearchVolume[] snapshot;
        SortField currentField;
        lock (gate)
        {
            if (precomputedSorts is not { } active) return;
            selections = active;
            snapshot = published;
            currentField = desktopSort?.Field ?? SortField.Name;
        }
        Action? check = checkpoint is null ? null : () => checkpoint(token);
        WarmColumn(selections[(int)currentField]);
        foreach (var selection in selections)
            if (selection.Field != currentField) WarmColumn(selection);

        void WarmColumn(SearchSortSelection selection)
        {
            foreach (var volume in snapshot)
            {
                var sizes = selection.Field == SortField.Size ? volume.DirectorySizes : null;
                for (int layer = 0; layer < volume.Layers.Count; layer++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(selections, Volatile.Read(ref precomputedSorts))) return;
                    var index = volume.Layers[layer];
                    if (index.Count == 0 || index.PrecomputeSort || !index.IsSortAvailable(selection.Field)) continue;
                    index.WarmSort(selection, selections, token, check);
                    if (sizes is { Length: > 0 } && sizes[layer].HasKnownDirectories)
                        sizes[layer].WarmSort(selection, selections, token, check);
                }
            }
        }
    }

    public void Publish(string volumeId, SearchIndex index)
        => PublishLayers(volumeId, [index]);

    public void PublishLayers(string volumeId, IReadOnlyList<SearchIndex> layers, string? generation = null)
    {
        ArgumentNullException.ThrowIfNull(layers);
        lock (gate)
        {
            indexes[volumeId] = CreateVolumeView(volumeId, layers);
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
            indexes[volumeId] = CreateVolumeView(volumeId, layers);
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
        var snapshot = indexes.Values.OrderBy(volume => volume.Id, StringComparer.Ordinal).ToArray();
        UpdateSortOwners(snapshot);
        Volatile.Write(ref published, snapshot);
        Interlocked.Increment(ref version);
    }

    private PublishedSearchVolume CreateVolumeView(string volumeId, IReadOnlyList<SearchIndex> layers)
    {
        var view = new PublishedSearchVolume(volumeId, layers.ToArray());
        if (layers.Count != 0 && indexes.TryGetValue(volumeId, out var previous) && previous.Layers.Count != 0
            && previous.Layers[0].Records == layers[0].Records)
            view.DirectorySortOwners = previous.DirectorySortOwners;
        return view;
    }

    internal bool SetDirectorySizes(PublishedSearchVolume volume, DirectorySizeOrder[]? sizes)
    {
        lock (gate)
        {
            if (indexes.GetValueOrDefault(volume.Id) != volume) return false;
            volume.DirectorySortOwners = sizes?.Where(order => !order.Index.PrecomputeSort && order.HasKnownDirectories)
                .SelectMany(order => order.SortOwners).ToArray() ?? [];
            UpdateSortOwners(published);
            volume.DirectorySizes = sizes;
            return true;
        }
    }

    private void UpdateSortOwners(PublishedSearchVolume[] snapshot)
    {
        sortOwners = [];
        foreach (var volume in snapshot)
        {
            foreach (var index in volume.Layers)
            {
                if (index.PrecomputeSort) continue;
                sortOwners.Add(index.BaselineSortSource);
                if (index.HasOrderCorrections) sortOwners.Add(index.SortSource);
            }
            sortOwners.UnionWith(volume.DirectorySortOwners);
        }
        if (precomputedSorts is { } selections)
            foreach (var selection in selections) selection.UpdateOwners(sortOwners);
        else desktopSort?.UpdateOwners(sortOwners);
    }

    public SearchResult Search(string text, SortField sort = SortField.Name, bool descending = false,
        bool matchPath = true, int offset = 0, int limit = 100, CancellationToken cancellationToken = default,
        SearchFilter? filter = null)
        => Search(text, sort, descending, matchPath, offset, limit, cancellationToken, filter, includeDirectorySizes: false, desktop: false);

    internal SearchResult SearchDesktop(string text, SortField sort, bool descending,
        bool matchPath, int offset, int limit, CancellationToken cancellationToken, bool includeDirectorySizes = true)
        => Search(text, sort, descending, matchPath, offset, limit, cancellationToken, null, includeDirectorySizes, desktop: true);

    internal SearchResult SearchHttp(string text, SortField sort, bool descending, bool matchPath, int offset, int limit,
        CancellationToken cancellationToken, SearchFilter filter, Action beforeColdSort)
        => Search(text, sort, descending, matchPath, offset, limit, cancellationToken, filter,
            includeDirectorySizes: false, desktop: false, beforeColdSort);

    private SearchResult Search(string text, SortField sort, bool descending, bool matchPath, int offset, int limit,
        CancellationToken cancellationToken, SearchFilter? filter, bool includeDirectorySizes, bool desktop,
        Action? beforeColdSort = null)
    {
        SearchIndex.ValidateRequest(text, sort, offset, limit, cancellationToken);
        filter?.Validate();
        var timer = Stopwatch.StartNew();
        var (snapshot, selection, sortReady) = CaptureSearch(sort, desktop, checkSortReadiness: beforeColdSort is not null && limit > 0);
        // 先给已知冷查询完整预算；实际读取排列时仍检查缓存是否已被逐出。
        if (!sortReady) beforeColdSort?.Invoke();
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
                    bool cold = selection is not null && !index.PrecomputeSort && query.RequiresFullOrder
                        && (!index.IsSortCached(selection) || order is not null && !order.IsPrecomputed(selection));
                    var cursor = new Cursor(volume, layer, query.Ids(sort, descending, cancellationToken, order, selection, beforeColdSort).GetEnumerator(),
                        sizes, order, cold);
                    cursors.Add(cursor);
                }
            }
            if (offset < total) InitializeCursors(cursors, queue, cancellationToken);
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

    private static void InitializeCursors(List<Cursor> cursors, PriorityQueue<Cursor, Priority> queue, CancellationToken token)
    {
        List<Cursor>? cold = null;
        foreach (var cursor in cursors)
        {
            if (cursor.Cold) (cold ??= []).Add(cursor);
            else if (cursor.Ids.MoveNext()) queue.Enqueue(cursor, cursor.Priority);
        }
        if (cold is null) return;
        if (cold.Count == 1) cold[0].Ready = cold[0].Ids.MoveNext();
        else
        {
            // 卷独立排序，可以并行得到首个候选。查询量小或已缓存时沿用同步归并路径。
            Parallel.ForEach(cold, new ParallelOptions { CancellationToken = token,
                MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 8) }, cursor => cursor.Ready = cursor.Ids.MoveNext());
        }
        foreach (var cursor in cold)
            if (cursor.Ready) queue.Enqueue(cursor, cursor.Priority);
    }

    private (PublishedSearchVolume[] Snapshot, SearchSortSelection? Selection, bool SortReady) CaptureSearch(
        SortField sort, bool desktop, bool checkSortReadiness)
    {
        lock (gate)
        {
            var snapshot = published;
            if (precomputedSorts is { } selections)
            {
                var warm = selections[(int)sort];
                if (desktop) desktopSort = warm;
                return (snapshot, warm, !checkSortReadiness || IsSortReady(snapshot, warm));
            }
            if (desktopSort is { } current && current.Field == sort) return (snapshot, current, false);
            if (!desktop && sortOwners.Count == 0) return (snapshot, null, false);
            var selection = new SearchSortSelection(sort);
            if (desktop)
            {
                desktopSort?.UpdateOwners(NoSortOwners);
                selection.UpdateOwners(sortOwners);
                desktopSort = selection;
            }
            return (snapshot, selection, false);
        }
    }

    private readonly record struct Priority(string VolumeId, SearchIndex Index, int Id, DirectorySizeOrder? DirectorySizes)
    {
        internal Int128? Size => Index.EffectiveSize(Id, DirectorySizes);
    }

    private sealed class Cursor(PublishedSearchVolume volume, int layer, IEnumerator<int> ids,
        DirectorySizeOrder[]? sortSizes, DirectorySizeOrder? directorySizes, bool cold)
    {
        public PublishedSearchVolume Volume { get; } = volume;
        public int Layer { get; } = layer;
        public SearchIndex Index { get; } = volume.Layers[layer];
        public IEnumerator<int> Ids { get; } = ids;
        public DirectorySizeOrder[]? SortSizes { get; } = sortSizes;
        public bool Cold { get; } = cold;
        public bool Ready { get; set; }
        public Priority Priority => new(Volume.Id, Index, Ids.Current, directorySizes);
    }
}
