using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NtfsSearch.App;

internal sealed class FileTypeIcons
{
    private const int BatchSize = 16;
    private const int CacheSize = 512;
    private const uint FileAttributeNormal = 0x80;
    private const uint ShellSmallIcon = 0x1;
    private const uint ShellUseFileAttributes = 0x10;
    private const uint ShellIcon = 0x100;
    private const uint ShellSystemIconIndex = 0x4000;
    private const uint ShellIconFlags = ShellSmallIcon | ShellUseFileAttributes | ShellSystemIconIndex;

    private readonly SemaphoreSlim lookup = new(1);
    private readonly Dictionary<string, string?> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> cacheOrder = new();
    private int? genericIconIndex;

    // 每批只接收扩展名，限制 Shell 工作量并避免搜索线程参与图标提取。
    internal async Task<Dictionary<string, string?>> GetAsync(string[] extensions, CancellationToken token = default)
    {
        await lookup.WaitAsync(token);
        try { return await Task.Run(() => ReadIcons(extensions, token), token); }
        finally { lookup.Release(); }
    }

    private Dictionary<string, string?> ReadIcons(string[] extensions, CancellationToken token)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        const uint coinitMultithreaded = 0;
        if (CoInitializeEx(0, coinitMultithreaded) < 0) return result;

        try
        {
            foreach (string extension in extensions.Take(BatchSize))
            {
                token.ThrowIfCancellationRequested();
                if (extension is not { Length: >= 2 and <= 255 } || extension[0] != '.' ||
                    extension.AsSpan(1).ContainsAny(".\\/:*?\"<>|\0")) continue;

                string key = extension.ToLowerInvariant();
                if (result.ContainsKey(key)) continue;
                if (!cache.TryGetValue(key, out string? source))
                {
                    source = ReadIcon(key);
                    if (cache.Count == CacheSize) cache.Remove(cacheOrder.Dequeue());
                    cache.Add(key, source);
                    cacheOrder.Enqueue(key);
                }
                result.Add(key, source);
            }
            return result;
        }
        finally { CoUninitialize(); }
    }

    private string? ReadIcon(string extension)
    {
        var size = (uint)Marshal.SizeOf<ShellFileInfo>();
        if (genericIconIndex is null &&
            SHGetFileInfoW("NTFS Search File", FileAttributeNormal, out var generic, size, ShellIconFlags) != 0)
            genericIconIndex = generic.IconIndex;

        // 用虚拟文件属性查询关联图标。系统返回通用图标时保留界面的占位图标。
        var found = SHGetFileInfoW(extension, FileAttributeNormal, out var info, size, ShellIconFlags | ShellIcon);
        if (info.Icon == 0) return null;
        try
        {
            if (found == 0 || info.IconIndex == genericIconIndex) return null;
            using var icon = Icon.FromHandle(info.Icon);
            using var bitmap = icon.ToBitmap();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            return "data:image/png;base64," + Convert.ToBase64String(png.GetBuffer(), 0, (int)png.Length);
        }
        catch (ExternalException) { return null; }
        finally { DestroyIcon(info.Icon); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }

    [DllImport("shell32", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern nuint SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);

    [DllImport("ole32")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32")] private static extern void CoUninitialize();
    [DllImport("user32")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(nint icon);
}
