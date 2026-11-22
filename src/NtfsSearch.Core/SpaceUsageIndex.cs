namespace NtfsSearch.Core;

internal sealed record SpaceFolder(string Path, SpaceBreadcrumb[] Breadcrumbs, Int128 Bytes, long UnknownFiles,
    bool IsEmpty, SpaceItem[] Children);
internal sealed record SpaceBreadcrumb(ulong Id, string Name);
internal sealed record SpaceItem(ulong? DirectoryId, string Name, string? Path, Int128 Bytes, long Count = 1, ulong? FileReference = null);

// 一个后台工作任务独占当前卷。索引层只读，统计只为目录保存对象。
internal sealed partial class SpaceUsageIndex(string rootPath)
{
    private readonly Folder root = new(0) { Name = rootPath.AsMemory() };
    private readonly Dictionary<ulong, Folder> folders = [];
    private readonly HashSet<Folder> anchors = [];
    private Layer[] layers = [];

    internal static SpaceUsageIndex Create(string rootPath, IReadOnlyList<SearchIndex> indexes, CancellationToken token = default)
    {
        var result = new SpaceUsageIndex(rootPath);
        result.layers = indexes.Select(index => new Layer(index, token)).ToArray();
        foreach (var layer in result.layers) result.ReadInitialLayer(layer.Index, token);
        result.SumInitialTree(token);
        return result;
    }

    private void ReadInitialLayer(SearchIndex index, CancellationToken token)
    {
        var parents = new Folder?[index.Directories.Length];
        for (int id = 0; id < index.Count; id++)
        {
            if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
            if (!index.IsVisible(id)) continue;
            ref readonly var row = ref index.Records[id];
            var parent = parents[row.Directory] ??= GetParent(index, row.Directory);
            if (row.IsDirectory)
            {
                ulong reference = index.FileReferences![id];
                if (DirectoryPathResolver.IsRoot(reference)) continue;
                var folder = GetFolder(reference);
                folder.Indexed = true;
                folder.Name = index.NameMemory(id);
                folder.Parent = parent;
            }
            else
            {
                parent.DirectFiles++;
                parent.Usage += ReadSize(index, id);
            }
        }
    }

