using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

public static class NtfsEnumerator
{
    public static SearchIndex Scan(char drive, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null, Action<long>? unreachableRecords = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS 枚举仅支持 Windows。");
        drive = char.ToUpperInvariant(drive);
        if (drive is < 'A' or > 'Z') throw new ArgumentException("请提供有效的盘符。", nameof(drive));
        return ScanWindows(drive, cancellationToken, progress, unreachableRecords);
    }

    [SupportedOSPlatform("windows")]
    private static SearchIndex ScanWindows(char drive, CancellationToken token, IProgress<string>? progress, Action<long>? unreachableRecords)
    {
        using var handle = OpenVolume(drive);
        return ScanOpenedVolume(drive, handle, token, progress, unreachableRecords);
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenVolume(char drive)
    {
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationW($"{drive}:\\", null, 0, out _, out _, out _, fs, fs.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("只支持 NTFS 卷。");
        var handle = CreateFileW($"\\\\.\\{drive}:", 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error == 5) throw new UnauthorizedAccessException("读取 NTFS 卷需要管理员权限。请用管理员终端运行 scan；查询已保存的索引不需要管理员权限。");
            throw new Win32Exception(error);
        }
        return handle;
    }

    [SupportedOSPlatform("windows")]
    private static SearchIndex ScanOpenedVolume(char drive, SafeFileHandle handle, CancellationToken token, IProgress<string>? progress, Action<long>? unreachableRecords)
    {
        var directories = new Dictionary<ulong, (ulong Parent, string Name)>();
        long recordCount = 0;
        progress?.Report("枚举目录关系（第 1 遍）…");
        Enumerate(handle, token, (frn, parent, name, attributes) =>
        {
            if (!IsRoot(frn) && !name.IsEmpty) recordCount++;
            if ((attributes & 16) != 0 && !IsRoot(frn)) directories[frn] = (parent, name.ToString());
            if ((recordCount & 262143) == 0) progress?.Report($"已读取 {recordCount:N0} 条记录…");
        });
        if (recordCount > int.MaxValue) throw new NotSupportedException("原型单索引上限为 21 亿目录项。");
        var resolver = new DirectoryPathResolver(drive, directories, id => DirectoryById(handle, id, drive));
        progress?.Report("建立名称索引（第 2 遍）…");
        var builder = new IndexBuilder((int)recordCount);
        long added = 0, skipped = 0;
        Enumerate(handle, token, (frn, parent, name, attributes) =>
        {
            if (IsRoot(frn) || name.IsEmpty) return;
            string? parentPath = resolver.Resolve(parent);
            if (parentPath is null) { skipped++; return; }
            builder.Add(parentPath, name, isDirectory: (attributes & 16) != 0);
            if ((++added & 262143) == 0) progress?.Report($"已索引 {added:N0} 条记录…");
        });
        unreachableRecords?.Invoke(skipped);
        progress?.Report("建立排序索引…");
        return builder.Build(token, progress);
    }

    private static bool IsRoot(ulong frn) => DirectoryPathResolver.IsRoot(frn);

    [SupportedOSPlatform("windows")]
    private static string? DirectoryById(SafeFileHandle volume, ulong id, char drive)
    {
        var descriptor = new FileIdDescriptor { Size = (uint)Marshal.SizeOf<FileIdDescriptor>(), FileId = id };
        using var handle = OpenFileById(volume, ref descriptor, 0, 7, 0, 0x02000000);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 5 or 87 or 1168) return null;
            throw new Win32Exception(error);
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 16) == 0) return null;
        var path = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        if (length >= path.Capacity)
        {
            if (length > 32768) return null;
            path = new((int)length + 1); length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 0);
        }
        if (length >= path.Capacity) return null;
        if (length == 0)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 5) return null;
            throw new Win32Exception(error);
        }
        string result = path.ToString();
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        if (!result.StartsWith($"{drive}:\\", StringComparison.OrdinalIgnoreCase)) return null;
        return result.EndsWith('\\') ? result : result + '\\';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdDescriptor { public uint Size, Type; public ulong FileId, Reserved; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, uint access, uint share, nint security, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);

    public static int Probe(char drive, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS 探测仅支持 Windows。");
        drive = char.ToUpperInvariant(drive);
        if (drive is < 'A' or > 'Z') throw new ArgumentException("无效盘符。", nameof(drive));
        using var handle = OpenVolume(drive);
        int records = 0;
        Enumerate(handle, token, (_, _, _, _) => records++, batchLimit: 1);
        return records;
    }

    [SupportedOSPlatform("windows")]
    private static void Enumerate(SafeFileHandle handle, CancellationToken token, UsnVisitor visitor, int batchLimit = int.MaxValue)
    {
        var input = new byte[24]; var output = new byte[1 << 20];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        ulong previous = 0;
        int batches = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!DeviceIoControl(handle, 0x000900B3, input, input.Length, output, output.Length, out int bytes, 0))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 38) return; // ERROR_HANDLE_EOF 是枚举正常结束。
                throw new Win32Exception(error);
            }
            if (bytes < 8 || bytes > output.Length) throw new InvalidDataException("NTFS 返回无效的枚举缓冲区。");
            ulong next = BinaryPrimitives.ReadUInt64LittleEndian(output);
            if (next <= previous) throw new InvalidDataException("NTFS 枚举游标未推进，已终止，避免死循环。");
            UsnParser.Visit(output.AsSpan(0, bytes), visitor);
            previous = next; BinaryPrimitives.WriteUInt64LittleEndian(input, next);
            if (++batches >= batchLimit) return;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, nint overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? label, int labelSize, out uint serial, out uint maxName, out uint flags, StringBuilder fs, int fsSize);
}
