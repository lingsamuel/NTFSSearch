namespace NtfsSearch.Core;

// 单卷单写者；发布出来的记录、名称、位图与目录表均不再修改。
public sealed class LiveVolumeIndex
{
    internal readonly record struct DirectoryNode(ulong Parent, ReadOnlyMemory<char> Name, string? RootPath = null, bool Deleted = false)
    {
        internal bool SameValue(DirectoryNode other) => Parent == other.Parent && Deleted == other.Deleted
            && RootPath == other.RootPath && Name.Span.SequenceEqual(other.Name.Span);
    }
    internal DirectoryGraph DirectoryGraph { get; private set; }
    private DirectoryAncestryFilter? ancestryFilter;
    private SearchIndex[] layers;
    public IReadOnlyList<SearchIndex> Layers => Array.AsReadOnly(Volatile.Read(ref layers));
    public JournalPosition? Position { get; private set; }
    public long AppliedSequence { get; private set; }

    public LiveVolumeIndex(SearchIndex baseIndex, JournalPosition? position = null, CancellationToken cancellationToken = default)
    {
        if (!baseIndex.HasIdentity) throw new ArgumentException("实时索引要求包含完整文件身份的基线。", nameof(baseIndex));
        layers = [baseIndex]; Position = position;
        DirectoryGraph = DirectoryGraph.Create(baseIndex, cancellationToken);
    }

    public bool Apply(CommittedChange change, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (change.Sequence <= AppliedSequence) return false;
        bool changed = ApplyCore(change.Transaction, token, allowMultipart: true);
        AppliedSequence = change.Sequence;
        return changed;
    }

    public bool Apply(LiveTransaction transaction, CancellationToken token = default)
        => ApplyCore(transaction, token, allowMultipart: false);

    // 先于应用事务取旧链接，保留文件移动的两端以及索引内容相同的写入通知。
    internal void CollectFileParents(LiveTransaction transaction, HashSet<ulong> parents)
    {
        foreach (var file in transaction.Files)
        {
            if (file.IsDirectory) continue;
            foreach (var link in file.Links) parents.Add(link.ParentReference);
            foreach (var layer in layers)
                foreach (int row in layer.FindFileRows(file.FileReference))
                    if (layer.IsVisible(row) && !layer.Records[row].IsDirectory)
                        parents.Add(layer.DirectoryReferences![layer.Records[row].Directory]);
        }
    }

