using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

internal static class NativeFileState
{
    internal static IndexedFileState Read(char drive, ulong reference)
    {
        using var volume = NativeJournal.Open(drive);
        return Read(volume, reference, id => RawMftState.Read(drive, id));
    }

    internal static IndexedFileState Read(SafeFileHandle volume, ulong reference, Func<ulong, IndexedFileState>? rawFallback = null)
    {
        string phase = "OpenById";
        try { return ReadCore(volume, reference, ref phase); }
        catch (Win32Exception error)
        {
            if (rawFallback is not null && SupportsRawFallback(phase, error.NativeErrorCode))
            {
                try { return rawFallback(reference); }
                catch (Exception rawError)
                {
                    throw new IOException($"NativeFileState/{phase}，FRN=0x{reference:X16}（{error.Message}）；完整rawMFT fallback失败：{rawError.Message}", rawError);
                }
            }
            throw new Win32Exception(error.NativeErrorCode, $"NativeFileState/{phase}，FRN=0x{reference:X16}：{error.Message}");
        }
        catch (IOException error)
        {
            throw new IOException($"NativeFileState/{phase}，FRN=0x{reference:X16}：{error.Message}", error);
        }
    }

    internal static bool SupportsRawFallback(string phase, int error) =>
        phase.StartsWith("ReadLinks/class46", StringComparison.Ordinal) && error is 87 or 50
        || (phase == "OpenById" || phase.StartsWith("ParentPath", StringComparison.Ordinal)
            || phase == "DirectoryParentIdentity") && error is 5 or 32 or 33;

