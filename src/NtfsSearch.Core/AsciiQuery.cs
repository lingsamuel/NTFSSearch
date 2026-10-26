using System.Text;
namespace NtfsSearch.Core;

internal static class AsciiQuery
{
    internal static byte[]? Encode(string text)
    {
        if (text.AsSpan().ContainsAnyInRange((char)128, char.MaxValue)) return null;
        var result = new byte[text.Length];
        Ascii.ToLower(text, result, out int written);
        if (written != text.Length) throw new InvalidOperationException("ASCII查询编码失败。");
        return result;
    }
}