    private bool ApplyCore(LiveTransaction transaction, CancellationToken token, bool allowMultipart)
    {
        token.ThrowIfCancellationRequested();
        if (Position is { } position)
        {
            if (transaction.To.JournalId == position.JournalId && transaction.To.NextUsn <= position.NextUsn)
            {
                // 子树补采可拆成多个持久提交；中间段由提交序号去重，最后一段才推进USN。
                // 直接事务调用仍按日志水位去重，不能重复应用相同范围。
                bool intermediatePart = allowMultipart && transaction.From == position && transaction.To == position
                    && transaction.Files.Count > 0;
                if (!intermediatePart) return false;
            }
            if (transaction.From != position) throw new InvalidDataException("增量事务的日志位置不连续。");
        }
        if (transaction.From.JournalId != transaction.To.JournalId || transaction.To.NextUsn < transaction.From.NextUsn)
            throw new InvalidDataException("增量事务的日志范围无效。");
        // 同批同身份以最后的完整状态为准，字典规模只与本次变化有关。
        var files = transaction.Files.GroupBy(f => f.FileReference).Select(g => g.Last()).ToArray();
        var changedFiles = files.Where(f => !MatchesCurrent(f)).ToArray();
        if (changedFiles.Length == 0) { Position = transaction.To; return false; }
        var graph = DirectoryGraph;
        var changedDirectories = new HashSet<ulong>();
        var replaced = new HashSet<ulong>();
        foreach (var file in changedFiles)
            if (file.Links.Count > 0)
                foreach (var layer in layers)
                    foreach (int id in layer.FindRecordRows(file.FileReference))
                        if (layer.IsVisible(id) && layer.FileReferences![id] != file.FileReference)
                            replaced.Add(layer.FileReferences[id]);
        // MFT记录槽复用意味着旧代际已经不存在；旧代际的迟到删除不能隐藏新代际。
        foreach (ulong reference in replaced)
            if (graph.ContainsKey(reference))
            {
                graph = graph.SetItem(reference, new(0, ReadOnlyMemory<char>.Empty, Deleted: true)); changedDirectories.Add(reference);
            }
        foreach (var file in changedFiles)
        {
            token.ThrowIfCancellationRequested();
            // 对象删除后按ID打开已无法取得属性，沿用目录图中的已知类型。
            bool deletedDirectory = file.Links.Count == 0 && graph.TryGetValue(file.FileReference, out var existing)
                && existing.RootPath is null;
            if (!file.IsDirectory && !deletedDirectory) continue;
            var node = file.Links.Count == 0
                ? new DirectoryNode(0, ReadOnlyMemory<char>.Empty, Deleted: true)
                : new DirectoryNode(file.Links[0].ParentReference, file.Links[0].Name.AsMemory());
            if (!graph.TryGetValue(file.FileReference, out var previous) || !previous.SameValue(node))
            {
                graph = graph.SetItem(file.FileReference, node); changedDirectories.Add(file.FileReference);
            }
        }
        foreach (var file in changedFiles)
            foreach (var link in file.Links)
                if (!graph.ContainsKey(link.ParentReference))
                    graph = graph.SetItem(link.ParentReference, new(0, ReadOnlyMemory<char>.Empty, Normalize(link.ParentPath)));
        changedDirectories.RemoveWhere(reference => !DirectoryGraph.ContainsKey(reference));
        if (changedDirectories.Count > 0)
        {
            ancestryFilter ??= new DirectoryAncestryFilter();
            ancestryFilter.Reset();
        }
        var resolved = new Dictionary<ulong, string?>();
        string? Resolve(ulong reference) => ResolveDirectory(reference, graph, resolved, token);
        var next = new List<SearchIndex>();
        foreach (var layer in layers)
        {
            token.ThrowIfCancellationRequested();
            ulong[]? hidden = layer.HiddenRows; bool ownsHidden = false;
            void Hide(int id)
            {
                if (hidden is not null && (hidden[id / 64] & (1UL << (id % 64))) != 0) return;
                if (!ownsHidden) { hidden = hidden is null ? new ulong[(int)(((long)layer.Count + 63) / 64)] : (ulong[])hidden.Clone(); ownsHidden = true; }
                hidden![id / 64] |= 1UL << (id % 64);
            }
            foreach (var file in changedFiles)
                foreach (int id in layer.FindFileRows(file.FileReference)) Hide(id);
            foreach (ulong reference in replaced)
                foreach (int id in layer.FindFileRows(reference)) Hide(id);
            string[] directories = layer.Directories;
            if (changedDirectories.Count > 0)
            {
                bool[]? deleted = null;
                for (int d = 0; d < directories.Length; d++)
                {
                    if ((d & 65535) == 0) token.ThrowIfCancellationRequested();
                    // 两遍扫描和硬链接补采可能保存混合历史路径，必须按旧图中的祖先身份判断，不能靠路径前缀。
                    if (!ancestryFilter!.Contains(layer.DirectoryReferences![d], DirectoryGraph, changedDirectories, token)) continue;
                    string? path = Resolve(layer.DirectoryReferences![d]);
                    if (path is null) { deleted ??= new bool[directories.Length]; deleted[d] = true; }
                    else if (path != directories[d])
                    {
                        if (ReferenceEquals(directories, layer.Directories)) directories = (string[])directories.Clone();
                        directories[d] = path;
                    }
                }
                if (deleted is not null)
                    for (int id = 0; id < layer.Count; id++)
                    {
                        if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                        if (deleted[layer.Records[id].Directory]) Hide(id);
                    }
            }
            next.Add(ownsHidden || !ReferenceEquals(directories, layer.Directories) ? layer.WithView(hidden, directories, token) : layer);
        }
        var builder = new IndexBuilder(changedFiles.Sum(f => f.Links.Count));
        int added = 0;
        foreach (var file in changedFiles)
            foreach (var link in file.Links)
            {
                token.ThrowIfCancellationRequested();
                string? parent = Resolve(link.ParentReference);
                if (parent is null) continue;
                builder.Add(parent, link.Name, file.Size, file.ModifiedUtcTicks, file.IsDirectory, file.FileReference, link.ParentReference, file.CreatedUtcTicks);
                added++;
            }
        if (added > 0)
        {
            var segment = builder.Build(token);
            // 按规模级联合并只读增量段，基线从不参与复制或重建。
            while (next.Count > 1 && next[^1].Count <= (long)segment.Count * 2)
            {
                segment = Merge([next[^1], segment], token); next.RemoveAt(next.Count - 1);
            }
            next.Add(segment);
        }
        // 没有可见行的增量段不再保留，旧查询仍持有原引用。
        for (int i = next.Count - 1; i > 0; i--) if (next[i].VisibleCount == 0) next.RemoveAt(i);
        token.ThrowIfCancellationRequested();
        DirectoryGraph = graph; Position = transaction.To;
        Volatile.Write(ref layers, next.ToArray());
        return true;
    }

