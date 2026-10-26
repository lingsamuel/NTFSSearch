using NtfsSearch.App;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class FullAuditReadinessTests
{
    private static readonly DateTime Indexed = new(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
    private static readonly VolumeManifest Original = new("volume", @"D:\", "original.ntidx", Indexed, new(1, 0));

    [Theory]
    [InlineData(0, 0, 0, false)]
    [InlineData(9, 8, 0, false)]
    [InlineData(9, 9, null, false)]
    [InlineData(9, 9, 1, false)]
    [InlineData(9, 9, 0, true)]
    public void RestoredFeedsWaitForKnownIdleCollector(int discovered, int live, int? active, bool expected)
        => Assert.Equal(expected, FullAuditReadiness.CanFinish(discovered, live, active));

    [Fact]
    public void PendingJournalRepairAndUnpublishedReplacementPreventAuditCompletion()
    {
        DateTime requested = Indexed.AddMinutes(1);
        Assert.False(FullAuditReadiness.VolumeReady(Original, Original.SnapshotFile, requested));
        var replacement = Original with { SnapshotFile = "replacement.ntidx", IndexedUtc = requested.AddMinutes(1) };
        Assert.False(FullAuditReadiness.VolumeReady(replacement, Original.SnapshotFile, requested));
        Assert.True(FullAuditReadiness.VolumeReady(replacement, replacement.SnapshotFile, requested));
    }

    [Fact]
    public void EarlierRepairMarkersDoNotBlockAnAlreadyPublishedGeneration()
    {
        Assert.True(FullAuditReadiness.VolumeReady(Original, Original.SnapshotFile, null));
        Assert.True(FullAuditReadiness.VolumeReady(Original, Original.SnapshotFile, Indexed));
        Assert.True(FullAuditReadiness.VolumeReady(Original, Original.SnapshotFile, Indexed.AddSeconds(-1)));
    }

    [Fact]
    public void MissingJournalManifestOrPublicationIsNotNativeLiveReadiness()
    {
        Assert.False(FullAuditReadiness.VolumeReady(null, Original.SnapshotFile, null));
        Assert.False(FullAuditReadiness.VolumeReady(Original with { Journal = null }, Original.SnapshotFile, null));
        Assert.False(FullAuditReadiness.VolumeReady(Original, null, null));
    }
}
