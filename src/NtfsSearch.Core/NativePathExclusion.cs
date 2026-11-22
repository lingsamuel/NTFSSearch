namespace NtfsSearch.Core;

internal static class NativePathExclusion
{
    // 父路径已以反斜线结束；仅比较片段，避免为每条索引记录分配完整路径。
    internal static bool Contains(string? excluded, string parent, ReadOnlySpan<char> name)
    {
        if (excluded is null) return false;
        ReadOnlySpan<char> directory = excluded.AsSpan().TrimEnd('\\');
        ReadOnlySpan<char> parentPath = parent.AsSpan();
        if (parentPath.Length > directory.Length && parentPath[directory.Length] == '\\'
            && parentPath[..directory.Length].Equals(directory, StringComparison.OrdinalIgnoreCase)) return true;
        return directory.Length == parentPath.Length + name.Length
            && directory[..parentPath.Length].Equals(parentPath, StringComparison.OrdinalIgnoreCase)
            && directory[parentPath.Length..].Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ContainsPortableData(string directory, string parent, ReadOnlySpan<char> name) =>
        parent.AsSpan().TrimEnd('\\').Equals(directory.AsSpan().TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
        && IsPortableDataName(name);

    internal static bool IsPortableDataName(ReadOnlySpan<char> name) =>
        MatchesAtomicFile(name, ExitSnapshotStore.FileName) || MatchesAtomicFile(name, DesktopSettings.FileName);

    private static bool MatchesAtomicFile(ReadOnlySpan<char> name, string file)
    {
        if (!name.StartsWith(file, StringComparison.OrdinalIgnoreCase)) return false;
        var suffix = name[file.Length..];
        // 原子写入使用 .<32 位 GUID>.tmp。相近的普通文件名仍参与索引。
        return suffix.IsEmpty || (suffix.Length == 37 && suffix[0] == '.'
            && suffix.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            && Guid.TryParseExact(suffix.Slice(1, 32), "N", out _));
    }
}
