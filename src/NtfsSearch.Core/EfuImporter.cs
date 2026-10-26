using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace NtfsSearch.Core;

public static class EfuImporter
{
    public static SearchIndex Read(TextReader reader, CancellationToken cancellationToken = default,
        IProgress<long>? progress = null, IProgress<string>? stages = null)
    {
        // 复用框架 CSV 解析器，处理双引号、逗号和跨行字段。
        using var csv = new TextFieldParser(reader) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        csv.SetDelimiters(",");
        string[] header = csv.ReadFields() ?? throw new InvalidDataException("缺少 EFU 表头。");
        int Find(string column) => Array.FindIndex(header, h => h.TrimStart('\ufeff').Equals(column, StringComparison.OrdinalIgnoreCase));
        int filename = Find("Filename"), size = Find("Size"), modified = Find("Date Modified"), attributes = Find("Attributes");
        if (filename < 0) throw new InvalidDataException("EFU 缺少 Filename 列。");
        var builder = new IndexBuilder();
        long count = 0;
        while (!csv.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] fields;
            try { fields = csv.ReadFields() ?? []; }
            catch (MalformedLineException e) { throw new InvalidDataException($"CSV 第 {e.LineNumber} 行格式错误。", e); }
            string Value(int column) => column < 0 ? "" : column < fields.Length ? fields[column]
                : throw new InvalidDataException($"CSV 第 {csv.LineNumber} 行缺少字段。");
            string full = Value(filename).Replace('/', '\\').TrimEnd('\\');
            if (full.Length == 2 && full[1] == ':') continue;
            if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\')
                throw new InvalidDataException("EFU 必须包含本地 Windows 绝对路径。");
            int slash = full.LastIndexOf('\\');
            bool isDirectory = Value(attributes) is { Length: > 0 } attr && (uint.Parse(attr, CultureInfo.InvariantCulture) & 16) != 0;
            long? bytes = Value(size) is { Length: > 0 } bytesText ? long.Parse(bytesText, CultureInfo.InvariantCulture) : null;
            long? ticks = null;
            if (Value(modified) is { Length: > 0 } time)
            {
                if (long.TryParse(time, CultureInfo.InvariantCulture, out long fileTime))
                    ticks = DateTime.FromFileTimeUtc(fileTime).Ticks;
                else if (DateTime.TryParse(time, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)) ticks = date.Ticks;
                else throw new InvalidDataException("EFU 包含无效的修改时间。");
            }
            builder.Add(full[..(slash + 1)], full.AsSpan(slash + 1), bytes, ticks, isDirectory);
            if ((++count & 65535) == 0) progress?.Report(count);
        }
        progress?.Report(count);
        return builder.Build(cancellationToken, stages);
    }
}