    private void SumInitialTree(CancellationToken token)
    {
        foreach (var folder in folders.Values)
        {
            token.ThrowIfCancellationRequested();
            if (folder.Parent is { } parent) parent.AddChild(folder);
            else anchors.Add(folder);
        }
        AttachAnchors(token, updateUsage: false);

        // 按父到子的顺序收集节点后反向累加，目录深度不会占用调用栈。
        var order = new List<Folder>(folders.Count + 1) { root };
        for (int i = 0; i < order.Count; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            for (var child = order[i].FirstChild; child is not null; child = child.NextSibling) order.Add(child);
        }
        if (order.Count != folders.Count + 1) throw new InvalidDataException("空间分析的目录层级不完整。");
        for (int i = order.Count - 1; i > 0; i--)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            var folder = order[i];
            folder.Parent!.Usage += folder.Usage;
        }
    }

    internal bool Contains(ulong reference) => reference == 0 || folders.ContainsKey(reference);

    /// <summary>按当前索引校验临时展开的父子关系，返回第一个已删除或移动的目录。</summary>
    internal ulong? FindBrokenBranch(ulong parent, IEnumerable<ulong> branch)
    {
        foreach (ulong id in branch)
        {
            if (!folders.TryGetValue(id, out var folder) || folder.Parent?.Id != parent) return id;
            parent = id;
        }
        return null;
    }

    /// <summary>将发生文件变更的父目录归到指定视图根的直接子目录。</summary>
    internal HashSet<ulong> ActivityFolders(ulong viewRoot, IEnumerable<ulong> parents)
    {
        var affected = new HashSet<ulong>();
        foreach (ulong parent in parents)
            for (var folder = folders.GetValueOrDefault(parent); folder is not null && folder.Id != viewRoot; folder = folder.Parent)
                if (folder.Parent?.Id == viewRoot)
                {
                    affected.Add(folder.Id);
                    break;
                }
        return affected;
    }

    /// <summary>接受盘符、资源管理器复制的绝对路径和扩展路径，保留实际索引名称。</summary>
    internal static string? NormalizeLocalPath(string? input)
    {
        string path = input?.TrimStart() ?? "";
        if (path.StartsWith('"'))
        {
            var quoted = path.AsSpan().TrimEnd();
            if (quoted.Length < 2 || quoted[^1] != '"') return null;
            path = quoted[1..^1].ToString();
        }
        path = path.Replace('/', '\\');
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        if (path.Length < 2 || !char.IsAsciiLetter(path[0]) || path[1] != ':'
            || path.Length > 2 && path[2] != '\\') return null;
        // Win32 的路径规范化可能去掉名称尾部的点和空格，导致命中另一个已索引目录。
        return path.Length == 2 ? path + '\\' : path;
    }

    /// <summary>在当前卷的目录树上匹配已规范化的绝对路径，包括自身没有记录的目录锚。</summary>
    internal (ulong? Id, bool Ambiguous) FindDirectory(string fullPath, CancellationToken token = default,
        bool exactOnly = false)
    {
        if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase)) return default;
        var relative = fullPath.AsSpan(rootPath.Length).TrimEnd('\\');
        var exact = FindDirectory(relative, StringComparison.Ordinal, token);
        return exactOnly || exact.Id.HasValue || exact.Ambiguous ? exact : FindDirectory(relative, StringComparison.OrdinalIgnoreCase, token);
    }

    private (ulong? Id, bool Ambiguous) FindDirectory(ReadOnlySpan<char> path, StringComparison comparison, CancellationToken token)
    {
        var pending = new Stack<(Folder Folder, int Offset)>();
        pending.Push((root, 0));
        ulong? match = null;
        while (pending.TryPop(out var next))
        {
            token.ThrowIfCancellationRequested();
            if (next.Offset == path.Length)
            {
                if (match.HasValue) return (null, true);
                match = next.Folder.Id;
                continue;
            }
            var remaining = path[next.Offset..];
            for (var child = next.Folder.FirstChild; child is not null; child = child.NextSibling)
            {
                token.ThrowIfCancellationRequested();
                if (!MatchesPathPrefix(remaining, child.Name.Span, comparison)) continue;
                int end = next.Offset + child.Name.Length;
                pending.Push((child, end == path.Length ? end : end + 1));
            }
        }
        return (match, false);
    }

    internal SpaceFolder Read(ulong reference, int limit, CancellationToken token = default, Filter? filter = null)
    {
        var folder = reference == 0 ? root : folders[reference];
        var breadcrumbs = Breadcrumbs(folder);
        string path = string.Join('\\', breadcrumbs.Select(crumb => crumb.Name.TrimEnd('\\'))) + "\\";
        var biggest = new PriorityQueue<Candidate, Int128>();
        Usage total = folder.Usage - (filter?.Totals.GetValueOrDefault(folder.Id) ?? default);
        long count = 0;
        bool hasDirectory = false;
        void Offer(Candidate candidate, Int128 size)
        {
            if (size <= 0 || candidate.Index is { } index && filter?.Files.Contains((index, candidate.Id)) == true) return;
            count++;
            if (biggest.Count < limit) biggest.Enqueue(candidate, size);
            else biggest.EnqueueDequeue(candidate, size);
        }

        for (var child = folder.FirstChild; child is not null; child = child.NextSibling)
        {
            token.ThrowIfCancellationRequested();
            if (filter?.Directories.Contains(child.Id) == true) continue;
            hasDirectory = true;
            Offer(new(child, null, 0), child.Usage.Bytes - (filter?.Totals.GetValueOrDefault(child.Id).Bytes ?? 0));
        }
        foreach (var layer in layers)
            layer.OfferFiles(folder.Id, Offer, token);

        Int128 remainder = total.Bytes;
        long otherCount = count - biggest.Count;
        var entries = new SpaceItem[biggest.Count + (otherCount > 0 ? 1 : 0)];
        Span<char> nameScratch = stackalloc char[255];
        for (int i = biggest.Count - 1; biggest.TryDequeue(out var item, out var size); i--)
        {
            string name = item.Folder is { } child ? child.Name.ToString() : item.Index!.Name(item.Id, nameScratch).ToString();
            entries[i] = new(item.Folder?.Id, name, path + name, size, FileReference: item.Index?.FileReferences![item.Id]);
            remainder -= size;
        }
        if (otherCount > 0) entries[^1] = new(null, "其他", null, remainder, otherCount);
        return new(folder == root ? path : path[..^1], breadcrumbs, total.Bytes, total.Unknown,
            !hasDirectory && folder.DirectFiles == (filter?.DirectFiles.GetValueOrDefault(folder.Id) ?? 0), entries);
    }

    private static SpaceBreadcrumb[] Breadcrumbs(Folder folder)
    {
        var trail = new List<SpaceBreadcrumb>();
        for (Folder? current = folder; current is not null; current = current.Parent)
            trail.Add(new(current.Id, current.Name.ToString()));
        trail.Reverse();
        return trail.ToArray();
    }

    private Folder GetFolder(ulong reference)
    {
        if (DirectoryPathResolver.IsRoot(reference)) return root;
        if (!folders.TryGetValue(reference, out var folder)) folders.Add(reference, folder = new(reference));
        return folder;
    }

    private Folder GetParent(SearchIndex index, int directory)
    {
        var folder = GetFolder(index.DirectoryReferences![directory]);
        if (folder != root) folder.AnchorPath ??= index.Directories[directory];
        return folder;
    }

    private Folder AnchorParent(Folder folder)
    {
        // 目录自身未入索引时，用现有子项的父路径找到最深的已知祖先。
        string path = folder.AnchorPath ?? throw new InvalidDataException("空间分析缺少父目录路径。");
        var remaining = path.AsMemory(rootPath.Length, path.Length - rootPath.Length - 1);
        var parent = root;
        while (parent.FirstChild is not null)
        {
            Folder? nearest = null;
            for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
            {
                int length = child.Name.Length;
                if (length < remaining.Length
                    && (nearest is null || length > nearest.Name.Length)
                    && MatchesPathPrefix(remaining.Span, child.Name.Span, StringComparison.Ordinal))
                    nearest = child;
            }
            if (nearest is null) break;
            parent = nearest;
            remaining = remaining[(nearest.Name.Length + 1)..];
        }
        folder.Name = remaining;
        return parent;
    }

    private static bool MatchesPathPrefix(ReadOnlySpan<char> path, ReadOnlySpan<char> name, StringComparison comparison)
        => name.Length <= path.Length && (name.Length == path.Length || path[name.Length] == '\\')
            && path.StartsWith(name, comparison);

    private void AttachAnchors(CancellationToken token, bool updateUsage)
    {
        foreach (var anchor in anchors.Where(folder => folder.Parent is null).OrderBy(folder => folder.AnchorPath?.Length))
        {
            token.ThrowIfCancellationRequested();
            Attach(anchor, AnchorParent(anchor), updateUsage);
        }
    }

    private static Usage ReadSize(SearchIndex index, int id)
        => index.Records[id].HasSize ? new(index.Size(id), 0) : new(0, 1);

    internal readonly record struct Usage(Int128 Bytes, long Unknown)
    {
        public static Usage operator +(Usage left, Usage right) => new(left.Bytes + right.Bytes, left.Unknown + right.Unknown);
        public static Usage operator -(Usage left, Usage right) => new(left.Bytes - right.Bytes, left.Unknown - right.Unknown);
    }

    private sealed class Folder(ulong id)
    {
        internal readonly ulong Id = id;
        internal Folder? Parent;
        internal Folder? FirstChild, NextSibling, PreviousSibling;
        internal ReadOnlyMemory<char> Name;
        internal string? AnchorPath;
        internal bool Indexed;
        internal Usage Usage;
        internal long DirectFiles;

        // 兄弟链接复用目录对象，常见的单子目录不再额外分配一个集合。
        internal void AddChild(Folder child)
        {
            child.NextSibling = FirstChild;
            if (FirstChild is not null) FirstChild.PreviousSibling = child;
            FirstChild = child;
        }

        internal void RemoveChild(Folder child)
        {
            if (child.PreviousSibling is { } previous) previous.NextSibling = child.NextSibling;
            else FirstChild = child.NextSibling;
            if (child.NextSibling is { } next) next.PreviousSibling = child.PreviousSibling;
            child.PreviousSibling = child.NextSibling = null;
        }
    }

    private readonly record struct Candidate(Folder? Folder, SearchIndex? Index, int Id);

    private sealed class Layer
    {
        private readonly int[] offsets;
        private readonly int[] rows;
        private readonly Dictionary<ulong, int> firstSlot = [];
        private readonly Dictionary<ulong, List<int>> otherSlots = [];
        internal SearchIndex Index { get; }

        internal Layer(SearchIndex index, CancellationToken token)
        {
            Index = index;
            offsets = new int[index.Directories.Length + 1];
            for (int id = 0; id < index.Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                ref readonly var row = ref index.Records[id];
                if (row.HasSize && row.SizeCode != 0) offsets[row.Directory + 1]++;
            }
            for (int slot = 0; slot < index.Directories.Length; slot++)
            {
                if ((slot & 65535) == 0) token.ThrowIfCancellationRequested();
                if (offsets[slot + 1] > 0)
                {
                    ulong reference = index.DirectoryReferences![slot];
                    if (DirectoryPathResolver.IsRoot(reference)) reference = 0;
                    if (!firstSlot.TryAdd(reference, slot))
                    {
                        if (!otherSlots.TryGetValue(reference, out var slots)) otherSlots.Add(reference, slots = []);
                        slots.Add(slot);
                    }
                }
                offsets[slot + 1] += offsets[slot];
            }
            // 行号按父目录分组。位图变化时复用排列，在显示时筛掉旧行。
            rows = new int[offsets[^1]];
            var position = (int[])offsets.Clone();
            for (int id = 0; id < index.Count; id++)
            {
                if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
                ref readonly var row = ref index.Records[id];
                if (row.HasSize && row.SizeCode != 0) rows[position[row.Directory]++] = id;
            }
        }

        private Layer(SearchIndex index, Layer source)
        {
            Index = index;
            offsets = source.offsets;
            rows = source.rows;
            firstSlot = source.firstSlot;
            otherSlots = source.otherSlots;
        }

        internal Layer WithView(SearchIndex index) => Index == index ? this : new(index, this);

        internal void OfferFiles(ulong directory, Action<Candidate, Int128> offer, CancellationToken token)
        {
            if (!firstSlot.TryGetValue(directory, out int first)) return;
            OfferSlot(first);
            if (otherSlots.TryGetValue(directory, out var slots))
                foreach (int slot in slots) OfferSlot(slot);

            void OfferSlot(int slot)
            {
                for (int i = offsets[slot]; i < offsets[slot + 1]; i++)
                {
                    if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
                    int id = rows[i];
                    if (Index.IsVisible(id)) offer(new(null, Index, id), Index.Size(id));
                }
            }
        }
    }
}
