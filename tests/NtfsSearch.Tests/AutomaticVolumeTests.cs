using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class AutomaticVolumeTests
{
    private static SearchIndex Index(string root, params (string Name, long? Size)[] rows)
    {
        var builder = new IndexBuilder();
        foreach (var row in rows) builder.Add(root, row.Name, row.Size, row.Size);
        return builder.Build();
    }

    [Fact]
    public void MultipleDisksHaveOneGlobalSortAndExactCount()
    {
        var catalog = new SearchCatalog();
        catalog.Publish("c", Index(@"C:\", ("z.txt", 10), ("a.txt", null)));
        catalog.Publish("d", Index(@"D:\", ("b.txt", 20), ("c.txt", 5)));
        Assert.Equal(4, catalog.Count);
        Assert.Equal(new[] { "b.txt", "z.txt", "c.txt", "a.txt" },
            catalog.Search("txt", SortField.Size, true).Rows.Select(r => r.Name));
        var result = catalog.Search("txt", SortField.Name, offset: 1, limit: 2);
        Assert.Equal(4, result.TotalCount);
        Assert.Equal(new[] { "b.txt", "c.txt" }, result.Rows.Select(r => r.Name));
        catalog.RemoveExcept(["d"]);
        Assert.Equal(2, catalog.Search("").TotalCount);
    }

    [Fact]
    public async Task StartupDiscoversAndLoadsDisksWithoutSelectingAnyFile()
    {
        var backend = new FakeBackend();
        var catalog = new SearchCatalog();
        var loader = new AutomaticVolumeLoader(backend, catalog);
        await loader.RefreshAsync();
        Assert.Equal(2, catalog.Count);
        Assert.Equal(1, backend.Starts);
        Assert.Equal(2, backend.Loads);
        await loader.RefreshAsync();
        Assert.Equal(2, backend.Loads);
        backend.Volumes = [backend.Volumes[1]];
        await loader.RefreshAsync();
        Assert.Equal(1, catalog.Count);
    }

    [Fact]
    public async Task OneDamagedVolumeDoesNotHideOtherDisksOrLoopLoading()
    {
        var backend = new FakeBackend { DamageFirst = true };
        var catalog = new SearchCatalog();
        var loader = new AutomaticVolumeLoader(backend, catalog);
        await loader.RefreshAsync(); await loader.RefreshAsync();
        Assert.Equal(1, catalog.Count);
        Assert.Equal(2, backend.Loads);
        Assert.Contains(loader.Statuses, s => s.Message.Contains("损坏"));
        Assert.Equal(1, backend.Rebuilds);
    }

    [Fact]
    public async Task CollectorHandoverCanRestartAfterOldOwnerLeaves()
    {
        var backend = new FakeBackend();
        var loader = new AutomaticVolumeLoader(backend, new SearchCatalog());
        await loader.RefreshAsync();
        backend.NeedsRestart = true;
        await loader.RefreshAsync();
        Assert.Equal(2, backend.Starts);
    }

    [Fact]
    public void AutomaticCacheFallsBackAndRejectsDifferentDiskIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "ntfs-auto-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = new VolumeCache(root);
            var disk = new NtfsVolume("volume-one", @"C:\", 100, "system");
            cache.Publish(disk, Index(@"C:\", ("old.txt", 1)));
            var latest = cache.Publish(disk, Index(@"C:\", ("new.txt", 2)));
            File.WriteAllText(Path.Combine(root, latest.SnapshotFile), "broken");
            var loaded = cache.Load(disk)!;
            Assert.True(loaded.UsedBackup);
            Assert.Equal("old.txt", Assert.Single(loaded.Index.Search("").Rows).Name);
            Assert.Null(cache.Load(disk with { Id = "different-disk" }));
            Assert.Null(cache.Load(disk with { Root = @"D:\" }));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private sealed class FakeBackend : IAutomaticVolumeBackend
    {
        public NtfsVolume[] Volumes = [new("one", @"C:\", 100, ""), new("two", @"D:\", 100, "")];
        public bool DamageFirst;
        public bool NeedsRestart;
        public bool CollectorNeedsRestart => NeedsRestart;
        public int Starts, Loads, Rebuilds;
        public void RequestRebuild(NtfsVolume volume) => Rebuilds++;
        public IReadOnlyList<NtfsVolume> Discover() => Volumes;
        public string CacheRevision(NtfsVolume volume) => "v1";
        public Task EnsureCollectorAsync(CancellationToken token) { Starts++; NeedsRestart = false; return Task.CompletedTask; }
        public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token)
        {
            Loads++;
            if (DamageFirst && volume.Id == "one") throw new InvalidDataException("损坏的缓存");
            return Task.FromResult<CachedVolume?>(new(Index(volume.Root, ("real.txt", 1)), false));
        }
        public string ReadStatus(NtfsVolume volume) => "等待后台采集";
    }

    [Fact]
    public void BasicNtfsScanReusesPathOrderingForUnknownMetadata()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "z"); builder.Add(@"D:\", "a");
        var index = builder.Build();
        Assert.Equal(2 * 2 * sizeof(int), index.Statistics.SortBytes);
        foreach (var field in new[] { SortField.Size, SortField.Modified })
            Assert.Equal(new[] { @"C:\z", @"D:\a" }, index.Search("", field, true).Rows.Select(r => r.FullPath));
    }

    [Fact]
    public void MultiDiskPagingCrossesBatchBoundariesWithoutChangingOrder()
    {
        var catalog = new SearchCatalog();
        var all = new List<SearchRow>();
        for (int disk = 0; disk < 3; disk++)
        {
            var builder = new IndexBuilder();
            for (int i = 0; i < 550; i++) builder.Add($"{(char)('C' + disk)}:\\", $"file{i:D4}", i * 3L + disk, i * 3L + disk);
            var index = builder.Build();
            all.AddRange(Enumerable.Range(0, index.Count).Select(index.GetRow));
            catalog.Publish(disk.ToString(), index);
        }
        var result = catalog.Search("file", SortField.Size, true, offset: 1300, limit: 71);
        Assert.Equal(1650, result.TotalCount);
        Assert.Equal(all.OrderByDescending(r => r.Size).Skip(1300).Take(71).Select(r => r.FullPath), result.Rows.Select(r => r.FullPath));
    }

    [Fact]
    public async Task ConcurrentPublishNeverDeletesCurrentSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "ntfs-publish-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var volume = new NtfsVolume("disk", @"C:\", 100, "");
            var cache = new VolumeCache(root);
            var data = Index(volume.Root, ("real.txt", 1));
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => cache.Publish(volume, data))));
            Assert.Equal(1, cache.Load(volume)!.Index.Count);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PreparedMatchingCanBePagedWithoutRescanningAndRejectsUseAfterDispose()
    {
        var index = Index(@"C:\", ("a.txt", 1), ("b.txt", 2));
        var prepared = index.Prepare("txt", true, default);
        Assert.Equal(new[] { "b.txt", "a.txt" }, prepared.Ids(SortField.Size, true, default).Select(id => index.GetRow(id).Name));
        Assert.Equal(new[] { "a.txt", "b.txt" }, prepared.Ids(SortField.Name, false, default).Select(id => index.GetRow(id).Name));
        prepared.Dispose();
        Assert.Throws<ObjectDisposedException>(() => prepared.Ids(SortField.Name, false, default));
    }
}
