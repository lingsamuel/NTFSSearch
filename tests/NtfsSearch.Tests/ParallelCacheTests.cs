using System.Buffers.Binary;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class ParallelCacheTests
{
    [Fact]
    public async Task FailedRepairRequestDoesNotCancelHealthyCacheLoads()
    {
        var catalog = new SearchCatalog();
        var loader = new AutomaticVolumeLoader(new RepairFailureBackend(), catalog, 2);
        await loader.RefreshAsync();
        Assert.Equal(1, catalog.Count);
        Assert.Contains(loader.Statuses, s => s.Root == @"C:\" && s.Message.Contains("无法写入重建请求"));
    }

    private sealed class RepairFailureBackend : IAutomaticVolumeBackend
    {
        public IReadOnlyList<NtfsVolume> Discover() => [new("bad", @"C:\", 100, ""), new("good", @"D:\", 100, "")];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public void RequestRebuild(NtfsVolume volume) => throw new IOException("Cache disk full");
        public async Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token)
        {
            if (volume.Id == "bad") throw new InvalidDataException("bad cache");
            await Task.Delay(25, token);
            var builder = new IndexBuilder(); builder.Add(volume.Root, "real.txt"); return new(builder.Build(), false);
        }
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "等待后台采集";
    }
    [Fact]
    public async Task SlowCacheDoesNotDelayPublishingAnotherDisk()
    {
        var backend = new Backend();
        var catalog = new SearchCatalog();
        catalog.Changed += () => backend.Published.TrySetResult();
        var loader = new AutomaticVolumeLoader(backend, catalog, 2);
        Task loading = loader.RefreshAsync();
        try
        {
            await backend.FastFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await backend.Published.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, catalog.Count);
            Assert.False(loading.IsCompleted);
            Assert.Equal(2, backend.Peak);
        }
        finally { backend.Release.TrySetResult(); await loading; }
        Assert.Equal(2, catalog.Count);
    }

    [Fact]
    public void PhysicalDiskExtentParserHandlesSpanningAndTruncatedBuffers()
    {
        var buffer = new byte[80];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(56), 7);
        Assert.Equal(new[] { "disk:2", "disk:7" }, VolumeResources.ParsePhysicalDisks(buffer));
        Assert.Throws<InvalidDataException>(() => VolumeResources.ParsePhysicalDisks(buffer[..^1]));
    }

    [Fact]
    public void ResourcePolicyReservesCpuAndBudgetsFromAvailableMemory()
    {
        var policy = IndexingConcurrencyPolicy.Create(32, 30L << 30);
        Assert.Equal(4, policy.ScanLimit); Assert.Equal(3, policy.CacheLoadLimit);
        Assert.Equal(6, policy.WorkerProcessors); Assert.Equal(15L << 30, policy.MemoryBudget);
    }

    private sealed class Backend : IAutomaticVolumeBackend
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FastFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int active;
        private readonly object peakGate = new();
        private TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Peak;
        public IReadOnlyList<NtfsVolume> Discover() => [new("slow", @"C:\", 100, ""), new("fast", @"D:\", 100, "")];
        public string CacheRevision(NtfsVolume volume) => "v1";
        public async Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token)
        {
            int count = Interlocked.Increment(ref active); lock (peakGate) Peak = Math.Max(Peak, count);
            try
            {
                if (volume.Id == "slow") { SlowStarted.TrySetResult(); await Release.Task.WaitAsync(token); }
                else { await SlowStarted.Task.WaitAsync(token); FastFinished.TrySetResult(); }
                var builder = new IndexBuilder(); builder.Add(volume.Root, "real.txt");
                var index = builder.Build();
                return new(index, false);
            }
            finally { Interlocked.Decrement(ref active); }
        }
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume volume) => "等待后台采集";
    }

}
