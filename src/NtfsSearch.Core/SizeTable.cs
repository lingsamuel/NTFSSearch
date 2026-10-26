namespace NtfsSearch.Core;

// 常见大小直接编码；仅超过31位的值进入旁表，未知值占独立的保留码。
internal sealed class SizeTable
{
    private readonly List<long> large = [];

    internal uint Add(long size)
    {
        if (size == -1) return uint.MaxValue;
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        if (size <= int.MaxValue) return (uint)size;
        if (large.Count == int.MaxValue) throw new InvalidOperationException("大文件大小旁表超出索引范围。");
        uint code = 0x80000000U | (uint)large.Count;
        large.Add(size); return code;
    }

    internal long[] Freeze()
    {
        var result = IndexArrays.Copy<long>(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(large));
        large.Clear(); large.Capacity = 0;
        return result;
    }
}
