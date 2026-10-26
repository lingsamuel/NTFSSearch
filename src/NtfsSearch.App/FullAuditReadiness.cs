using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class FullAuditReadiness
{
    internal static bool CanFinish(int discovered, int live, int? activeCollectors)
        => discovered > 0 && live == discovered && activeCollectors == 0;

    internal static bool VolumeReady(VolumeManifest? manifest, string? publishedGeneration, DateTime? repairRequestedUtc)
        => manifest?.Journal is not null && manifest.SnapshotFile == publishedGeneration
            && (repairRequestedUtc is null || repairRequestedUtc <= manifest.IndexedUtc);
}
