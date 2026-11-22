using System.Text;

namespace NtfsSearch.Core;

internal static class SearchQuery
{
    public static string[] Parse(string text)
    {
        var terms = new List<string>();
        var buffer = new StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (!quoted && char.IsWhiteSpace(c))
            {
                if (buffer.Length > 0) { terms.Add(buffer.ToString()); buffer.Clear(); }
            }
            else buffer.Append(c == '/' ? '\\' : c);
        }
        if (buffer.Length > 0) terms.Add(buffer.ToString());
        return terms.ToArray();
    }

    // 仅用于已选入结果页的路径。UTF-16 的起止位置以完整路径为基准，结束位置不包含在范围内。
    internal static int[] MatchRanges(string fullPath, int nameLength, string[] terms, bool matchPath)
    {
        if (terms.Length == 0) return [];
        ReadOnlySpan<char> path = fullPath.AsSpan();
        Span<int> ends = path.Length <= 512 ? stackalloc int[path.Length] : new int[path.Length];
        ends.Clear();
        foreach (string term in terms)
        {
            // 有路径分隔符的条件始终限定路径，即使搜索范围为「仅名称」。
            int next = matchPath || term.Contains('\\') ? 0 : path.Length - nameLength;
            while (next <= path.Length - term.Length)
            {
                int index = path[next..].IndexOf(term, StringComparison.OrdinalIgnoreCase);
                if (index < 0) break;
                int start = next + index;
                ends[start] = Math.Max(ends[start], start + term.Length);
                next = start + 1;
            }
        }

        var ranges = new List<int>();
        for (int start = 0; start < ends.Length;)
        {
            int end = ends[start];
            if (end == 0) { start++; continue; }
            for (int index = start + 1; index < end; index++) end = Math.Max(end, ends[index]);
            ranges.Add(start);
            ranges.Add(end);
            start = end;
        }
        return ranges.ToArray();
    }
}
