using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed record CollectorMetrics(string Session, int ScanLimit, int WorkerProcessors, long MemoryBudget,
    string[] ActiveRoots, int[] ActiveProcesses, int PeakProcesses, DateTime UpdatedUtc);

internal sealed class CollectorMetricsWriter(string root, string session, IndexingConcurrencyPolicy policy)
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> active = new(StringComparer.Ordinal);
    private int peak;
    public void Started(NtfsVolume volume, int processId)
    {
        lock (gate) { active[volume.Root] = processId; peak = Math.Max(peak, active.Count); Write(); }
    }
    public void Stopped(NtfsVolume volume)
    {
        lock (gate) { active.Remove(volume.Root); Write(); }
    }
    public void Initialize() { lock (gate) Write(); }
    private void Write()
    {
        try
        {
            AtomicJson.Write(Path.Combine(root, "collector-metrics.json"), new CollectorMetrics(session, policy.ScanLimit,
                policy.WorkerProcessors, policy.MemoryBudget, active.Keys.ToArray(), active.Values.ToArray(), peak, DateTime.UtcNow));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
