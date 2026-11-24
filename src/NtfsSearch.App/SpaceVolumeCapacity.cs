using System.ComponentModel;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed record SpaceVolumeCapacity(double UsedFraction, string UsedText, string FreeText, string TotalText)
{
    internal static SpaceVolumeCapacity? FromBytes(long total, long free)
    {
        if (total <= 0 || free < 0 || free > total) return null;
        long used = total - free;
        return new((double)used / total, FileSizeDisplay.Format((Int128)used),
            FileSizeDisplay.Format((Int128)free), FileSizeDisplay.Format((Int128)total));
    }

    // 容量和已发布索引必须来自同一卷，盘符被重新分配时只显示盘符。
    internal static SpaceVolumeCapacity? Read(NtfsVolume volume)
    {
        try
        {
            var drive = new DriveInfo(volume.Root);
            // 总量和可用量采用同一 Windows 用户配额口径。
            long total = drive.TotalSize;
            long free = drive.AvailableFreeSpace;
            return string.Equals(VolumeDiscovery.IdentityAtRoot(volume.Root), volume.Id, StringComparison.OrdinalIgnoreCase)
                ? FromBytes(total, free) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }
}
