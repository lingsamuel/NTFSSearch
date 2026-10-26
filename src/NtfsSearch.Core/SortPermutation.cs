using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NtfsSearch.Core;

// 仅冻结后的全卷排列使用紧凑表示；排序仍在普通int数组上复用现有算法。
internal sealed class SortPermutation
{
    private readonly byte[]? packed;
    private readonly int[]? values;
    internal int Count { get; }
    internal long StorageBytes => packed?.LongLength ?? (long)Count * sizeof(int);
    internal static bool UsesPackedStorage(int count) => count >= 1024 && count <= 1 << 24;

    private SortPermutation(int count)
    {
        Count = count;
        if (UsesPackedStorage(count)) packed = IndexArrays.Persistent<byte>(checked(count * 3 + 1));
        else values = IndexArrays.Persistent<int>(count);
    }
    private SortPermutation(int[] owned) { Count = owned.Length; values = owned; }

    internal int this[int position]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if (packed is null) return values![position];
            if ((uint)position >= (uint)Count) throw new IndexOutOfRangeException();
            // 多留一个zeroed尾字节，最后一项也可读4字节；托管ref始终由GC追踪，不借出裸指针。
            ref byte first = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(packed), position * 3);
            uint value = Unsafe.ReadUnaligned<uint>(ref first);
            if (!BitConverter.IsLittleEndian) value = BinaryPrimitives.ReverseEndianness(value);
            return (int)(value & 0xFFFFFF);
        }
    }

    // 调用方交出所有权：int32回退直接保留原数组，之后不得再改；packed会复制到终态POH。
    internal static SortPermutation TakeSorted(int[] order, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!UsesPackedStorage(order.Length)) return new(order);
        var result = new SortPermutation(order.Length);
        for (int start = 0; start < order.Length; start += 65536)
        {
            token.ThrowIfCancellationRequested();
            result.Store(start, order.AsSpan(start, Math.Min(65536, order.Length - start)));
        }
        return result;
    }

    internal static SortPermutation Read(Stream stream, int count, byte[] seen, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = new SortPermutation(count);
        var block = new int[Math.Min(65536, count)];
        Array.Clear(seen);
        for (int start = 0; start < count; start += block.Length)
        {
            token.ThrowIfCancellationRequested();
            var ids = block.AsSpan(0, Math.Min(block.Length, count - start));
            stream.ReadExactly(MemoryMarshal.AsBytes(ids));
            foreach (int id in ids)
            {
                // 必须先验证原始int32再编码，否则越界值会被24位截断掩盖。
                if (id < 0 || id >= count || (seen[id / 8] & (1 << (id % 8))) != 0)
                    throw new InvalidDataException("排序缓存包含重复或越界 ID。");
                seen[id / 8] |= (byte)(1 << (id % 8));
            }
            result.Store(start, ids);
        }
        return result;
    }

    private void Store(int start, ReadOnlySpan<int> ids)
    {
        if (packed is null) { ids.CopyTo(values.AsSpan(start)); return; }
        int offset = start * 3;
        foreach (int id in ids)
        {
            packed[offset++] = (byte)id;
            packed[offset++] = (byte)(id >> 8);
            packed[offset++] = (byte)(id >> 16);
        }
    }
}
