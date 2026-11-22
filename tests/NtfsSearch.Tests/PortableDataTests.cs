using NtfsSearch.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace NtfsSearch.Tests;

public sealed class PortableDataTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "ntfs-migration-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(root, "old");
    private string Target => Path.Combine(root, "new");
    private static readonly NtfsVolume Volume = new("volume-c", @"C:\", 100, "");

    [Fact]
    public void DefaultDataIsBesideExecutable()
    {
        Assert.Equal(Path.GetFullPath(root), PortableDataDirectory.Resolve(root));
        Assert.Equal(AppContext.BaseDirectory, PortableDataDirectory.Resolve());
    }

    [Fact]
    public async Task ValidPrimaryAndBackupAreMigratedWithoutRemovingSource()
    {
        Publish(Source, Volume, "old.txt"); Publish(Source, Volume, "latest.txt");
        File.WriteAllText(Path.Combine(Source, "unrelated.txt"), "leave me");
        var result = await CacheMigration.MigrateAsync(Source, Target, fullValidate: true);
        Assert.Equal(CacheMigrationStatus.Migrated, Assert.Single(result.Volumes).Status);
        Assert.True(result.CanCleanSource);
        Assert.Equal("latest.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
        File.Delete(new VolumeCache(Target).ManifestPath(Volume));
        Assert.Equal("old.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
        Assert.True(File.Exists(new VolumeCache(Source).ManifestPath(Volume)));
        Assert.False(File.Exists(Path.Combine(Target, "unrelated.txt")));
    }

    [Fact]
    public async Task ExistingValidDestinationTakesPriority()
    {
        Publish(Source, Volume, "old.txt"); Publish(Target, Volume, "new.txt");
        string manifest = new VolumeCache(Target).ManifestPath(Volume);
        byte[] before = File.ReadAllBytes(manifest);
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.KeptDestination, Assert.Single(result.Volumes).Status);
        Assert.Equal(before, File.ReadAllBytes(manifest));
        Assert.Equal("new.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
    }

    [Fact]
    public async Task ActiveOldCollectorNeverAllowsCleanup()
    {
        Publish(Source, Volume, "old.txt");
        using var collector = new FileStream(Path.Combine(Source, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var result = await CacheMigration.MigrateAsync(Source, Target, fullValidate: true);
        Assert.True(result.SourceInUse); Assert.False(result.CanCleanSource);
        Assert.True(result.FullValidationCompleted);
        Assert.Equal(CacheMigrationStatus.Migrated, Assert.Single(result.Volumes).Status);
        Assert.True(File.Exists(new VolumeCache(Source).ManifestPath(Volume)));
    }

    [Fact]
    public async Task CancellationDoesNotPublishAnything()
    {
        Publish(Source, Volume, "old.txt");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CacheMigration.MigrateAsync(Source, Target, cancellation.Token));
        Assert.False(Directory.Exists(Target) && Directory.EnumerateFiles(Target, "*.ready.json").Any());
        Assert.True(File.Exists(new VolumeCache(Source).ManifestPath(Volume)));
    }

    [Theory]
    [InlineData("../outside.ntidx")]
    [InlineData("..\\outside.ntidx")]
    [InlineData("C:\\outside.ntidx")]
    public async Task UnsafeManifestReferencesAreRejected(string snapshot)
    {
        Publish(Source, Volume, "old.txt");
        AtomicJson.Write(new VolumeCache(Source).ManifestPath(Volume), new VolumeManifest(Volume.Id, Volume.Root, snapshot, DateTime.UtcNow));
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.Failed, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.Null(new VolumeCache(Target).Load(Volume));
    }

    [Fact]
    public async Task CorruptVolumeDoesNotBlockOtherVolumeAndCanRetry()
    {
        var second = new NtfsVolume("volume-d", @"D:\", 100, "");
        var broken = Publish(Source, Volume, "broken.txt"); Publish(Source, second, "healthy.txt");
        string badSnapshot = Path.Combine(Source, broken.SnapshotFile);
        byte[] saved = File.ReadAllBytes(badSnapshot); File.WriteAllText(badSnapshot, "truncated");
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Contains(result.Volumes, v => v.Status == CacheMigrationStatus.Failed);
        Assert.Contains(result.Volumes, v => v.Status == CacheMigrationStatus.Migrated);
        Assert.False(result.CanCleanSource);
        Assert.Null(new VolumeCache(Target).Load(Volume));
        Assert.Equal("healthy.txt", new VolumeCache(Target).Load(second)!.Index.GetRow(0).Name);
        File.WriteAllBytes(badSnapshot, saved);
        result = await CacheMigration.MigrateAsync(Source, Target, fullValidate: true);
        Assert.True(result.CanCleanSource);
        Assert.Equal("broken.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Target, "*.migration-*"));
    }

    [Fact]
    public async Task CorruptPrimaryFallsBackToCompleteBackupGeneration()
    {
        Publish(Source, Volume, "backup.txt"); var latest = Publish(Source, Volume, "latest.txt");
        File.WriteAllText(Path.Combine(Source, latest.SnapshotFile), "bad");
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.Migrated, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.Equal("backup.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
    }

    [Fact]
    public async Task MissingSourceIsNoOp()
    {
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Empty(result.Volumes); Assert.False(result.CanCleanSource);
        Assert.False(Directory.Exists(Source));
    }

    [Fact]
    public async Task DestinationPublishedWhileMigrationWaitsIsKept()
    {
        Publish(Source, Volume, "old.txt"); Publish(Target, Volume, "new.txt");
        string path = Path.Combine(Target, VolumeCache.Key(Volume) + ".publish.lock");
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task<CacheMigrationResult> migration = CacheMigration.MigrateAsync(Source, Target);
        await Task.Delay(150);
        Assert.False(migration.IsCompleted);
        lease.Dispose();
        var result = await migration.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CacheMigrationStatus.KeptDestination, Assert.Single(result.Volumes).Status);
        Assert.Equal("new.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
    }

    [Fact]
    public async Task CancellationWhileWaitingForPublisherLeavesBothCachesUntouched()
    {
        Publish(Source, Volume, "old.txt"); Publish(Target, Volume, "new.txt");
        string path = Path.Combine(Target, VolumeCache.Key(Volume) + ".publish.lock");
        using var lease = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource();
        Task<CacheMigrationResult> migration = CacheMigration.MigrateAsync(Source, Target, cancellation.Token);
        await Task.Delay(150); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => migration.WaitAsync(TimeSpan.FromSeconds(10)));
        lease.Dispose();
        Assert.Equal("old.txt", new VolumeCache(Source).Load(Volume)!.Index.GetRow(0).Name);
        Assert.Equal("new.txt", new VolumeCache(Target).Load(Volume)!.Index.GetRow(0).Name);
    }

    [Fact]
    public async Task CorruptSnapshotBackupIsRetainedAtSourceOnly()
    {
        var manifest = Publish(Source, Volume, "valid.txt");
        File.WriteAllText(Path.Combine(Source, manifest.SnapshotFile + ".bak"), "corrupt backup");
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.Migrated, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.Empty(Directory.EnumerateFiles(Target, "*.ntidx.bak"));
        Assert.True(File.Exists(Path.Combine(Source, manifest.SnapshotFile + ".bak")));
    }

    [Fact]
    public async Task ValidChecksumWithInvalidSnapshotStructureIsNotMigrated()
    {
        var manifest = Publish(Source, Volume, "invalid.txt");
        string path = Path.Combine(Source, manifest.SnapshotFile);
        byte[] data = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), -1);
        SHA256.HashData(data.AsSpan(0, data.Length - 32), data.AsSpan(data.Length - 32));
        File.WriteAllBytes(path, data);
        var result = await CacheMigration.MigrateAsync(Source, Target, fullValidate: true);
        Assert.Equal(CacheMigrationStatus.Failed, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.Null(new VolumeCache(Target).Load(Volume));
    }

    [Fact]
    public async Task DefaultMigrationKeepsSourceUntilFullFormatValidation()
    {
        Publish(Source, Volume, "valid.txt");
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.Migrated, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.False(result.FullValidationCompleted);
        result = await CacheMigration.MigrateAsync(Source, Target, fullValidate: true);
        Assert.Equal(CacheMigrationStatus.KeptDestination, Assert.Single(result.Volumes).Status);
        Assert.True(result.CanCleanSource);
        Assert.True(result.FullValidationCompleted);
    }

    [Fact]
    public async Task LiveGenerationIsRejectedWithoutPublishingOrModifyingSource()
    {
        var initial = new JournalPosition(1, 0);
        var builder = new IndexBuilder(); builder.Add(Volume.Root, "base.txt", fileReference: 10, parentReference: 5);
        var sourceCache = new VolumeCache(Source);
        VolumeManifest manifest;
        using (var publication = sourceCache.BeginPublication(Volume, builder.Build(), initial))
        {
            manifest = publication.Manifest;
            using var writer = new ChangeLogStore(Source, manifest.SnapshotFile, initial, true);
            writer.Append(new(initial, new(1, 1), [new(11, false, [new(5, Volume.Root, "later.txt")])]));
            publication.Commit();
        }
        byte[] before = File.ReadAllBytes(sourceCache.ManifestPath(Volume));
        var result = await CacheMigration.MigrateAsync(Source, Target);
        Assert.Equal(CacheMigrationStatus.Failed, Assert.Single(result.Volumes).Status);
        Assert.False(result.CanCleanSource);
        Assert.False(result.FullValidationCompleted);
        Assert.Equal(before, File.ReadAllBytes(sourceCache.ManifestPath(Volume)));
        Assert.Null(new VolumeCache(Target).Load(Volume));
        using var reader = new ChangeLogStore(Source, manifest.SnapshotFile, initial);
        Assert.Equal("later.txt", Assert.Single(reader.ReadAfter(0)).Transaction.Files[0].Links[0].Name);
    }

    private static VolumeManifest Publish(string directory, NtfsVolume volume, string name)
    {
        var builder = new IndexBuilder(); builder.Add(volume.Root, name);
        return new VolumeCache(directory).Publish(volume, builder.Build());
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
