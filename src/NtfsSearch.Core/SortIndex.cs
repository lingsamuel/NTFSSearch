namespace NtfsSearch.Core;

internal static class SortIndex
{
    internal static int[] Sort(int[] order, Comparison<int> comparison, CancellationToken token, int[]? scratch = null)
    {
        token.ThrowIfCancellationRequested();
        void SortPart(int start, int length)
        {
            int calls = 0;
            try
            {
                Array.Sort(order, start, length, Comparer<int>.Create((a, b) =>
                {
                    if ((++calls & 4095) == 0) token.ThrowIfCancellationRequested();
                    return comparison(a, b);
                }));
            }
            catch (InvalidOperationException e) when (e.InnerException is OperationCanceledException)
            { throw new OperationCanceledException(token); }
        }
        int parts = (int)Math.Min(Math.Min(Environment.ProcessorCount, 16), Math.Max(1, ((long)order.Length + 999_999) / 1_000_000));
        if (parts <= 1) { SortPart(0, order.Length); token.ThrowIfCancellationRequested(); return order; }
        int length = (int)(((long)order.Length + parts - 1) / parts);
        Parallel.For(0, parts, new ParallelOptions { CancellationToken = token, MaxDegreeOfParallelism = parts }, part =>
        {
            int start = part * length;
            if (start < order.Length) SortPart(start, Math.Min(length, order.Length - start));
        });
        // 排序复用 Array.Sort；归并复用框架优先队列。只增加一份临时 ID 数组，不复制名称或元数据。
        if (scratch is not null && scratch.Length < order.Length) throw new ArgumentException("归并缓冲区过小。", nameof(scratch));
        var merged = scratch ?? new int[order.Length];
        var positions = new int[parts];
        var ends = new int[parts];
        var queue = new PriorityQueue<int, int>(Comparer<int>.Create(comparison));
        for (int part = 0; part < parts; part++)
        {
            positions[part] = part * length; ends[part] = (int)Math.Min((long)(part + 1) * length, order.Length);
            if (positions[part] < ends[part]) queue.Enqueue(part, order[positions[part]]);
        }
        for (int i = 0; i < order.Length; i++)
        {
            if ((i & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!queue.TryPeek(out int part, out int id)) throw new InvalidOperationException("排序归并提前结束。");
            merged[i] = id;
            if (++positions[part] < ends[part]) queue.DequeueEnqueue(part, order[positions[part]]);
            else queue.Dequeue();
        }
        token.ThrowIfCancellationRequested();
        if (scratch is not null)
        {
            merged.AsSpan(0, order.Length).CopyTo(order);
            return order;
        }
        return merged;
    }
}
