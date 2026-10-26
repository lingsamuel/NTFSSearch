using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class SnapshotFormatUpgradeTests
{
    private static readonly NtfsVolume Volume = new("format-upgrade-test", @"C:\", 100, "");
    private static readonly JournalPosition Initial = new(1, 0);

    private static SearchIndex Baseline()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "directory", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\directory\", "file.txt", 42, 123, fileReference: 20, parentReference: 10);
        return builder.Build();
    }

    [Fact]
    public void SameGenerationSnapshotBackupKeepsCurrentManifestAndJournal()
    {
        string directory = Temporary();
        try
        {
            var cache = new VolumeCache(directory);
            using (var publication = cache.BeginPublication(Volume, Baseline(), Initial)) publication.Commit();
            var current = cache.CurrentManifest(Volume)!;
            string path = Path.Combine(directory, current.SnapshotFile);
            File.Copy(path, path + ".bak");
            File.WriteAllText(path, "broken primary encoding");
            var loaded = cache.Load(Volume)!;
            Assert.False(loaded.UsedBackup);
            Assert.Equal(current, loaded.Manifest);
            Assert.Equal(Initial, loaded.Manifest!.Journal);
            Assert.Equal(2, loaded.Index.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void RetiredGenerationRemovesItsFormatBackupAndKeepsCurrentAndPrevious()
    {
        string directory = Temporary();
        try
        {
            var cache = new VolumeCache(directory);
            var a = cache.Publish(Volume, Baseline()); string first = Path.Combine(directory, a.SnapshotFile);
            File.Copy(first, first + ".bak");
            var b = cache.Publish(Volume, Baseline()); string previous = Path.Combine(directory, b.SnapshotFile);
            File.Copy(previous, previous + ".bak");
            var c = cache.Publish(Volume, Baseline()); string current = Path.Combine(directory, c.SnapshotFile);
            File.Copy(current, current + ".bak");
            cache.CleanupRetired(Volume);
            Assert.False(File.Exists(first + ".bak"));
            Assert.True(File.Exists(previous + ".bak"));
            Assert.True(File.Exists(current + ".bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UpgradeUsesRawBaselineAndKeepsManifestRevisionJournalAndLegacyBytes()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            byte[] original = File.ReadAllBytes(path), manifest = File.ReadAllBytes(cache.ManifestPath(Volume));
            string revision = cache.Revision(Volume);
            byte[] fingerprint = loaded.Index.CheckpointFingerprint!;
            byte[] canonical = IndexFingerprint.Canonical(loaded.Index, default);
            var live = new LiveVolumeIndex(loaded.Index, Initial);
            live.Apply(new LiveTransaction(Initial, new(1, 1), [new IndexedFileState(10, true, [new(5, @"C:\", "renamed")])]));
            Assert.True(cache.TryUpgradeSnapshot(Volume, loaded with { Layers = live.Layers }));
            Assert.True(SnapshotStore.IsCurrentFormat(path));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
            Assert.Equal(manifest, File.ReadAllBytes(cache.ManifestPath(Volume)));
            Assert.Equal(revision, cache.Revision(Volume));
            var restored = cache.Load(Volume)!;
            Assert.Equal(loaded.Manifest, restored.Manifest);
            Assert.Equal(fingerprint, restored.Index.CheckpointFingerprint);
            Assert.Equal(canonical, IndexFingerprint.Canonical(restored.Index, default));
            Assert.Single(restored.Index.Search(@"directory\file.txt").Rows);
            Assert.False(cache.TryUpgradeSnapshot(Volume, restored));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepairFromSameGenerationBackupPreservesTheOnlyHealthyBackup(bool currentBackup)
    {
        string directory = Temporary();
        try
        {
            var (cache, _, path) = Legacy(directory);
            if (currentBackup) Assert.True(cache.TryUpgradeSnapshot(Volume, cache.Load(Volume)!));
            File.Copy(path, path + ".bak", overwrite: true);
            byte[] healthy = File.ReadAllBytes(path + ".bak");
            if (currentBackup)
            {
                byte[] corrupt = (byte[])healthy.Clone(); corrupt[^1] ^= 1;
                File.WriteAllBytes(path, corrupt);
            }
            else File.WriteAllText(path, "broken primary");
            var loaded = cache.Load(Volume)!;
            Assert.False(loaded.UsedBackup); Assert.True(loaded.UsedSnapshotBackup);
            Assert.True(cache.TryUpgradeSnapshot(Volume, loaded));
            Assert.True(SnapshotStore.IsCurrentFormat(path));
            Assert.Equal(healthy, File.ReadAllBytes(path + ".bak"));
            var repaired = cache.Load(Volume)!;
            Assert.False(repaired.UsedSnapshotBackup); Assert.False(repaired.UsedBackup);
            Assert.Equal(loaded.Manifest, repaired.Manifest);
            Assert.Equal(IndexFingerprint.Canonical(loaded.Index, default), IndexFingerprint.Canonical(repaired.Index, default));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void PreviousManifestFallbackIsNotReencoded()
    {
        string directory = Temporary();
        try
        {
            var (cache, _, previous) = Legacy(directory);
            using var publication = cache.BeginPublication(Volume, Baseline(), Initial);
            publication.Commit(); publication.Dispose();
            string current = Path.Combine(directory, publication.Manifest.SnapshotFile);
            File.WriteAllText(current, "broken current generation");
            var loaded = cache.Load(Volume)!;
            Assert.True(loaded.UsedBackup);
            Assert.False(cache.TryUpgradeSnapshot(Volume, loaded));
            Assert.False(File.Exists(previous + ".bak"));
            Assert.Equal("broken current generation", File.ReadAllText(current));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void IneligibleStaticOrIdentitylessSourceKeepsItsExistingUpgradePath(bool journal, bool identity)
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory, journal, identity);
            byte[] original = File.ReadAllBytes(path);
            Assert.False(cache.TryUpgradeSnapshot(Volume, loaded));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void ModifiedLayerCannotReplaceTheRawSnapshot()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            var view = loaded.Index.WithView([1UL], loaded.Index.Directories, default);
            Assert.False(cache.TryUpgradeSnapshot(Volume, loaded with { Index = view }));
            Assert.False(SnapshotStore.IsCurrentFormat(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task StaleGenerationCannotPublishAfterAConcurrentPublisher()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, original) = Legacy(directory);
            using var next = cache.BeginPublication(Volume, Baseline(), Initial);
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task<bool> encoding = Task.Run(() => cache.TryUpgradeSnapshot(Volume, loaded, stop.Token));
            next.Commit(); next.Dispose();
            Assert.False(await encoding);
            Assert.Equal(next.Manifest, cache.CurrentManifest(Volume));
            Assert.False(File.Exists(original + ".bak"));
            Assert.False(SnapshotStore.IsCurrentFormat(original));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledLeaseWaitKeepsLoadedResultsAndLeavesNoCandidate()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            byte[] original = File.ReadAllBytes(path);
            using var lease = cache.AcquireLease(Volume, exclusive: true, default);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.False(await Task.Run(() => cache.TryUpgradeSnapshot(Volume, loaded, stop.Token)));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.Equal(2, loaded.Index.Search("").TotalCount);
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
            Assert.False(File.Exists(path + ".bak"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task ConcurrentEncodersPublishExactlyOnce()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            byte[] original = File.ReadAllBytes(path);
            bool[] completed = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                Task.Run(() => cache.TryUpgradeSnapshot(Volume, loaded))));
            Assert.Single(completed, value => value);
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
            Assert.True(SnapshotStore.IsCurrentFormat(path));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void CancelledWindowsReplacementKeepsPrimaryAndCleansCandidate()
    {
        if (!OperatingSystem.IsWindows()) return;
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            byte[] original = File.ReadAllBytes(path);
            using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.False(cache.TryUpgradeSnapshot(Volume, loaded, stop.Token));
            Assert.Equal(original, File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task LegacyCheckpointAndCompactedWalKeepAnchorsAfterReencoding()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, _) = Legacy(directory);
            string generation = loaded.Manifest!.SnapshotFile;
            using var writer = new ChangeLogStore(directory, generation, Initial, writable: true);
            var prefix = writer.Append(new(Initial, new(1, 1), []));
            string checkpoint = Path.Combine(directory, generation + ".live-" + writer.LogId + ".state");
            CompactDirectoryGraphTests.WriteLegacy(loaded.Index, checkpoint,
                [(5, 0, "", @"C:\", false), (10, 5, "directory", null, false),
                 (88, 0, "", @"C:\historic\", false)], prefix.Sequence, prefix.Position);
            writer.Compact(prefix.Sequence);
            writer.Append(new(new(1, 1), new(1, 2), [new(30, false, [new(88, @"C:\wrong\", "tail.txt")], 7, 8)]));
            byte[] wal = File.ReadAllBytes(writer.LogPath);
            byte[] head = File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(writer.LogPath)!, "head.json"));
            async Task<string[]> Restore(CachedVolume cached)
            {
                var feed = new LiveIndexFeed(directory);
                try
                {
                    var layers = await feed.AttachAsync(Volume, cached, default);
                    var catalog = new SearchCatalog(); catalog.PublishLayers("test", layers);
                    Assert.Single(catalog.Search(@"historic\tail.txt").Rows);
                    return catalog.Search("", SortField.Path).Rows.Select(row => row.FullPath).ToArray();
                }
                finally { feed.RemoveExcept([]); }
            }
            string[] expected = await Restore(loaded);
            Assert.True(cache.TryUpgradeSnapshot(Volume, loaded));
            Assert.Equal(expected, await Restore(cache.Load(Volume)!));
            Assert.Equal(wal, File.ReadAllBytes(writer.LogPath));
            Assert.Equal(head, File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(writer.LogPath)!, "head.json")));
            Assert.Equal(loaded.Manifest, cache.CurrentManifest(Volume));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void UpgradeCleansOnlyItsOwnAbandonedCandidates()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            string stale = path + ".format-abandoned.tmp";
            string interruptedWrite = stale + ".writer.tmp";
            string other = Path.Combine(directory, "another-generation.ntidx.format-untouched.tmp");
            File.WriteAllText(stale, "abandoned"); File.WriteAllText(interruptedWrite, "abandoned write");
            File.WriteAllText(other, "other generation");
            Assert.True(cache.TryUpgradeSnapshot(Volume, loaded));
            Assert.False(File.Exists(stale)); Assert.False(File.Exists(interruptedWrite));
            Assert.True(File.Exists(other));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BackgroundQueueCloseCancelsItsWorkerAndKeepsPublishedResults()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, path) = Legacy(directory);
            var maintenance = new SnapshotFormatMaintenance(cache);
            Assert.True(maintenance.Enqueue(Volume, loaded));
            using var lease = cache.AcquireLease(Volume, exclusive: true, default);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => maintenance.RunAsync(lifetime.Token));
            Assert.False(maintenance.Enqueue(Volume, loaded));
            Assert.False(SnapshotStore.IsCurrentFormat(path));
            Assert.Equal(2, loaded.Index.Count); Assert.False(File.Exists(path + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BackgroundQueueUsesApplicationLifetimeAfterLoadTokenIsDisposed()
    {
        string directory = Temporary();
        try
        {
            var (cache, _, path) = Legacy(directory);
            var maintenance = new SnapshotFormatMaintenance(cache);
            using (var loadLifetime = new CancellationTokenSource())
            {
                var loaded = cache.Load(Volume, loadLifetime.Token)!;
                Assert.True(maintenance.Enqueue(Volume, loaded));
                loadLifetime.Cancel();
            }
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Task worker = maintenance.RunAsync(lifetime.Token);
            try
            {
                bool converted = false;
                while (!converted && !lifetime.IsCancellationRequested)
                {
                    await Task.Delay(10, lifetime.Token);
                    try { converted = SnapshotStore.IsCurrentFormat(path); } catch (IOException) { }
                    if (worker.IsFaulted) await worker;
                }
                Assert.True(converted);
            }
            finally
            {
                lifetime.Cancel();
                try { await worker; } catch (OperationCanceledException) { }
            }
            Assert.Equal(2, cache.Load(Volume)!.Index.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void BackgroundQueueIsBoundedAndDeduplicatesGenerations()
    {
        string directory = Temporary();
        try
        {
            var (cache, loaded, _) = Legacy(directory);
            var maintenance = new SnapshotFormatMaintenance(cache);
            Assert.True(maintenance.Enqueue(Volume, loaded)); Assert.False(maintenance.Enqueue(Volume, loaded));
            for (int i = 0; i < 15; i++)
                Assert.True(maintenance.Enqueue(Volume, loaded with
                { Manifest = loaded.Manifest! with { SnapshotFile = VolumeCache.Key(Volume) + "." + Guid.NewGuid().ToString("N") + ".ntidx" } }));
            Assert.False(maintenance.Enqueue(Volume, loaded with
            { Manifest = loaded.Manifest! with { SnapshotFile = VolumeCache.Key(Volume) + "." + Guid.NewGuid().ToString("N") + ".ntidx" } }));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static (VolumeCache Cache, CachedVolume Loaded, string Path) Legacy(string directory,
        bool journal = true, bool identity = true)
    {
        var cache = new VolumeCache(directory);
        SearchIndex baseline;
        if (identity) baseline = Baseline();
        else { var builder = new IndexBuilder(); builder.Add(@"C:\", "static.txt", 42, 123); baseline = builder.Build(); }
        using var publication = cache.BeginPublication(Volume, baseline, journal ? Initial : null);
        publication.Commit(); publication.Dispose();
        string path = Path.Combine(directory, publication.Manifest.SnapshotFile);
        LegacySnapshotFixture.Write(baseline, path);
        return (cache, cache.Load(Volume)!, path);
    }

    private static string Temporary()
    {
        string directory = Path.Combine(Path.GetTempPath(), "snapshot-format-upgrade-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); return directory;
    }
}
