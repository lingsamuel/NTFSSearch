using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class LiveIndexFeedTests
{
    [Fact]
    public async Task PublicationActivatesFeedWithoutLosingChangesDuringAttachHandoff()
    {
        string directory = Path.Combine(Path.GetTempPath(), "feed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new JournalPosition(1, 0);
            var builder = new IndexBuilder(); builder.Add(@"C:\", "base", fileReference: 10, parentReference: 5);
            var baseline = builder.Build(); var volume = new NtfsVolume("test", @"C:\", 100, "");
            var manifest = new VolumeManifest(volume.Id, volume.Root, "base.ntidx", DateTime.UtcNow, initial);
            using var writer = new ChangeLogStore(directory, manifest.SnapshotFile, initial, true);
            var feed = new LiveIndexFeed(directory); var catalog = new SearchCatalog();
            var prepared = await feed.AttachAsync(volume, new(baseline, false, manifest), default);
            int updates = 0;
            var delivery = new TaskCompletionSource();
            feed.Updated += update =>
            {
                Assert.True(catalog.PublishLiveLayers(update.VolumeKey, update.Generation, update.Layers));
                Interlocked.Increment(ref updates); delivery.TrySetResult();
            };
            using var stop = new CancellationTokenSource(); Task pump = feed.RunAsync(stop.Token);
            try
            {
                writer.Append(new(initial, new(1, 1), [new(11, false, [new(5, @"C:\", "new")])]));
                await Task.Delay(350);
                Assert.Equal(0, Volatile.Read(ref updates));
                catalog.PublishLayers(VolumeCache.Key(volume), prepared, manifest.SnapshotFile);
                Assert.True(feed.Activate(volume, manifest.SnapshotFile));
                await delivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(2, catalog.Count);

                // 同一代重新准备时也暂停旧session，旧层发布后再激活，不能倒退到旧计数。
                prepared = await feed.AttachAsync(volume, new(baseline, false, manifest), default);
                delivery = new TaskCompletionSource();
                writer.Append(new(new(1, 1), new(1, 2), [new(12, false, [new(5, @"C:\", "later")])]));
                await Task.Delay(350);
                Assert.Equal(1, Volatile.Read(ref updates));
                Assert.Equal(2, catalog.Count);
                catalog.PublishLayers(VolumeCache.Key(volume), prepared, manifest.SnapshotFile);
                Assert.True(feed.Activate(volume, manifest.SnapshotFile));
                await delivery.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(3, catalog.Count);
                Assert.False(feed.Activate(volume, "stale-generation"));
            }
            finally { stop.Cancel(); try { await pump; } catch (OperationCanceledException) { } }
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task ReplacingAndRemovingSessionsReleasesGenerationLeases()
    {
        string directory = Path.Combine(Path.GetTempPath(), "feed-leases-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new JournalPosition(1, 0); var baseline = new IndexBuilder().Build();
            var volume = new NtfsVolume("test", @"C:\", 100, "");
            foreach (string generation in new[] { "old.ntidx", "new.ntidx" })
                using (var writer = new ChangeLogStore(directory, generation, initial, true)) { }
            var feed = new LiveIndexFeed(directory);
            await feed.AttachAsync(volume, new(baseline, false, new(volume.Id, volume.Root, "old.ntidx", DateTime.UtcNow, initial)), default);
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(directory, "old.ntidx.generation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            await feed.AttachAsync(volume, new(baseline, false, new(volume.Id, volume.Root, "new.ntidx", DateTime.UtcNow, initial)), default);
            using (var old = new FileStream(Path.Combine(directory, "old.ntidx.generation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            feed.RemoveExcept([]);
            using (var current = new FileStream(Path.Combine(directory, "new.ntidx.generation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LoaderCallsActivationAfterPublishingGeneration()
    {
        var catalog = new SearchCatalog(); var backend = new PublishingBackend(catalog);
        await new AutomaticVolumeLoader(backend, catalog).RefreshAsync();
        Assert.Equal(1, backend.Published);
    }

    [Fact]
    public async Task CheckpointSharingFailureKeepsHealthyFeedAndRetriesWithoutRebuild()
    {
        string directory = Path.Combine(Path.GetTempPath(), "feed-checkpoint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new JournalPosition(1, 0); var baseline = new IndexBuilder().Build();
            var volume = new NtfsVolume("test", @"C:\", 100, "");
            var manifest = new VolumeManifest(volume.Id, volume.Root, "base.ntidx", DateTime.UtcNow, initial);
            using var writer = new ChangeLogStore(directory, manifest.SnapshotFile, initial, true);
            string checkpoint = Path.Combine(directory, manifest.SnapshotFile + ".live-" + writer.LogId + ".state");
            Directory.CreateDirectory(checkpoint); // 目录占用目标路径，模拟可跨平台复现的检查点写入失败。
            var feed = new LiveIndexFeed(directory); var catalog = new SearchCatalog();
            var layers = await feed.AttachAsync(volume, new(baseline, false, manifest), default);
            var session = SessionFor(feed);
            session.GetType().GetField("SavedUtc")!.SetValue(session, DateTime.UtcNow.AddMinutes(-6));
            int failures = 0; var updated = new TaskCompletionSource();
            feed.Failed += (_, _) => Interlocked.Increment(ref failures);
            feed.Updated += update => { catalog.PublishLiveLayers(update.VolumeKey, update.Generation, update.Layers); updated.TrySetResult(); };
            catalog.PublishLayers(VolumeCache.Key(volume), layers, manifest.SnapshotFile); feed.Activate(volume, manifest.SnapshotFile);
            writer.Append(new(initial, new(1, 1), [new(10, false, [new(5, @"C:\", "first")])]));
            using var stop = new CancellationTokenSource(); Task pump = feed.RunAsync(stop.Token);
            try
            {
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(350);
                Assert.Equal(0, Volatile.Read(ref failures));
                Assert.Equal(1, feed.ActiveVolumeCount);
                Directory.Delete(checkpoint);
                session.GetType().GetField("NextCheckpointAttemptUtc")!.SetValue(session, DateTime.MinValue);
                updated = new TaskCompletionSource();
                writer.Append(new(new(1, 1), new(1, 2), [new(11, false, [new(5, @"C:\", "second")])]));
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Task.Delay(350);
                Assert.Equal(2, catalog.Count); Assert.Equal(0, Volatile.Read(ref failures));
                Assert.True(File.Exists(checkpoint));
            }
            finally { stop.Cancel(); try { await pump; } catch (OperationCanceledException) { } }
        }
        finally { Directory.Delete(directory, true); }
    }


    [Fact]
    public async Task TemporaryWalOpenFailureRetainsSessionAndRecoversAfterPathReturns()
    {
        string directory = Path.Combine(Path.GetTempPath(), "feed-io-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var initial = new JournalPosition(1, 0); var baseline = new IndexBuilder().Build();
            var volume = new NtfsVolume("test", @"C:\", 100, "");
            var manifest = new VolumeManifest(volume.Id, volume.Root, "base.ntidx", DateTime.UtcNow, initial);
            using (var writer = new ChangeLogStore(directory, manifest.SnapshotFile, initial, true)) { }
            var feed = new LiveIndexFeed(directory); var catalog = new SearchCatalog();
            var layers = await feed.AttachAsync(volume, new(baseline, false, manifest), default);
            int failures = 0; var updated = new TaskCompletionSource();
            feed.Failed += (_, _) => Interlocked.Increment(ref failures);
            feed.Updated += update => { catalog.PublishLiveLayers(update.VolumeKey, update.Generation, update.Layers); updated.TrySetResult(); };
            catalog.PublishLayers(VolumeCache.Key(volume), layers, manifest.SnapshotFile); feed.Activate(volume, manifest.SnapshotFile);
            string changes = Path.Combine(directory, "base.ntidx.changes");
            Directory.Move(changes, changes + ".unavailable");
            using var stop = new CancellationTokenSource(); Task pump = feed.RunAsync(stop.Token);
            try
            {
                await Task.Delay(400);
                Assert.Equal(0, Volatile.Read(ref failures)); Assert.Equal(1, feed.ActiveVolumeCount);
                Directory.Move(changes + ".unavailable", changes);
                using (var writer = new ChangeLogStore(directory, manifest.SnapshotFile, initial, true))
                    writer.Append(new(initial, new(1, 1), [new(10, false, [new(5, @"C:\", "recovered")])]));
                await updated.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, catalog.Count); Assert.Equal(0, Volatile.Read(ref failures));
            }
            finally { stop.Cancel(); try { await pump; } catch (OperationCanceledException) { } }
        }
        finally { Directory.Delete(directory, true); }
    }

    private static object SessionFor(LiveIndexFeed feed)
    {
        var sessions = (System.Collections.IDictionary)typeof(LiveIndexFeed).GetField("sessions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(feed)!;
        return sessions.Values.Cast<object>().Single();
    }

    private sealed class PublishingBackend(SearchCatalog catalog) : IAutomaticVolumeBackend
    {
        private readonly NtfsVolume volume = new("test", @"C:\", 100, "");
        private readonly SearchIndex index = new IndexBuilder().Build();
        public int Published;
        public IReadOnlyList<NtfsVolume> Discover() => [volume];
        public string CacheRevision(NtfsVolume _) => "1";
        public Task<CachedVolume?> LoadAsync(NtfsVolume _, CancellationToken token) => Task.FromResult<CachedVolume?>(
            new(index, false, new(volume.Id, volume.Root, "generation", DateTime.UtcNow)));
        public Task EnsureCollectorAsync(CancellationToken token) => Task.CompletedTask;
        public string ReadStatus(NtfsVolume _) => "ready";
        public void OnPublished(NtfsVolume current, string? generation)
        {
            Assert.Equal("generation", generation);
            Assert.True(catalog.PublishLiveLayers(VolumeCache.Key(current), generation!, [index]));
            Published++;
        }
    }

}
