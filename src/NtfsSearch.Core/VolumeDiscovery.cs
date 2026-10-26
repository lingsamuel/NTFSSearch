using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NtfsSearch.Core;

public sealed record NtfsVolume(string Id, string Root, long TotalBytes, string Label);

public static class VolumeDiscovery
{
    public static IReadOnlyList<NtfsVolume> Discover()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("自动 NTFS 发现仅支持 Windows。");
        var volumes = new List<NtfsVolume>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable) || !drive.IsReady
                    || !drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                var id = new StringBuilder(64);
                if (!GetVolumeNameForVolumeMountPointW(drive.RootDirectory.FullName, id, id.Capacity))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                volumes.Add(new(id.ToString(), drive.RootDirectory.FullName, drive.TotalSize, drive.VolumeLabel));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            { /* 离线或锁定卷不应阻止发现其他磁盘；下一轮重新探测。 */ }
        }
        return volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);
}
