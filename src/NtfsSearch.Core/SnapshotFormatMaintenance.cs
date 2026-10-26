using System.Collections.Concurrent;
using System.Threading.Channels;

namespace NtfsSearch.Core;

// 与实时会话共用应用生命周期；排队时不捕获并行加载器的短期CancellationToken。
public sealed class SnapshotFormatMaintenance(VolumeCache cache)
{
    private readonly ConcurrentDictionary<string, byte> scheduled = new(StringComparer.Ordinal);
    private readonly Channel<(NtfsVolume Volume, CachedVolume Cached)> pending =
        Channel.CreateBounded<(NtfsVolume, CachedVolume)>(new BoundedChannelOptions(16)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false });

    public bool Enqueue(NtfsVolume volume, CachedVolume loaded)
    {
        if (!SnapshotFormatUpgrade.IsEligible(loaded)) return false;
        string generation = loaded.Manifest!.SnapshotFile;
        if (!scheduled.TryAdd(generation, 0)) return false;
        if (pending.Writer.TryWrite((volume, loaded))) return true;
        scheduled.TryRemove(generation, out _); return false;
    }

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (var work in pending.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await Task.Run(() => cache.TryUpgradeSnapshot(work.Volume, work.Cached, token),
                        CancellationToken.None).ConfigureAwait(false);
                }
                finally { scheduled.TryRemove(work.Cached.Manifest!.SnapshotFile, out _); }
            }
        }
        finally
        {
            pending.Writer.TryComplete();
            while (pending.Reader.TryRead(out var work)) scheduled.TryRemove(work.Cached.Manifest!.SnapshotFile, out _);
        }
    }
}