    private bool MatchesCurrent(IndexedFileState file)
    {
        var links = new List<(SearchIndex Index, int Id)>();
        foreach (var layer in layers)
            foreach (int id in layer.FindFileRows(file.FileReference))
                if (layer.IsVisible(id)) links.Add((layer, id));
        if (links.Count != file.Links.Count) return false;
        var remaining = file.Links.ToList();
        Span<char> scratch = stackalloc char[255];
        foreach (var (index, id) in links)
        {
            var r = index.Records[id];
            if (r.IsDirectory != file.IsDirectory || (r.HasSize ? index.Size(id) : (long?)null) != (file.IsDirectory ? null : file.Size)
                || (r.HasModified ? r.Modified : (long?)null) != file.ModifiedUtcTicks
                || index.CreatedUtcTicks(id) != file.CreatedUtcTicks) return false;
            int found = -1;
            var name = index.Name(id, scratch);
            for (int link = 0; link < remaining.Count; link++)
                if (remaining[link].ParentReference == index.DirectoryReferences![r.Directory] && name.SequenceEqual(remaining[link].Name.AsSpan()))
                { found = link; break; }
            if (found < 0) return false;
            remaining.RemoveAt(found);
        }
        return true;
    }

    private static string? ResolveDirectory(ulong reference, DirectoryGraph graph,
        Dictionary<ulong, string?> resolved, CancellationToken token)
    {
        if (resolved.TryGetValue(reference, out var cached)) return cached;
        var chain = new List<ulong>(); var visiting = new HashSet<ulong>();
        ulong cursor = reference; string? path;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (resolved.TryGetValue(cursor, out path)) break;
            if (!graph.TryGetValue(cursor, out var node) || node.Deleted) { path = null; break; }
            if (node.RootPath is not null) { path = node.RootPath; break; }
            if (!visiting.Add(cursor)) throw new InvalidDataException("目录身份图包含循环。");
            chain.Add(cursor); cursor = node.Parent;
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            path = path is null ? null : string.Concat(path.AsSpan(), graph[chain[i]].Name.Span, "\\".AsSpan());
            resolved[chain[i]] = path;
        }
        resolved[reference] = path; return path;
    }

    /// <summary>单卷写者在提交边界调用。只有失效记录积累到一定比例才合并，已发布的查询视图仍然有效。</summary>
    public bool CompactIfNeeded(CancellationToken token = default)
    {
        var current = layers;
        long rows = current.Sum(index => (long)index.Count);
        long visible = current.Sum(index => (long)index.VisibleCount);
        if (rows == 0 || (rows - visible) * 4 < rows) return false;

        var compacted = Merge(current, token);
        // 目录图从新的有效基线重新取得父引用，释放旧基线和已经删除的目录覆盖。
        var graph = DirectoryGraph.Create(compacted, token);
        token.ThrowIfCancellationRequested();
        DirectoryGraph = graph;
        Volatile.Write(ref layers, [compacted]);
        return true;
    }

    private static SearchIndex Merge(IReadOnlyList<SearchIndex> indexes, CancellationToken token)
    {
        var builder = new IndexBuilder(indexes.Sum(index => index.VisibleCount), fixedSize: true);
        var directories = new Dictionary<(string Path, ulong Identity), int>();
        Span<char> scratch = stackalloc char[255];
        foreach (var index in indexes)
        {
            var mapped = new int[index.Directories.Length];
            Array.Fill(mapped, -1);
            for (int id = 0; id < index.Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                if (!index.IsVisible(id)) continue;
                ref readonly var row = ref index.Records[id];
                int directory = mapped[row.Directory];
                if (directory < 0)
                {
                    var key = (index.Directories[row.Directory], index.DirectoryReferences![row.Directory]);
                    if (!directories.TryGetValue(key, out directory))
                    {
                        directory = builder.AddDirectory(key.Item1, key.Item2);
                        directories.Add(key, directory);
                    }
                    mapped[row.Directory] = directory;
                }
                builder.AddRecord(directory, index.Name(id, scratch), row.HasSize ? index.Size(id) : null,
                    row.HasModified ? row.Modified : null, row.IsDirectory, index.FileReferences![id], index.CreatedUtcTicks(id));
            }
        }
        return builder.Build(token);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\') + "\\";
    public void SaveCheckpoint(string path, long sequence, CancellationToken token = default)
    {
        LiveViewStore.Save(this, path, sequence, token); AppliedSequence = sequence;
    }
    public static LiveVolumeIndex LoadCheckpoint(SearchIndex baseIndex, string path, CancellationToken token = default)
        => LiveViewStore.Load(baseIndex, path, token);
    internal LiveVolumeIndex(SearchIndex[] restored, DirectoryGraph graph, long sequence, JournalPosition? position)
    {
        layers = restored; DirectoryGraph = graph; AppliedSequence = sequence; Position = position;
    }
}
