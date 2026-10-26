namespace NtfsSearch.Core;

public sealed class JournalChangeResolver(string dataDirectory, Func<ulong, IndexedFileState> readState)
{
    private const uint Relevant = 0x0001b33f;
    private readonly string dataRoot = Path.GetFullPath(dataDirectory).Replace('/', '\\').TrimEnd('\\') + '\\';
    private readonly HashSet<ulong> internalFiles = [];
    // 仅跟踪排除边界相关的混合硬链接及尚未完成的改名，不为全卷普通对象建身份集合。
    private readonly HashSet<ulong> externallyLinkedInternalFiles = [];
    private readonly HashSet<ulong> pendingExternalRenames = [];
    private readonly List<ulong> reentered = [];
    public IReadOnlyList<ulong> ReenteredDirectories => reentered;
    public LiveTransaction Resolve(JournalBatch batch, JournalPosition durableFrom, CancellationToken token = default)
    {
        reentered.Clear();
        var files = new List<IndexedFileState>();
        foreach (var group in batch.Records.Where(r => !DirectoryPathResolver.IsRoot(r.FileReference) && (r.Reason & Relevant) != 0).GroupBy(r => r.FileReference))
        {
            token.ThrowIfCancellationRequested();
            uint reason = group.Aggregate(0u, (mask, record) => mask | record.Reason);
            if (internalFiles.Contains(group.Key) && !externallyLinkedInternalFiles.Contains(group.Key)
                && (reason & 0x00013300) == 0) continue;
            IndexedFileState? current = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try { current = readState(group.Key); break; }
                catch (IOException) when (attempt < 2) { token.ThrowIfCancellationRequested(); Thread.Sleep(25); }
            }
            if (current is null) throw new IOException("无法读取变化对象的当前状态。");
            bool hasInternal = current.Links.Any(IsInternal);
            bool wasInternal = internalFiles.Contains(group.Key);
            bool hadExternal = externallyLinkedInternalFiles.Contains(group.Key) || pendingExternalRenames.Contains(group.Key);
            var links = current.Links.Where(link => !IsInternal(link)).ToArray();
            bool renamedFromExternal = group.Where(r => (r.Reason & 0x00001000) != 0)
                .Any(record => !IsInternalParent(record.ParentReference));
            if (hasInternal) internalFiles.Add(group.Key);
            if (hasInternal && links.Length > 0) externallyLinkedInternalFiles.Add(group.Key);
            else externallyLinkedInternalFiles.Remove(group.Key);
            if (renamedFromExternal && links.Length > 0 && (reason & 0x00002000) == 0)
                pendingExternalRenames.Add(group.Key);
            if ((reason & 0x00002200) != 0) pendingExternalRenames.Remove(group.Key);

            // 原来公开的名称移入Data或移除最后一个公开硬链接时必须发布删除。
            // 纯内部创建、写入和临时文件原子改名继续只推进水位，避免自身WAL反馈。
            bool removedExternal = hadExternal || renamedFromExternal
                || (reason & 0x00010000) != 0 && !wasInternal && (reason & 0x00000100) == 0;
            if (hasInternal && links.Length == 0 && !removedExternal) continue;
            if (links.Length == 0 && !removedExternal && group.All(r => IsInternalParent(r.ParentReference))) continue;
            if (current.IsDirectory && links.Length > 0 && (reason & 0x00003000) != 0)
            {
                foreach (var old in group.Where(r => (r.Reason & 0x00001000) != 0))
                {
                    if (DirectoryPathResolver.IsRoot(old.ParentReference)) continue;
                    if (internalFiles.Contains(old.ParentReference)) { wasInternal = true; break; }
                    try
                    {
                        var parent = readState(old.ParentReference);
                        if (parent.Links.Count == 0 || parent.Links.Any(IsInternal)) { wasInternal = true; break; }
                    }
                    catch (IOException) { wasInternal = true; break; }
                }
                if (wasInternal) reentered.Add(group.Key);
            }
            if (!hasInternal && links.Length > 0) internalFiles.Remove(group.Key);
            files.Add(current with { Links = links, IsDirectory = current.IsDirectory || group.Any(r => (r.Attributes & 16) != 0) });
        }
        return new(durableFrom, batch.To, files);
    }

    public void MarkInternal(ulong reference) => internalFiles.Add(reference);
    public IndexedFileState FilterState(IndexedFileState state) => state with { Links = state.Links.Where(link => !IsInternal(link)).ToArray() };
    private bool IsInternalParent(ulong reference)
    {
        if (DirectoryPathResolver.IsRoot(reference)) return false;
        if (internalFiles.Contains(reference)) return true;
        try
        {
            var parent = readState(reference);
            if (parent.Links.Count > 0 && parent.Links.All(IsInternal))
            { internalFiles.Add(reference); return true; }
        }
        catch (IOException) { }
        // 无法确认父目录在排除区时保守发布空状态，防止既有公开行残留。
        return false;
    }
    private bool IsInternal(IndexedLink link)
    {
        if (link.ParentPath.StartsWith(dataRoot, StringComparison.OrdinalIgnoreCase)) return true;
        string root = dataRoot.TrimEnd('\\');
        return link.ParentPath.Length + link.Name.Length == root.Length
            && root.AsSpan().StartsWith(link.ParentPath, StringComparison.OrdinalIgnoreCase)
            && root.AsSpan(link.ParentPath.Length).Equals(link.Name, StringComparison.OrdinalIgnoreCase);
    }
}
