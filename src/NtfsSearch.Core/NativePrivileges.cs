using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

internal static class NativePrivileges
{
    private static readonly Lazy<bool> backupEnabled = new(EnableBackup, LazyThreadSafetyMode.ExecutionAndPublication);
    internal static void EnsureBackupPrivilege() => _ = backupEnabled.Value;

    private static bool EnableBackup()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("备份读取权限仅支持Windows。");
        if (!OpenProcessToken(GetCurrentProcess(), 0x0020 | 0x0008, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken失败，无法启用SeBackupPrivilege。");
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "LookupPrivilegeValue失败：SeBackupPrivilege。");
            // 只启用令牌已有的备份读取权限，不申请SeRestore或所有权/写入权限。
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            bool adjusted = AdjustTokenPrivileges(token, false, ref privileges, 0, 0, 0);
            ValidateAdjustment(adjusted, Marshal.GetLastWin32Error());
        }
        return true;
    }

    internal static void ValidateAdjustment(bool adjusted, int error)
    {
        // AdjustTokenPrivileges可以返回TRUE同时报告ERROR_NOT_ALL_ASSIGNED，不能据TRUE认定成功。
        if (!adjusted || error != 0)
            throw new Win32Exception(error, $"无法启用SeBackupPrivilege：{new Win32Exception(error).Message}");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenPrivileges { public uint Count; public Luid Luid; public uint Attributes; }
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        ref TokenPrivileges privileges, uint length, nint previous, nint returned);
}
