using System.Numerics;

namespace NtfsSearch.Core;

internal sealed partial class SpaceUsageIndex
{
    internal SpaceUsageIndex Update(IReadOnlyList<SearchIndex> indexes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (layers.Length == 0 || indexes.Count == 0 || layers[0].Index.Records != indexes[0].Records)
            return Create(rootPath, indexes, token);
        if (layers.Length == indexes.Count && layers.Select(layer => layer.Index).SequenceEqual(indexes)) return this;

        var previousLayers = layers.ToDictionary(layer => layer.Index.Records);
        var nextLayers = new Layer[indexes.Count];
        var changedFolders = new HashSet<Folder>();
        var fileChanges = new Dictionary<Folder, FileChange>();
        for (int i = 0; i < indexes.Count; i++)
        {
            var index = indexes[i];
            if (previousLayers.Remove(index.Records, out var previous))
            {
                nextLayers[i] = previous.WithView(index);
                CompareVisibility(previous.Index, index, Collect, token);
            }
            else
            {
                nextLayers[i] = new Layer(index, token);
                CollectLayer(index, add: true);
            }
        }
        foreach (var previous in previousLayers.Values) CollectLayer(previous.Index, add: false);

        var relations = new List<(Folder Folder, Folder? Parent, ReadOnlyMemory<char> Name)>(changedFolders.Count);
        var touched = new HashSet<Folder>(changedFolders);
        foreach (var folder in changedFolders)
        {
            token.ThrowIfCancellationRequested();
            var (parent, name) = LocateDirectory(folder.Id, indexes);
            relations.Add((folder, parent, name));
            if (parent is not null) touched.Add(parent);
        }

        if (relations.Any(relation => relation.Parent != relation.Folder.Parent
                || !relation.Name.Span.SequenceEqual(relation.Folder.Name.Span))
            || fileChanges.Any(change => change.Key != root && !change.Key.Indexed && change.Value.Count != 0
                && (change.Key.DirectFiles == 0 || change.Key.DirectFiles + change.Value.Count == 0)))
            foreach (var anchor in anchors)
            {
                token.ThrowIfCancellationRequested();
                Detach(anchor, anchor.Parent!);
                touched.Add(anchor);
            }

        // 先断开旧边再连新边。父子同时移动时，任意更新顺序都只会搬运一次各自的子树计数。
        foreach (var (folder, parent, _) in relations)
            if (folder.Parent is { } oldParent && parent != oldParent)
            {
                Detach(folder, oldParent);
                touched.Add(oldParent);
            }
        foreach (var (folder, parent, name) in relations)
        {
            folder.Indexed = parent is not null;
            if (parent is null) continue;
            anchors.Remove(folder);
            folder.Name = name;
            if (folder.Parent is null) Attach(folder, parent);
        }
        foreach (var (folder, change) in fileChanges)
        {
            folder.DirectFiles += change.Count;
            Adjust(folder, change.Usage);
            touched.Add(folder);
        }
        foreach (var folder in touched)
        {
            token.ThrowIfCancellationRequested();
            if (folder == root || folder.Indexed) continue;
            if (folder.DirectFiles != 0 || folder.FirstChild is not null) anchors.Add(folder);
            else PruneAnchor(folder);
        }
        AttachAnchors(token, updateUsage: true);
        layers = nextLayers;
        return this;

        void CollectLayer(SearchIndex index, bool add)
        {
            for (int id = 0; id < index.Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                if (index.IsVisible(id)) Collect(index, id, add);
            }
        }

        void Collect(SearchIndex index, int id, bool add)
        {
            ref readonly var row = ref index.Records[id];
            if (row.IsDirectory)
            {
                ulong reference = index.FileReferences![id];
                if (!DirectoryPathResolver.IsRoot(reference)) changedFolders.Add(GetFolder(reference));
                return;
            }
            var folder = GetParent(index, row.Directory);
            var size = ReadSize(index, id);
            var change = fileChanges.GetValueOrDefault(folder);
            fileChanges[folder] = add ? new(change.Count + 1, change.Usage + size) : new(change.Count - 1, change.Usage - size);
        }
    }

    private (Folder? Parent, ReadOnlyMemory<char> Name) LocateDirectory(ulong reference,
        IReadOnlyList<SearchIndex> indexes)
    {
        for (int layer = indexes.Count - 1; layer >= 0; layer--)
        {
            var index = indexes[layer];
            var (start, count) = index.FindFileRange(reference);
            for (int id = start + count - 1; id >= start; id--)
            {
                ref readonly var row = ref index.Records[id];
                if (row.IsDirectory && index.IsVisible(id))
                    return (GetParent(index, row.Directory), index.NameMemory(id));
            }
        }
        return default;
    }

    private static void CompareVisibility(SearchIndex previous, SearchIndex current,
        Action<SearchIndex, int, bool> change, CancellationToken token)
    {
        if (previous.HiddenRows == current.HiddenRows) return;
        int words = (int)(((long)current.Count + 63) / 64);
        for (int i = 0; i < words; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            ulong oldHidden = previous.HiddenRows?[i] ?? 0;
            ulong newHidden = current.HiddenRows?[i] ?? 0;
            ulong changed = oldHidden ^ newHidden;
            while (changed != 0)
            {
                int bit = BitOperations.TrailingZeroCount(changed);
                bool isVisible = (newHidden & (1UL << bit)) == 0;
                change(isVisible ? current : previous, i * 64 + bit, isVisible);
                changed &= changed - 1;
            }
        }
    }

    private static void Attach(Folder folder, Folder parent, bool updateUsage = true)
    {
        for (Folder? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
            if (ancestor == folder) throw new InvalidDataException("空间分析的目录层级包含循环。");
        folder.Parent = parent;
        parent.AddChild(folder);
        if (updateUsage) Adjust(parent, folder.Usage);
    }

    private void PruneAnchor(Folder folder)
    {
        while (folder != root && !folder.Indexed && folder.DirectFiles == 0 && folder.FirstChild is null)
        {
            anchors.Remove(folder);
            folders.Remove(folder.Id);
            if (folder.Parent is not { } parent) break;
            Detach(folder, parent);
            folder = parent;
        }
    }

    private static void Detach(Folder folder, Folder parent)
    {
        Adjust(parent, default(Usage) - folder.Usage);
        parent.RemoveChild(folder);
        folder.Parent = null;
    }

    private static void Adjust(Folder folder, Usage delta)
    {
        if (delta == default) return;
        for (Folder? current = folder; current is not null; current = current.Parent) current.Usage += delta;
    }

    private readonly record struct FileChange(long Count, Usage Usage);
}
