using System.Text;
namespace NtfsSearch.Core;

internal static class AsciiCaseShape
{
    // 字母大小写最多变化一次时，一个边界即可无损恢复；数字标点不参与边界判断。
    internal static bool TryEncode(ReadOnlySpan<char> name, out byte cut, out bool suffixUpper)
    {
        cut = 0; suffixUpper = false;
        if (name.IsEmpty || name.Length > 255 || name.ContainsAnyInRange((char)128, char.MaxValue)) return false;
        int previous = -1;
        bool changed = false;
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            int current = c >= 'A' && c <= 'Z' ? 1 : c >= 'a' && c <= 'z' ? 0 : -1;
            if (current < 0) continue;
            if (previous >= 0 && current != previous)
            {
                if (changed) return false;
                changed = true; cut = (byte)i; suffixUpper = current == 1;
            }
            previous = current;
        }
        if (!changed && previous == 1) cut = (byte)name.Length;
        return true;
    }
    internal static void Restore(Span<char> text, byte cut, bool suffixUpper)
    {
        Span<char> upper = suffixUpper ? text[cut..] : text[..cut];
        Ascii.ToUpperInPlace(upper, out int written);
        if (written != upper.Length) throw new InvalidOperationException();
    }
}
