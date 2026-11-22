using System.Threading.Channels;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 只在后台逐卷生成统计。卷更新通知不执行汇总，频繁发布也只排一个待处理信号。
internal sealed class DirectorySizeWorker(SearchHost host, Action changed) : IDisposable
{
    private readonly Channel<bool> updates = Channel.CreateBounded<bool>(1);
    private readonly object gate = new();
    private readonly ManualResetEventSlim searchIdle = new(true);
    private (PublishedSearchVolume Volume, CancellationTokenSource Source)? active;

    internal void Pause() => searchIdle.Reset();
    internal void Resume() => searchIdle.Set();
    public void Dispose() => searchIdle.Dispose();

    // 在汇总批次的检查点让出执行权，保留已经累积的数据。
    private void YieldToSearch(CancellationToken token)
    {
        if (!searchIdle.IsSet) searchIdle.Wait(token);
    }

    internal void Notify()
    {
        updates.Writer.TryWrite(true);
        lock (gate)
        {
            if (active is not { } work) return;
            var current = host.Catalog.FindVolume(work.Volume.Id);
            if (current is not { SupportsDirectorySizes: true }
                || work.Volume.Layers[0].Records != current.Layers[0].Records)
                work.Source.Cancel();
        }
    }

    internal async Task RunAsync(CancellationToken token)
    {
        var statistics = new Dictionary<string, DirectorySizeAggregation>(StringComparer.Ordinal);
        try
        {
            await foreach (bool _ in updates.Reader.ReadAllAsync(token))
            {
                var roots = host.CaptureAnalysisVolumes().ToDictionary(view => VolumeCache.Key(view.Volume),
                    view => view.Volume.Root, StringComparer.Ordinal);
                foreach (string id in statistics.Keys.Where(id => !roots.ContainsKey(id)).ToArray())
                    statistics.Remove(id);
                foreach (var published in host.Catalog.PublishedVolumes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!roots.TryGetValue(published.Id, out string? root)) continue;
                    using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                    PublishedSearchVolume volume;
                    lock (gate)
                    {
                        if (host.Catalog.FindVolume(published.Id) is not { SupportsDirectorySizes: true, DirectorySizes: null } current)
                            continue;
                        volume = current;
                        active = (volume, source);
                    }
                    try
                    {
                        if (!statistics.TryGetValue(volume.Id, out var aggregation))
                            statistics.Add(volume.Id, aggregation = new(root, YieldToSearch));
                        var orders = aggregation.Update(volume.Layers, source.Token);
                        if (host.Catalog.FindVolume(volume.Id) == volume && !source.IsCancellationRequested)
                        {
                            volume.DirectorySizes = orders;
                            changed();
                        }
                    }
                    catch (OperationCanceledException) when (source.IsCancellationRequested)
                    {
                        // 更新会原地修改工作副本，取消后从新基线恢复。
                        statistics.Remove(volume.Id);
                    }
                    catch (Exception error)
                    {
                        statistics.Remove(volume.Id);
                        System.Diagnostics.Trace.TraceWarning("目录大小统计失败 ({0}): {1}", root, error);
                        if (host.Catalog.FindVolume(volume.Id) == volume)
                        {
                            volume.DirectorySizes = [];
                            changed();
                        }
                    }
                    finally
                    {
                        lock (gate) active = null;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
