namespace NtfsSearch.Core;

internal sealed class DirectoryPathResolver(char drive,
    IReadOnlyDictionary<ulong, (ulong Parent, string Name)> directories, Func<ulong, string?> livePath)
{
    private readonly string root = $"{drive}:\\";
    private readonly Dictionary<ulong, string> paths = [];
    private readonly HashSet<ulong> unreachable = [];
    internal static bool IsRoot(ulong reference) => (reference & 0x0000FFFFFFFFFFFFUL) == 5;

    public string? Resolve(ulong reference)
    {
        if (IsRoot(reference)) return root;
        if (paths.TryGetValue(reference, out string? cached)) return cached;
        if (unreachable.Contains(reference)) return null;
        var chain = new List<ulong>();
        var visiting = new HashSet<ulong>();
        ulong current = reference;
        while (!IsRoot(current) && !paths.ContainsKey(current))
        {
            if (unreachable.Contains(current) || !visiting.Add(current) || chain.Count > 16384)
            { foreach (ulong id in chain) unreachable.Add(id); unreachable.Add(reference); return null; }
            if (!directories.TryGetValue(current, out var directory))
            {
                string? path = livePath(current);
                if (path is null)
                { unreachable.Add(current); foreach (ulong id in chain) unreachable.Add(id); return null; }
                paths[current] = path; break;
            }
            chain.Add(current); current = directory.Parent;
        }
        string resolved = IsRoot(current) ? root : paths[current];
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            resolved += directories[chain[i]].Name + '\\'; paths[chain[i]] = resolved;
        }
        return paths[reference];
    }
}
