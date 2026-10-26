using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 单卷单写者；发布出来的记录、名称、位图与目录表均不再修改。
public sealed class LiveVolumeIndex
{
    internal sealed record DirectoryNode(ulong Parent, string Name, string? RootPath = null, bool Deleted = false);
    internal ImmutableDictionary<ulong, DirectoryNode> DirectoryGraph { get; private set; }
    private SearchIndex[] layers;
    public IReadOnlyList<SearchIndex> Layers => Array.AsReadOnly(Volatile.Read(ref layers));
    public JournalPosition? Position { get; private set; }
    public long AppliedSequence { get; private set; }

    public LiveVolumeIndex(SearchIndex baseIndex, JournalPosition? position = null, CancellationToken cancellationToken = default)
    {
        if (!baseIndex.HasIdentity) throw new ArgumentException("实时索引要求包含完整文件身份的基线。", nameof(baseIndex));
        layers = [baseIndex]; Position = position;
        var graph = ImmutableDictionary.CreateBuilder<ulong, DirectoryNode>();
        for (int id = 0; id < baseIndex.Count; id++)
        {
            if ((id & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            ref readonly var record = ref baseIndex.Records[id];
            if (record.IsDirectory && baseIndex.IsVisible(id))
                graph[baseIndex.FileReferences![id]] = new(baseIndex.DirectoryReferences![record.Directory], baseIndex.Name(id).ToString());
        }
        for (int d = 0; d < baseIndex.Directories.Length; d++)
        {
            if ((d & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!graph.ContainsKey(baseIndex.DirectoryReferences![d]))
                graph[baseIndex.DirectoryReferences[d]] = new(0, "", baseIndex.Directories[d]);
        }
        DirectoryGraph = graph.ToImmutable();
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
        bool directoryChanged = false;
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
                graph = graph.SetItem(reference, new(0, "", Deleted: true)); directoryChanged = true;
            }
        foreach (var file in changedFiles)
        {
            token.ThrowIfCancellationRequested();
            // 对象删除后按ID打开已无法取得属性，沿用目录图中的已知类型。
            bool deletedDirectory = file.Links.Count == 0 && graph.TryGetValue(file.FileReference, out var existing)
                && existing.RootPath is null;
            if (!file.IsDirectory && !deletedDirectory) continue;
            var node = file.Links.Count == 0
                ? new DirectoryNode(0, "", Deleted: true)
                : new DirectoryNode(file.Links[0].ParentReference, file.Links[0].Name);
            if (!graph.TryGetValue(file.FileReference, out var previous) || previous != node)
            {
                graph = graph.SetItem(file.FileReference, node); directoryChanged = true;
            }
        }
        foreach (var file in changedFiles)
            foreach (var link in file.Links)
                if (!graph.ContainsKey(link.ParentReference))
                    graph = graph.SetItem(link.ParentReference, new(0, "", Normalize(link.ParentPath)));
        var resolved = new Dictionary<ulong, string?>();
        string? Resolve(ulong reference)
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
                path = path is null ? null : path + graph[chain[i]].Name + "\\";
                resolved[chain[i]] = path;
            }
            resolved[reference] = path; return path;
        }
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
            if (directoryChanged)
            {
                bool[]? deleted = null;
                for (int d = 0; d < directories.Length; d++)
                {
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
                builder.Add(parent, link.Name, file.Size, file.ModifiedUtcTicks, file.IsDirectory, file.FileReference, link.ParentReference);
                added++;
            }
        if (added > 0)
        {
            var segment = builder.Build(token);
            // 按规模级联合并只读增量段，基线从不参与复制或重建。
            while (next.Count > 1 && next[^1].Count <= (long)segment.Count * 2)
            {
                segment = Merge(next[^1], segment, token); next.RemoveAt(next.Count - 1);
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
        foreach (var (index, id) in links)
        {
            var r = index.Records[id];
            if (r.IsDirectory != file.IsDirectory || (r.HasSize ? r.Size : (long?)null) != (file.IsDirectory ? null : file.Size)
                || (r.HasModified ? r.Modified : (long?)null) != file.ModifiedUtcTicks) return false;
            int found = remaining.FindIndex(l => l.ParentReference == index.DirectoryReferences![r.Directory]
                && index.Name(id).SequenceEqual(l.Name.AsSpan()));
            if (found < 0) return false;
            remaining.RemoveAt(found);
        }
        return true;
    }

    private static SearchIndex Merge(SearchIndex left, SearchIndex right, CancellationToken token)
    {
        var builder = new IndexBuilder(checked(left.VisibleCount + right.VisibleCount));
        foreach (var index in new[] { left, right })
            for (int id = 0; id < index.Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                if (!index.IsVisible(id)) continue;
                ref readonly var r = ref index.Records[id];
                builder.Add(index.Directories[r.Directory], index.Name(id), r.HasSize ? r.Size : null,
                    r.HasModified ? r.Modified : null, r.IsDirectory, index.FileReferences![id], index.DirectoryReferences![r.Directory]);
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
    internal LiveVolumeIndex(SearchIndex[] restored, ImmutableDictionary<ulong, DirectoryNode> graph, long sequence, JournalPosition? position)
    {
        layers = restored; DirectoryGraph = graph; AppliedSequence = sequence; Position = position;
    }
}
