using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

// OpenFileById的87也可能来自真正的参数错误，必须另取当前MFT身份后才能当作删除。
internal static class MftReferenceProbe
{
    internal static bool ConfirmMissing(SafeFileHandle volume, ulong reference)
    {
        byte[] geometry = new byte[96];
        if (!VolumeNative.Control(volume, 0x00090064, null, geometry, out int geometryBytes))
            throw ProbeError(reference, "FSCTL_GET_NTFS_VOLUME_DATA", Marshal.GetLastWin32Error());
        if (geometryBytes < 96) throw new InvalidDataException("NTFS卷数据不完整，无法确认失效FRN。");
        int recordSize = BinaryPrimitives.ReadInt32LittleEndian(geometry.AsSpan(48));
        if (recordSize < 512 || recordSize > 1 << 20) throw new InvalidDataException("NTFS文件记录大小无效。");
        byte[] input = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(input, reference & 0x0000ffffffffffffUL);
        byte[] output = new byte[checked(recordSize + 16)];
        if (!VolumeNative.Control(volume, 0x00090068, input, output, out int bytes))
            throw ProbeError(reference, "FSCTL_GET_NTFS_FILE_RECORD", Marshal.GetLastWin32Error());
        if (bytes < 0 || bytes > output.Length) throw new InvalidDataException("NTFS文件记录返回长度越界。");
        return ConfirmsMissingReference(output.AsSpan(0, bytes), reference);
    }

    internal static bool ConfirmsMissingReference(ReadOnlySpan<byte> output, ulong reference)
    {
        if (output.Length < 12) throw new InvalidDataException("NTFS文件记录响应头不完整。");
        ulong slot = reference & 0x0000ffffffffffffUL;
        ulong returnedSlot = BinaryPrimitives.ReadUInt64LittleEndian(output) & 0x0000ffffffffffffUL;
        int length = BinaryPrimitives.ReadInt32LittleEndian(output[8..]);
        if (length < 48 || length > output.Length - 12) throw new InvalidDataException("NTFS文件记录响应越界。");
        var record = output.Slice(12, length);
        if (!record[..4].SequenceEqual("FILE"u8)) throw new InvalidDataException("NTFS文件记录签名无效。");
        if (returnedSlot > slot) throw new InvalidDataException("NTFS返回了请求slot之后的记录。");
        // 此FSCTL返回不大于请求slot的第一个in-use记录；回退即说明请求slot已释放。
        if (returnedSlot < slot) return true;
        ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(record[16..]);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(record[22..]);
        ushort links = BinaryPrimitives.ReadUInt16LittleEndian(record[18..]);
        // 这里只读固定头（首个扇区内），不解析属性，也不受USA扇区尾部替换影响。
        return sequence != reference >> 48 || (flags & 1) == 0 || links == 0;
    }

    private static Win32Exception ProbeError(ulong reference, string operation, int error) =>
        new(error, $"OpenFileById返回87，无法进一步确认FRN=0x{reference:X16}：{operation}失败；{new Win32Exception(error).Message}");
}
