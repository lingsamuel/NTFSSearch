using System.ComponentModel;
using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

internal static class NativeSubtree
{
    internal sealed record Node(string Path, ulong FileReference, bool IsDirectory, bool IsReparse);

    internal static IEnumerable<IndexedFileState> Enumerate(char drive, ulong directoryReference, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var volume = NativeJournal.Open(drive);
        using var root = NativeFileState.OpenById(volume, directoryReference);
        if (root.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var identity = NativeFileState.Identity(root);
        if (identity.Reference != directoryReference || (identity.Attributes & 16) == 0)
            throw new ArgumentException("子树起点必须是仍存在的目录身份。", nameof(directoryReference));
        string path = NativeFileState.FinalPath(root) ?? throw new IOException("无法解析子树起点路径。");
        if (!path.StartsWith($"{char.ToUpperInvariant(drive)}:\\", StringComparison.OrdinalIgnoreCase))
            throw new IOException("子树起点不属于指定卷。");
        var first = new Node(path, directoryReference, true, (identity.Attributes & 0x400) != 0);

        IEnumerable<Node> Children(Node node)
        {
            token.ThrowIfCancellationRequested();
            // 路径可能在枚举期间被替换为junction，重新验证身份和属性后才下钻。
            using var directory = NativeFileState.OpenPath(node.Path, followReparse: false);
            if (directory.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error is 2 or 3) yield break;
                throw new Win32Exception(error);
            }
            var current = NativeFileState.Identity(directory);
            if (current.Reference != node.FileReference || current.Serial != identity.Serial || (current.Attributes & 0x400) != 0) yield break;
            using var paths = Directory.EnumerateFileSystemEntries(node.Path, "*", new EnumerationOptions
            {
                RecurseSubdirectories = false,
                AttributesToSkip = 0,
                IgnoreInaccessible = false,
                ReturnSpecialDirectories = false
            }).GetEnumerator();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool more;
                try { more = paths.MoveNext(); }
                catch (DirectoryNotFoundException) { yield break; }
                if (!more) yield break;
                using var child = NativeFileState.OpenPath(paths.Current, followReparse: false);
                if (child.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is 2 or 3) continue;
                    throw new Win32Exception(error);
                }
                var childIdentity = NativeFileState.Identity(child);
                if (childIdentity.Serial != identity.Serial) continue;
                yield return new(paths.Current, childIdentity.Reference,
                    (childIdentity.Attributes & 16) != 0, (childIdentity.Attributes & 0x400) != 0);
            }
        }
        MftMetadataReader? metadata = null;
        IndexedFileState ReadRaw(ulong reference) => (metadata ??= new MftMetadataReader(drive)).ReadFileState(drive, reference);
        try
        {
            foreach (var state in Walk(first, Children, node => NativeFileState.Read(volume, node.FileReference, ReadRaw), token))
                yield return state;
        }
        finally { metadata?.Dispose(); }
    }

    internal static IEnumerable<IndexedFileState> Walk(Node root, Func<Node, IEnumerable<Node>> children,
        Func<Node, IndexedFileState> readState, CancellationToken token)
    {
        var visited = new HashSet<ulong>();
        var stack = new Stack<IEnumerator<Node>>();
        stack.Push(((IEnumerable<Node>)new[] { root }).GetEnumerator());
        try
        {
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var cursor = stack.Peek();
                if (!cursor.MoveNext()) { stack.Pop().Dispose(); continue; }
                var node = cursor.Current;
                if (!visited.Add(node.FileReference)) continue;
                var state = readState(node);
                if (state.Links.Count == 0 && !DirectoryPathResolver.IsRoot(node.FileReference)) continue;
                if (state.Links.Count > 0) yield return state;
                if (node.IsDirectory && !node.IsReparse) stack.Push(children(node).GetEnumerator());
            }
        }
        finally { while (stack.Count > 0) stack.Pop().Dispose(); }
    }
}
