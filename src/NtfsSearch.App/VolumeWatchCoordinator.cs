using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class VolumeWatchCoordinator(string root, CancellationToken lifetime) : IAsyncDisposable
{
    private readonly Dictionary<string, Watch> watches = new(StringComparer.Ordinal);
    public async Task EnsureAsync(NtfsVolume volume, VolumeManifest manifest)
    {
        string key = VolumeCache.Key(volume);
        if (watches.TryGetValue(key, out var current) && current.Generation == manifest.SnapshotFile) return;
        if (current is not null)
        {
            await current.Stop.CancelAsync(); await current.Task; current.Stop.Dispose();
            try { new VolumeCache(root).CleanupRetired(volume, lifetime); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Task task = Task.Run(() => VolumeJournalService.WatchAsync(root, volume, manifest, stop.Token));
        watches[key] = new(manifest.SnapshotFile, stop, task);
    }
    public async Task RemoveExceptAsync(IEnumerable<NtfsVolume> volumes)
    {
        var keep = volumes.Select(VolumeCache.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string key in watches.Keys.Except(keep).ToArray())
        {
            var watch = watches[key]; watches.Remove(key); await watch.Stop.CancelAsync(); await watch.Task; watch.Stop.Dispose();
        }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var watch in watches.Values) await watch.Stop.CancelAsync();
        await Task.WhenAll(watches.Values.Select(w => w.Task));
        foreach (var watch in watches.Values) watch.Stop.Dispose();
    }
    private sealed record Watch(string Generation, CancellationTokenSource Stop, Task Task);
}
