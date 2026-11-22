using System.Threading.Channels;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 只在后台逐卷生成统计。卷更新通知不执行汇总，频繁发布也只排一个待处理信号。
internal sealed class DirectorySizeWorker(SearchCatalog catalog,
    Func<(NtfsVolume Volume, IReadOnlyList<SearchIndex> Layers)[]> captureVolumes, Action changed) : IDisposable
{
    private readonly Channel<bool> updates = Channel.CreateBounded<bool>(1);
    private readonly object gate = new();
    private readonly ManualResetEventSlim searchIdle = new(true);
    private (PublishedSearchVolume Volume, CancellationTokenSource Source)? active;
    private volatile bool enabled = true;
    private long revision;

    internal bool Enabled => enabled;

    /// <summary>切换搜索统计并作废旧工作。已发布的普通搜索索引不受此设置影响。</summary>
    internal bool SetEnabled(bool value)
    {
        lock (gate)
        {
            if (enabled == value) return false;
            enabled = value;
            revision++;
            if (!value)
            {
                active?.Source.Cancel();
                foreach (var volume in catalog.PublishedVolumes) catalog.SetDirectorySizes(volume, null);
            }
            updates.Writer.TryWrite(true);
            return true;
        }
    }

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
        lock (gate)
        {
            if (!enabled) return;
            updates.Writer.TryWrite(true);
            if (active is not { } work) return;
            var current = catalog.FindVolume(work.Volume.Id);
            if (current is not { SupportsDirectorySizes: true }
                || work.Volume.Layers[0].Records != current.Layers[0].Records)
                work.Source.Cancel();
        }
    }

    internal async Task RunAsync(CancellationToken token)
    {
        var statistics = new Dictionary<string, DirectorySizeAggregation>(StringComparer.Ordinal);
        long observed = 0;
        try
        {
            await foreach (bool _ in updates.Reader.ReadAllAsync(token))
            {
                long batch;
                lock (gate)
                {
                    batch = revision;
                    if (observed != batch)
                    {
                        statistics.Clear();
                        observed = batch;
                    }
                    if (!enabled) continue;
                }
                var roots = captureVolumes().ToDictionary(view => VolumeCache.Key(view.Volume),
                    view => view.Volume.Root, StringComparer.Ordinal);
                foreach (string id in statistics.Keys.Where(id => !roots.ContainsKey(id)).ToArray())
                    statistics.Remove(id);
                foreach (var published in catalog.PublishedVolumes)
                {
                    token.ThrowIfCancellationRequested();
                    if (!roots.TryGetValue(published.Id, out string? root)) continue;
                    using var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                    PublishedSearchVolume volume;
                    lock (gate)
                    {
                        if (batch != revision) break;
                        if (catalog.FindVolume(published.Id) is not { SupportsDirectorySizes: true, DirectorySizes: null } current)
                            continue;
                        volume = current;
                        active = (volume, source);
                    }
                    try
                    {
                        if (!statistics.TryGetValue(volume.Id, out var aggregation))
                            statistics.Add(volume.Id, aggregation = new(root, YieldToSearch));
                        var orders = aggregation.Update(volume.Layers, source.Token);
                        Publish(volume, orders, batch, source.Token);
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
                        Publish(volume, [], batch, source.Token);
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

    private void Publish(PublishedSearchVolume volume, DirectorySizeOrder[] orders, long batch, CancellationToken token)
    {
        lock (gate)
        {
            // 关闭及快速重新开启都使前一轮结果失效。
            if (batch != revision || token.IsCancellationRequested || !catalog.SetDirectorySizes(volume, orders)) return;
        }
        changed();
    }
}
