using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NtfsSearch.App;

internal static class DesktopFileActions
{
    // NTFS 名称可包含未配对的 UTF-16 代理项。操作使用原始路径，JSON 字段用于显示。
    internal static string EncodePath(string path) => Convert.ToBase64String(MemoryMarshal.AsBytes(path.AsSpan()));

    internal static string DecodePath(string key) => new(MemoryMarshal.Cast<byte, char>(Convert.FromBase64String(key)));

    internal static Task OpenAsync(string key, bool chooseApplication)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // 在 STA 线程调用资源管理器，Shell 处理程序响应期间桌面仍能继续处理消息。
        var thread = new Thread(() =>
        {
            try
            {
                string path = DecodePath(key);
                if (chooseApplication) OpenWith(path);
                else
                {
                    const int noAssociation = 1155;
                    try { using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
                    catch (Win32Exception error) when (error.NativeErrorCode == noAssociation)
                    { OpenWith(path); }
                }
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    internal static void Run(string action, string key, long windowHandle)
    {
        string path = DecodePath(key);
        switch (action)
        {
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

    private static void OpenWith(string path)
    {
        // 由资源管理器解释条目的打开方式命令，使用系统为该路径提供的应用选择器。
        var shellType = Type.GetTypeFromProgID("Shell.Application", throwOnError: true)!;
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic? folder = shell.NameSpace(Path.GetDirectoryName(path));
        dynamic? item = folder?.ParseName(Path.GetFileName(path));
        if (item is null) throw new FileNotFoundException("资源管理器找不到此文件。", path);
        item.InvokeVerb("openas");
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
