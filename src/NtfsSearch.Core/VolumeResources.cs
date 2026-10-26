using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

public static class VolumeResources
{
    public static long AvailableMemoryBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
            if (GlobalMemoryStatusEx(ref status)) return Math.Max(1, checked((long)status.AvailablePhysical));
            return 2L << 30;
        }
        return Math.Max(1, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);
    }

    internal static string[]? PhysicalDisks(char drive)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = VolumeNative.Open(drive, 0);
        if (handle.IsInvalid) return null;
        var buffer = new byte[128];
        bool success = VolumeNative.Control(handle, 0x00560000, null, buffer, out int bytes);
        if (!success && Marshal.GetLastWin32Error() == 234)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            if (count is 0 or > 4096) return null;
            buffer = new byte[checked(8 + (int)count * 24)];
            success = VolumeNative.Control(handle, 0x00560000, null, buffer, out bytes);
        }
        if (!success || bytes < 8 || bytes > buffer.Length) return null;
        try { return ParsePhysicalDisks(buffer.AsSpan(0, bytes)); }
        catch (InvalidDataException) { return null; }
    }

    internal static string[] ParsePhysicalDisks(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) throw new InvalidDataException("卷设备列表不完整。");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count is 0 or > 4096 || buffer.Length < 8L + count * 24L) throw new InvalidDataException("卷设备列表越界。");
        var devices = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++) devices.Add("disk:" + BinaryPrimitives.ReadUInt32LittleEndian(buffer[(8 + i * 24)..]));
        return devices.Order(StringComparer.Ordinal).ToArray();
    }

    public static long EstimateBuildBytes(char drive, long cachedBytes)
    {
        long estimated = Math.Max(256L << 20, cachedBytes > long.MaxValue / 2 ? long.MaxValue : cachedBytes * 2);
        if (!OperatingSystem.IsWindows()) return estimated;
        using var handle = VolumeNative.Open(drive, 0);
        var buffer = new byte[96];
        if (handle.IsInvalid || !VolumeNative.Control(handle, 0x00090064, null, buffer, out int bytes) || bytes < 64) return estimated;
        uint recordBytes = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(48));
        long mftBytes = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(56));
        if (recordBytes == 0 || mftBytes <= 0) return estimated;
        long records = mftBytes / recordBytes;
        // 调度估算，不是常驻占用或硬上限；缓存提供实际名称分布，MFT补足首次扫描的规模信息。
        return Math.Max(estimated, records > long.MaxValue / 128 ? long.MaxValue : records * 128);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, MemoryLoad;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtended;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
