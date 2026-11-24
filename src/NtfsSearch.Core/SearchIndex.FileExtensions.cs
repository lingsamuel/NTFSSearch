using System.Text.RegularExpressions;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    /// <summary>用索引中的名称和父目录判定文件后缀，供选项汇总与查询共用。</summary>
    internal ReadOnlySpan<char> FileExtension(int id, Span<char> scratch)
    {
        var name = Name(id, scratch);
        var extension = Path.GetExtension(name);
        // Windows Shell 将最后一个点后含普通空格的名称视为无后缀。
        if (extension.IsEmpty || extension.Contains(' ')) return default;

        int separator = name.LastIndexOf('@');
        if (separator > 0)
        {
            var version = name[(separator + 1)..];
            if (version.StartsWith("v", StringComparison.Ordinal)
                && IsGoChecksumLookup(Directories[Records[id].Directory])
                && GoModuleVersion().IsMatch(version)) return default;
        }
        return extension;
    }

    private static bool IsGoChecksumLookup(string directory)
    {
        // Go 使用 GOMODCACHE 下的固定相对路径，校验数据库的名字也可以带路径分量。
        const string cacheRoot = @"\cache\download\sumdb\";
        int cache = directory.IndexOf(cacheRoot, StringComparison.OrdinalIgnoreCase);
        return cache >= 0 && directory.AsSpan(cache + cacheRoot.Length)
            .IndexOf(@"\lookup\", StringComparison.OrdinalIgnoreCase) > 0;
    }

    // Go 的预发布标识支持大写字母，缓存中会把它们编码为感叹号加小写字母。
    [GeneratedRegex("""
        \A v[0-9]+\.[0-9]+\.[0-9]+
        (?:-(?:[0-9a-z-]|![a-z])+(?:\.(?:[0-9a-z-]|![a-z])+)*)?
        (?:\+incompatible)? \z
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex GoModuleVersion();
}
