namespace NtfsSearch.Core;

internal readonly record struct SpaceExclusion(string Path, ulong? FileReference);

internal sealed partial class SpaceUsageIndex
{
    internal sealed class Filter
    {
        internal readonly HashSet<ulong> Directories = [];
        internal readonly HashSet<(SearchIndex Index, int Row)> Files = [];
        internal readonly Dictionary<ulong, Usage> Totals = [];
        internal readonly Dictionary<ulong, long> DirectFiles = [];
    }

    // 每份筛选只对应当前已发布的索引。重读变更后的大小，按祖先链扣减，无需遍历排除的子树。
    internal Filter? CreateFilter(SpaceExclusion[] exclusions, CancellationToken token = default)
    {
        if (exclusions.Length == 0) return null;
        var filter = new Filter();
        foreach (var entry in exclusions)
        {
            if (entry.FileReference is null && FindDirectory(entry.Path, token, exactOnly: true).Id is ulong id && id != 0)
                filter.Directories.Add(id);
        }
        foreach (ulong id in filter.Directories)
        {
            var folder = folders[id];
            if (!IsExcluded(folder.Parent, filter))
                Deduct(folder.Parent, folder.Usage);
        }
        foreach (var entry in exclusions)
        {
            token.ThrowIfCancellationRequested();
            if (entry.FileReference is not ulong fileReference) continue;
            int separator = entry.Path.LastIndexOf('\\');
            if (separator < 2) continue;
            string parentPath = entry.Path[..(separator + 1)];
            if (FindDirectory(parentPath, token, exactOnly: true).Id is not ulong parentId) continue;
            var parent = parentId == 0 ? root : folders[parentId];
            if (IsExcluded(parent, filter)) continue;

            var name = entry.Path.AsSpan(separator + 1);
            if (FindExcludedFile(fileReference, parentId, name, token) is not { } file || !filter.Files.Add(file)) continue;
            filter.DirectFiles[parentId] = filter.DirectFiles.GetValueOrDefault(parentId) + 1;
            Deduct(parent, ReadSize(file.Index, file.Row));
        }
        return filter;

        void Deduct(Folder? folder, Usage amount)
        {
            for (; folder is not null; folder = folder.Parent)
                filter.Totals[folder.Id] = filter.Totals.GetValueOrDefault(folder.Id) + amount;
        }
    }

    private (SearchIndex Index, int Row)? FindExcludedFile(ulong fileReference, ulong parentId, ReadOnlySpan<char> name,
        CancellationToken token)
    {
        Span<char> scratch = stackalloc char[255];
        foreach (var layer in layers)
        {
            var index = layer.Index;
            var (start, count) = index.FindFileRange(fileReference);
            for (int row = start; row < start + count; row++)
                if (MatchesFile(index, row, parentId) && name.SequenceEqual(index.Name(row, scratch))) return (index, row);
        }

        // 原路径的文件被替换时，复用索引的名称排序来定位新记录。父路径变更只影响同名条目的顺序。
        foreach (var layer in layers)
        {
            var index = layer.Index;
            var order = index.SortOrder(SortField.Name);
            int low = 0, high = order.Count;
            while (low < high)
            {
                int middle = low + (high - low) / 2;
                if (index.Name(order[middle], scratch).CompareTo(name, StringComparison.OrdinalIgnoreCase) < 0)
                    low = middle + 1;
                else
                    high = middle;
            }
            for (int position = low; position < order.Count; position++)
            {
                if ((position & 255) == 0) token.ThrowIfCancellationRequested();
                int row = order[position];
                var candidate = index.Name(row, scratch);
                if (!candidate.Equals(name, StringComparison.OrdinalIgnoreCase)) break;
                if (candidate.SequenceEqual(name) && MatchesFile(index, row, parentId)) return (index, row);
            }
        }
        return null;
    }

    private static bool MatchesFile(SearchIndex index, int row, ulong parentId)
    {
        ref readonly var record = ref index.Records[row];
        ulong reference = index.DirectoryReferences![record.Directory];
        if (DirectoryPathResolver.IsRoot(reference)) reference = 0;
        return reference == parentId && !record.IsDirectory && index.IsVisible(row);
    }

    internal bool IsExcluded(ulong id, Filter? filter) =>
        filter is not null && IsExcluded(folders.GetValueOrDefault(id), filter);

    private static bool IsExcluded(Folder? folder, Filter filter)
    {
        if (filter.Directories.Count == 0) return false;
        for (; folder is not null; folder = folder.Parent)
            if (filter.Directories.Contains(folder.Id)) return true;
        return false;
    }
}
