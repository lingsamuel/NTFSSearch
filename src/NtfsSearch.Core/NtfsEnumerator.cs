using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

public sealed record NtfsScanResult(SearchIndex Index, JournalPosition Position, JournalCapture Capture);

internal delegate void NtfsRowVisitor(string parentPath, ReadOnlySpan<char> name,
    long? size, long? modifiedUtcTicks, bool directory, ulong reference, ulong parentReference);

public static class NtfsEnumerator
{
    public static SearchIndex Scan(char drive, CancellationToken cancellationToken = default,
        IProgress<string>? progress = null, Action<long>? unreachableRecords = null, string? excludeDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS 枚举仅支持 Windows。");
        drive = char.ToUpperInvariant(drive);
        if (drive is < 'A' or > 'Z') throw new ArgumentException("请提供有效的盘符。", nameof(drive));
        return ScanWindows(drive, cancellationToken, progress, unreachableRecords,
            excludeDirectory is null ? null : NativeJournal.CanonicalPathForPath(excludeDirectory).TrimEnd('\\'));
    }

    public static NtfsScanResult ScanWithJournal(char drive, string workDirectory,
        CancellationToken cancellationToken = default, IProgress<string>? progress = null,
        Action<long>? unreachableRecords = null, string? excludeDirectory = null)
    {
        var capture = NativeJournal.Capture(drive, workDirectory, cancellationToken);
        try
        {
            var index = Scan(drive, cancellationToken, progress, unreachableRecords, excludeDirectory);
            // 捕获必须继续到调用者完成基线持久化与回放，不能在扫描结束时关闭。
            return new(index, capture.Start, capture);
        }
        catch { capture.Dispose(); throw; }
    }

    [SupportedOSPlatform("windows")]
    private static SearchIndex ScanWindows(char drive, CancellationToken token, IProgress<string>? progress, Action<long>? unreachableRecords, string? excludeDirectory)
    {
        IndexBuilder builder = null!;
        ReadRows(drive, count => builder = new(count),
            (parent, name, size, modified, directory, file, directoryId) => builder.Add(parent, name, size, modified, directory, file, directoryId),
            token, progress, unreachableRecords, excludeDirectory);
        progress?.Report("建立排序索引…");
        return builder.Build(token, progress);
    }

    [SupportedOSPlatform("windows")]
    private static SafeFileHandle OpenVolume(char drive)
    {
        var fs = new StringBuilder(32);
        if (!GetVolumeInformationW($"{drive}:\\", null, 0, out _, out _, out _, fs, fs.Capacity))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!fs.ToString().Equals("NTFS", StringComparison.OrdinalIgnoreCase)) throw new NotSupportedException("只支持 NTFS 卷。");
        NativePrivileges.EnsureBackupPrivilege();
        var handle = VolumeNative.Open(drive, 0x80000000);
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
    internal static void ReadRows(char drive, Action<int> initialize, NtfsRowVisitor add, CancellationToken token,
        IProgress<string>? progress, Action<long>? unreachableRecords, string? excludeDirectory)
    {
        using var handle = OpenVolume(drive);
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
        initialize((int)recordCount);
        long added = 0, skipped = 0;
        progress?.Report("读取MFT元数据并补全硬链接…");
        using var metadata = new MftMetadataReader(drive);
        Enumerate(handle, token, (frn, parent, name, attributes) =>
        {
            if (IsRoot(frn) || name.IsEmpty) return;
            var information = metadata.Read(frn);
            bool directory = (attributes & 16) != 0;
            if (!directory && information?.RequiresLinkEnumeration == true)
            {
                var state = NativeFileState.Read(handle, frn, id => metadata.ReadFileState(drive, id));
                foreach (var link in state.Links)
                {
                    if (NativePathExclusion.Contains(excludeDirectory, link.ParentPath, link.Name)) continue;
                    add(link.ParentPath, link.Name, state.Size, state.ModifiedUtcTicks, false,
                        frn, link.ParentReference);
                }
            }
            else
            {
                string? parentPath = resolver.Resolve(parent);
                if (parentPath is null) { skipped++; return; }
                if (NativePathExclusion.Contains(excludeDirectory, parentPath, name)) return;
                add(parentPath, name, directory ? null : information?.Size,
                information?.ModifiedUtcTicks, directory, frn, parent);
            }
            if ((++added & 262143) == 0) progress?.Report($"已索引 {added:N0} 条记录…");
        });
        unreachableRecords?.Invoke(skipped);
    }

    private static bool IsRoot(ulong frn) => DirectoryPathResolver.IsRoot(frn);

    [SupportedOSPlatform("windows")]
    private static string? DirectoryById(SafeFileHandle volume, ulong id, char drive)
    {
        string? result = NativeFileState.DirectoryPath(volume, id);
        return result is not null && result.StartsWith($"{drive}:\\", StringComparison.OrdinalIgnoreCase) ? result : null;
    }

    public static int ProbeMetadata(char drive, CancellationToken token = default)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NTFS 探测仅支持 Windows。");
        using var handle = OpenVolume(char.ToUpperInvariant(drive));
        using var metadata = new MftMetadataReader(char.ToUpperInvariant(drive));
        int matches = 0;
        Enumerate(handle, token, (frn, _, _, _) =>
        {
            if (metadata.Read(frn) is not null) matches++;
        }, batchLimit: 1);
        return matches;
    }

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
            if (!VolumeNative.Control(handle, 0x000900B3, input, output, out int bytes))
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 38) return; // ERROR_HANDLE_EOF 是枚举正常结束。
                throw new Win32Exception(error, $"FSCTL_ENUM_USN_DATA失败，StartFRN=0x{previous:X16}：{new Win32Exception(error).Message}");
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
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(string root, StringBuilder? label, int labelSize, out uint serial, out uint maxName, out uint flags, StringBuilder fs, int fsSize);
}
