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
        IProgress<string>? progress = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS 枚举仅支持 Windows。");
        drive = char.ToUpperInvariant(drive);
        if (drive is < 'A' or > 'Z') throw new ArgumentException("请提供有效的盘符。", nameof(drive));
        return ScanWindows(drive, cancellationToken, progress);
    }

    [SupportedOSPlatform("windows")]
    private static SearchIndex ScanWindows(char drive, CancellationToken token, IProgress<string>? progress)
    {
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationW($"{drive}:\\", null, 0, out _, out _, out _, fs, fs.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("只支持 NTFS 卷。");
        using var handle = CreateFileW($"\\\\.\\{drive}:", 0x80000000, 7, 0, 3, 0, 0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5) throw new UnauthorizedAccessException("读取 NTFS 卷需要管理员权限。请用管理员终端运行 scan；查询已保存的索引不需要管理员权限。");
            throw new Win32Exception(error);
        }
        var directories = new Dictionary<ulong, (ulong Parent, string Name)>();
        long recordCount = 0;
        progress?.Report("枚举目录关系（第 1 遍）…");
        Enumerate(handle, token, (frn, parent, name, attributes) =>
        {
            recordCount++;
            if ((attributes & 16) != 0 && !IsRoot(frn)) directories[frn] = (parent, name.ToString());
            if ((recordCount & 262143) == 0) progress?.Report($"已读取 {recordCount:N0} 条记录…");
        });
        if (recordCount > int.MaxValue) throw new NotSupportedException("原型单索引上限为 21 亿目录项。");
        var paths = new Dictionary<ulong, string>();
        string Root() => $"{drive}:\\";
        string ParentPath(ulong frn)
        {
            if (IsRoot(frn)) return Root();
            if (paths.TryGetValue(frn, out string? cached)) return cached;
            var chain = new List<ulong>();
            var visited = new HashSet<ulong>();
            ulong current = frn;
            while (!IsRoot(current) && !paths.ContainsKey(current))
            {
                if (!visited.Add(current)) throw new InvalidDataException("目录关系包含循环，扫描未发布。");
                if (!directories.TryGetValue(current, out var directory))
                    throw new InvalidDataException("目录在扫描期间发生变化，无法生成完整路径。请重试。");
                chain.Add(current); current = directory.Parent;
                if (chain.Count > 16384) throw new InvalidDataException("目录层级超出 Windows 路径范围。");
            }
            string path = IsRoot(current) ? Root() : paths[current];
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                path += directories[chain[i]].Name + '\\'; paths[chain[i]] = path;
            }
            return paths[frn];
        }
        progress?.Report("建立名称索引（第 2 遍）…");
        var builder = new IndexBuilder((int)recordCount);
        long added = 0;
        Enumerate(handle, token, (frn, parent, name, attributes) =>
        {
            if (IsRoot(frn) || name.IsEmpty) return;
            builder.Add(ParentPath(parent), name, isDirectory: (attributes & 16) != 0);
            if ((++added & 262143) == 0) progress?.Report($"已索引 {added:N0} 条记录…");
        });
        progress?.Report("建立排序索引…");
        return builder.Build(token, progress);
    }

    private static bool IsRoot(ulong frn) => (frn & 0x0000FFFFFFFFFFFFUL) == 5;

    [SupportedOSPlatform("windows")]
    private static void Enumerate(SafeFileHandle handle, CancellationToken token, UsnVisitor visitor)
    {
        var input = new byte[24]; var output = new byte[1 << 20];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(16), long.MaxValue);
        ulong previous = 0;
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
