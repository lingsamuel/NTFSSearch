using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

// 将长期大数组与构建期散列表的LOH分配分开；小数组仍参与普通分代回收。
internal static class IndexArrays
{
    private const int LargeArrayBytes = 85_000;
    internal static T[] Persistent<T>(int length) where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return (long)length * Unsafe.SizeOf<T>() < LargeArrayBytes
            ? new T[length] : GC.AllocateArray<T>(length, pinned: true);
    }

    internal static T[] Copy<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        var result = Persistent<T>(values.Length);
        values.CopyTo(result); return result;
    }

    internal static T[] Freeze<T>(T[] values, int count) where T : unmanaged
    {
        if (count == values.Length && (long)count * Unsafe.SizeOf<T>() < LargeArrayBytes) return values;
        return Copy<T>(values.AsSpan(0, count));
    }
}
