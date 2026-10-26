using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

public sealed record JournalInfo(ulong JournalId, long FirstUsn, long NextUsn, long LowestValidUsn)
{
    public JournalPosition Position => new(JournalId, NextUsn);
    public void Validate(JournalPosition position)
    {
        if (position.JournalId != JournalId || position.NextUsn < Math.Max(FirstUsn, LowestValidUsn) || position.NextUsn > NextUsn)
            throw new JournalInvalidatedException("USN 日志已更换或被截断，需要重建该卷索引。");
    }
}
public sealed record JournalRecord(ulong FileReference, ulong ParentReference, long Usn, uint Reason, uint Attributes, string Name);
public sealed record JournalBatch(JournalPosition From, JournalPosition To, IReadOnlyList<JournalRecord> Records);
public sealed class JournalInvalidatedException(string message) : IOException(message);

public static class NativeJournal
{
    public static JournalInfo Query(char drive, bool createIfMissing = false)
    {
        using var handle = Open(drive);
        try { return Query(handle); }
        catch (Win32Exception error) when (createIfMissing && error.NativeErrorCode == 1179)
        {
            // 写句柄仅用于已确认缺失日志的创建；现有日志与常规采集保持只读。
            using var writable = Open(drive, 0xc0000000);
            return Query(writable, createIfMissing: true);
        }
    }

    internal static SafeFileHandle Open(char drive, uint access = 0x80000000)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("USN 日志仅支持 Windows。");
        drive = char.ToUpperInvariant(drive);
        if (drive is < 'A' or > 'Z') throw new ArgumentException("无效盘符。", nameof(drive));
        NativePrivileges.EnsureBackupPrivilege();
        var handle = VolumeNative.Open(drive, access);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error(); handle.Dispose();
        throw new Win32Exception(error, $"打开卷{drive}:失败（access=0x{access:X8}）：{new Win32Exception(error).Message}");
    }

    internal static JournalInfo Query(SafeFileHandle handle, bool createIfMissing = false)
    {
        byte[] output = new byte[80];
        if (!VolumeNative.Control(handle, 0x000900f4, null, output, out int bytes))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1179 && createIfMissing)
            {
                // 仅在 ERROR_JOURNAL_NOT_ACTIVE 时创建，绝不修改现有日志参数。
                byte[] request = new byte[16];
                BinaryPrimitives.WriteInt64LittleEndian(request, 128L << 20);
                BinaryPrimitives.WriteInt64LittleEndian(request.AsSpan(8), 32L << 20);
                if (!VolumeNative.Control(handle, 0x000900e7, request, output, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return Query(handle);
            }
            throw new Win32Exception(error, $"FSCTL_QUERY_USN_JOURNAL失败：{new Win32Exception(error).Message}");
        }
        if (bytes < 56) throw new InvalidDataException("USN 日志信息不完整。");
        return new(BinaryPrimitives.ReadUInt64LittleEndian(output), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8)),
            BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(24)));
    }

    public static JournalBatch Read(char drive, JournalPosition from, long? stopBeforeUsn = null, int maxRecords = 8192)
    {
        using var handle = Open(drive);
        return Read(handle, from, stopBeforeUsn, maxRecords);
    }

    internal static JournalBatch Read(SafeFileHandle handle, JournalPosition from, long? stopBeforeUsn = null, int maxRecords = 8192, byte[]? outputBuffer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecords);
        Query(handle).Validate(from);
        if (stopBeforeUsn <= from.NextUsn) return new(from, from, []);
        byte[] output = outputBuffer ?? new byte[1 << 20];
        int bytes = 0;
        bool success = false;
        foreach (int size in new[] { 48, 44, 40 })
        {
            success = VolumeNative.Control(handle, 0x000900bb, BuildReadRequest(from, size), output, out bytes);
            if (success) break;
            int error = Marshal.GetLastWin32Error();
            if (error is 1178 or 1179 or 1181) throw new JournalInvalidatedException("USN 日志读取范围已失效，需要重建该卷。");
            if (error != 87) throw ReadError(error, from, $"requestSize={size}");
        }
        if (!success) throw ReadError(Marshal.GetLastWin32Error(), from, "requestSize=48/44/40均失败");
        if (bytes < 8 || bytes > output.Length) throw new InvalidDataException("USN 返回缓冲区长度无效。");
        long next = BinaryPrimitives.ReadInt64LittleEndian(output);
        if (next < from.NextUsn) throw new InvalidDataException("USN 游标回退。");
        var all = UsnParser.ReadJournal(output.AsSpan(0, bytes));
        var records = new List<JournalRecord>(Math.Min(maxRecords, all.Count));
        foreach (var record in all)
        {
            if (record.Usn < from.NextUsn) throw new InvalidDataException("USN 返回了请求之前的记录。");
            if (record.Usn >= stopBeforeUsn || records.Count == maxRecords) { next = record.Usn; break; }
            records.Add(record);
        }
        if (stopBeforeUsn.HasValue) next = Math.Min(next, stopBeforeUsn.Value);
        return new(from, new(from.JournalId, next), records);
    }

    private static Win32Exception ReadError(int error, JournalPosition from, string detail) =>
        new(error, $"FSCTL_READ_USN_JOURNAL失败，Journal=0x{from.JournalId:X16}，StartUsn={from.NextUsn}，{detail}：{new Win32Exception(error).Message}");

    internal static byte[] BuildReadRequest(JournalPosition from, int size)
    {
        if (size is not (40 or 44 or 48)) throw new ArgumentOutOfRangeException(nameof(size));
        byte[] input = new byte[size];
        BinaryPrimitives.WriteInt64LittleEndian(input, from.NextUsn);
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), from.JournalId);
        if (size >= 44)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 2);
        }
        // V0兼容路径没有版本字段；解析器仍对非V2记录明确失败，禁止静默遗漏。
        return input;
    }

    public static JournalCapture Capture(char drive, string workDirectory, CancellationToken token = default,
        JournalPosition? from = null) => new(drive, workDirectory, token, from);

    public static ulong ReferenceForPath(string path)
    {
        using var handle = NativeFileState.OpenPath(path);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return NativeFileState.Reference(handle);
    }

    public static string CanonicalPathForPath(string path)
    {
        using var handle = NativeFileState.OpenPath(path);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        return NativeFileState.FinalPath(handle) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static IEnumerable<IndexedFileState> EnumerateSubtree(char drive, ulong directoryReference,
        CancellationToken token = default) => NativeSubtree.Enumerate(drive, directoryReference, token);

    public static IndexedFileState GetCurrentFileStateById(char drive, ulong fileReference) => NativeFileState.Read(drive, fileReference);
}
