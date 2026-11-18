namespace NtfsSearch.App;

internal static class FileSizeDisplay
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];

    internal static string Format(long? bytes)
    {
        if (bytes is not long size) return "—";
        decimal value = size;
        int unit = 0;
        while (value >= 1024)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.##} {Units[unit]}";
    }
}
