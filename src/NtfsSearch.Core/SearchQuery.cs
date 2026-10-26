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
            else buffer.Append(c);
        }
        if (buffer.Length > 0) terms.Add(buffer.ToString());
        return terms.ToArray();
    }
}
