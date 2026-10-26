using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed record CollectorMetrics(string Session, int ScanLimit, int WorkerProcessors, long MemoryBudget,
    string[] ActiveRoots, int[] ActiveProcesses, int PeakProcesses, DateTime UpdatedUtc)
{ public string[] ScansStarted { get; init; } = []; }

internal sealed class CollectorMetricsWriter(string root, string session, IndexingConcurrencyPolicy policy)
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> active = new(StringComparer.Ordinal);
    private int peak;
    private readonly HashSet<string> scans = new(StringComparer.Ordinal);
    public void Started(NtfsVolume volume, int processId)
    {
        lock (gate) { active[volume.Root] = processId; scans.Add(volume.Root); peak = Math.Max(peak, active.Count); Write(); }
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
                policy.WorkerProcessors, policy.MemoryBudget, active.Keys.ToArray(), active.Values.ToArray(), peak, DateTime.UtcNow)
            { ScansStarted = scans.ToArray() });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
