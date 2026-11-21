namespace NtfsSearch.App;

internal static class FileSizeDisplay
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    internal static string Format(long? bytes)
        => bytes is long size ? Format((Int128)size) : "—";

    internal static string Format(Int128 bytes)
    {
        Int128 scale = 1;
        int unit = 0;
        while (unit + 1 < Units.Length && bytes >= scale * 1024)
        {
            scale *= 1024;
            unit++;
        }
        decimal value = (decimal)(bytes / scale) + (decimal)(bytes % scale) / (decimal)scale;
        return $"{value:0.##} {Units[unit]}";
    }
}
