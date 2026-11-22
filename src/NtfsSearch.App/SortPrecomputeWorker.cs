using System.Diagnostics;
using System.Threading.Channels;
using NtfsSearch.Core;

// 排序补建按卷顺序执行。通知合并为一个待处理信号，避免增量更新积压批次。
internal sealed class SortPrecomputeWorker(SearchCatalog catalog)
{
    private readonly Channel<bool> updates = Channel.CreateBounded<bool>(1);
    private readonly object gate = new();
    private CancellationTokenSource? active;
    private bool paused;

    internal void SetEnabled(bool enabled)
    {
        lock (gate)
        {
            if (!catalog.SetPrecomputeSort(enabled)) return;
            active?.Cancel();
        }
        Notify();
    }

    internal void Notify()
    {
        if (catalog.PrecomputeSort) updates.Writer.TryWrite(true);
    }

    internal void Pause()
    {
        lock (gate)
        {
            paused = true;
            // 查询可能需要同一个排列，取消旧工作以让出缓存入口。
            active?.Cancel();
        }
    }

    internal void Resume()
    {
        lock (gate) paused = false;
        Notify();
    }

    internal async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (bool _ in updates.Reader.ReadAllAsync(token))
            {
                using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (gate)
                {
                    if (paused || !catalog.PrecomputeSort) continue;
                    active = source;
                }
                try { catalog.WarmSorts(source.Token); }
                catch (OperationCanceledException) when (source.IsCancellationRequested) { }
                catch (Exception error) { Trace.TraceWarning("后台预计算排序失败：{0}", error); }
                finally { lock (gate) active = null; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