    private static IndexedFileState ReadCore(SafeFileHandle volume, ulong reference, ref string phase)
    {
        using var file = OpenById(volume, reference);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 1168) return new(reference, false, []);
            if (error == 87 && MftReferenceProbe.ConfirmMissing(volume, reference)) return new(reference, false, []);
            throw new Win32Exception(error, $"OpenFileById失败：FRN=0x{reference:X16}，slot={reference & 0x0000ffffffffffffUL}，代际={reference >> 48}；{new Win32Exception(error).Message}");
        }
        phase = "GetInfo";
        if (!GetFileInformationByHandle(file, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        ulong actual = ((ulong)info.IndexHigh << 32) | info.IndexLow;
        if (actual != reference) throw new InvalidDataException("按ID打开的对象身份不匹配。");
        bool directory = (info.Attributes & 16) != 0;
        if (info.Links == 0) return new(reference, directory, []);
        IReadOnlyList<(ulong ParentReference, string Name)> links;
        if (directory)
        {
            phase = "DirectoryFinalPath";
            string? path = FinalPath(file);
            if (path is null) throw new IOException("无法解析目录路径。");
            string trimmed = path.TrimEnd('\\');
            int separator = trimmed.LastIndexOf('\\');
            if (separator < 0) return new(reference, true, []); // 卷根不作为结果行。
            phase = "DirectoryParentIdentity";
            using var parent = OpenPath(trimmed[..(separator + 1)]);
            if (parent.IsInvalid || !GetFileInformationByHandle(parent, out var parentInfo)) throw new Win32Exception(Marshal.GetLastWin32Error());
            links = [((((ulong)parentInfo.IndexHigh << 32) | parentInfo.IndexLow), trimmed[(separator + 1)..])];
        }
        else
        {
            phase = "ReadLinks/class46";
            try { links = ReadLinks(file); }
            catch (Win32Exception error) when (error.NativeErrorCode == 87)
            {
                string? diagnosticPath = null;
                try { diagnosticPath = FinalPath(file); } catch (IOException) { }
                phase = $"ReadLinks/class46(path={diagnosticPath ?? "不可解析"})";
                throw;
            }
        }
        var result = new List<IndexedLink>(links.Count);
        foreach (var link in links)
        {
            phase = $"ParentPath(parent=0x{link.ParentReference:X16})";
            string? path = DirectoryPath(volume, link.ParentReference);
            if (path is null) throw new IOException("无法解析硬链接父目录，稍后应重试该事务。");
            result.Add(new(link.ParentReference, path, link.Name));
        }
        phase = "Metadata";
        long? modified = MftMetadata.UtcTicksFromFileTime(((long)info.Write.High << 32) | info.Write.Low);
        long? created = MftMetadata.UtcTicksFromFileTime(((long)info.Creation.High << 32) | info.Creation.Low);
        return new(reference, directory, result, directory ? null : checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow)),
            modified, created);
    }

    internal static SafeFileHandle OpenById(SafeFileHandle volume, ulong id)
    {
        var descriptor = new FileIdDescriptor { Size = 24, FileId = id };
        return OpenFileById(volume, ref descriptor, 0, 7, 0, 0x02200000);
    }
    internal static string? DirectoryPath(SafeFileHandle volume, ulong id)
    {
        using var handle = OpenById(volume, id);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is 2 or 3 or 1168) return null;
            throw new Win32Exception(error, $"ParentPath/OpenById失败，父FRN=0x{id:X16}：{new Win32Exception(error).Message}");
        }
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if ((info.Attributes & 16) == 0) return null;
        string? path = FinalPath(handle);
        return path is null ? null : path.EndsWith('\\') ? path : path + '\\';
    }
    internal static string? FinalPath(SafeFileHandle handle)
    {
        string? value = FullPath(handle, 0);
        if (value is null) return null;
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + value[8..];
        return value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
    }

    internal static string VolumeIdentity(SafeFileHandle handle)
    {
        string path = FullPath(handle, 1) ?? throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!path.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase)) throw new IOException("无法获取本地卷GUID身份。");
        int end = path.IndexOf('\\', 4);
        if (end < 0) throw new InvalidDataException("原生卷GUID路径格式无效。");
        return path[..(end + 1)];
    }

    private static string? FullPath(SafeFileHandle handle, uint flags)
    {
        var path = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, flags);
        if (length >= path.Capacity)
        {
            if (length > 32768) throw new IOException("目录路径超过Windows上限。");
            path = new((int)length + 1); length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, flags);
        }
        if (length == 0 || length >= path.Capacity) return null;
        return path.ToString();
    }
    internal static (ulong Reference, uint Serial, uint Attributes) Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (((ulong)info.IndexHigh << 32) | info.IndexLow, info.Serial, info.Attributes);
    }
    internal static ulong Reference(SafeFileHandle handle) => Identity(handle).Reference;
    internal static uint Serial(SafeFileHandle handle) => Identity(handle).Serial;

    internal static SafeFileHandle OpenPath(string path, bool followReparse = true)
    {
        string full = Path.GetFullPath(path);
        string extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        return CreateFileW(extended, 0, 7, 0, 3, 0x02000000 | (followReparse ? 0U : 0x00200000), 0);
    }

    internal static IReadOnlyList<(ulong ParentReference, string Name)> ReadLinks(SafeFileHandle handle)
    {
        byte[] buffer = new byte[4096];
        while (true)
        {
            int status = NtQueryInformationFile(handle, out var io, buffer, buffer.Length, 46);
            if (status == 0)
            {
                if ((ulong)io.Information > (ulong)buffer.Length) throw new InvalidDataException("硬链接返回长度越界。");
                return ParseLinks(buffer.AsSpan(0, (int)io.Information));
            }
            if (status is not (unchecked((int)0x80000005) or unchecked((int)0xc0000004) or unchecked((int)0xc0000023)))
                throw new Win32Exception((int)RtlNtStatusToDosError(status));
            uint needed = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
            long capacity = Math.Max((long)needed, (long)buffer.Length * 2);
            if (capacity > 64 << 20) throw new InvalidDataException("硬链接列表超过安全上限。");
            buffer = new byte[(int)capacity];
        }
    }

    internal static IReadOnlyList<(ulong ParentReference, string Name)> ParseLinks(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8) throw new InvalidDataException("硬链接信息头不完整。");
        uint needed = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]);
        if (needed < 8 || needed > buffer.Length || count > (buffer.Length - 8) / 20) throw new InvalidDataException("硬链接信息不完整。");
        var links = new List<(ulong, string)>((int)count);
        int offset = 8;
        for (int i = 0; i < count; i++)
        {
            if (buffer.Length - offset < 20) throw new InvalidDataException("硬链接条目头不完整。");
            var entry = buffer[offset..];
            uint next = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint characters = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            if (characters == 0 || characters > (entry.Length - 20) / 2) throw new InvalidDataException("硬链接名称越界。");
            int bytes = (int)characters * 2;
            if (next != 0 && (next < 20 + bytes || (next & 7) != 0 || next > entry.Length)) throw new InvalidDataException("硬链接条目偏移无效。");
            links.Add((BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]), MemoryMarshal.Cast<byte, char>(entry.Slice(20, bytes)).ToString()));
            if (i == count - 1) { if (next != 0) throw new InvalidDataException("硬链接列表尾部无效。"); }
            else if (next == 0) throw new InvalidDataException("硬链接列表提前终止。");
            offset += (int)next;
        }
        return links;
    }

    [StructLayout(LayoutKind.Sequential)] private struct FileIdDescriptor { public uint Size, Type; public ulong FileId, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeTime { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInformation
    {
        public uint Attributes; public NativeTime Creation, Access, Write;
        public uint Serial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoStatus { public nint Status; public nuint Information; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor id, uint access, uint share, nint security, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint capacity, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, nint security, uint creation, uint flags, nint template);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationFile(SafeFileHandle handle, out IoStatus io, [Out] byte[] buffer, int length, int informationClass);
    [DllImport("ntdll.dll")] private static extern uint RtlNtStatusToDosError(int status);
}
