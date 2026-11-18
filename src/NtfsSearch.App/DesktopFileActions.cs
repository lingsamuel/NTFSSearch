using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NtfsSearch.App;

internal static class DesktopFileActions
{
    // NTFS 名称可包含未配对的 UTF-16 代理项。操作使用原始路径，JSON 字段用于显示。
    internal static string EncodePath(string path) => Convert.ToBase64String(MemoryMarshal.AsBytes(path.AsSpan()));

    internal static void Run(string action, string key, long windowHandle)
    {
        byte[] bytes = Convert.FromBase64String(key);
        string path = new(MemoryMarshal.Cast<byte, char>(bytes));
        switch (action)
        {
            case "open":
                using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
                break;
            case "reveal":
                var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                start.ArgumentList.Add("/select," + path);
                using (Process.Start(start)) { }
                break;
            case "copy":
                CopyPath(path, (nint)windowHandle);
                break;
        }
    }

    private static void CopyPath(string path, nint windowHandle)
    {
        const uint moveableMemory = 2, unicodeText = 13;
        nint memory = GlobalAlloc(moveableMemory, (nuint)((path.Length + 1) * sizeof(char)));
        if (memory == 0) throw new Win32Exception();
        try
        {
            nint address = GlobalLock(memory);
            if (address == 0) throw new Win32Exception();
            try
            {
                Marshal.Copy(path.ToCharArray(), 0, address, path.Length);
                Marshal.WriteInt16(address, path.Length * sizeof(char), 0);
            }
            finally { GlobalUnlock(memory); }

            if (!OpenClipboard(windowHandle)) throw new Win32Exception();
            try
            {
                if (!EmptyClipboard() || SetClipboardData(unicodeText, memory) == 0)
                    throw new Win32Exception();
                // 剪贴板接管句柄，后端退出后仍可粘贴。
                memory = 0;
            }
            finally { CloseClipboard(); }
        }
        finally { if (memory != 0) GlobalFree(memory); }
    }

    [DllImport("kernel32", SetLastError = true)] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32", SetLastError = true)] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32")] private static extern nint GlobalFree(nint memory);
    [DllImport("user32", SetLastError = true)] private static extern bool OpenClipboard(nint window);
    [DllImport("user32", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32", SetLastError = true)] private static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32")] private static extern bool CloseClipboard();
}
