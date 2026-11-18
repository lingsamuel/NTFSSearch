using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace NtfsSearch.Core;

public sealed record NtfsVolume(string Id, string Root, long TotalBytes, string Label, string[]? PhysicalDisks = null);

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
                string root = drive.RootDirectory.FullName;
                volumes.Add(new(IdentityAtRoot(root), root, drive.TotalSize, drive.VolumeLabel, VolumeResources.PhysicalDisks(drive.Name[0])));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            { /* 离线或锁定卷不应阻止发现其他磁盘；下一轮重新探测。 */ }
        }
        return volumes.OrderBy(v => v.Root, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static string IdentityAtRoot(string root)
    {
        var id = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, id, id.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return id.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, int length);
}
