using System.Buffers;

namespace NtfsSearch.Core;

// 一次匹配的位图在整个排序/归并期间保留，避免每翻一批结果就重新扫描全卷。
internal sealed class PreparedSearch(SearchIndex index, ulong[]? bits, long total) : IDisposable
{
    private ulong[]? ownedBits = bits;
    private bool disposed;
    public long TotalCount { get; } = total;
    public IEnumerable<int> Ids(SortField sort, bool descending, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return index.EnumerateMatches(ownedBits, TotalCount, sort, descending, token);
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (ownedBits is not null) ArrayPool<ulong>.Shared.Return(ownedBits);
        ownedBits = null;
    }
}
