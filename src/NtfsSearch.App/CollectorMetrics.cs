namespace NtfsSearch.App;

internal sealed record CollectorMetrics(string Session, int ScanLimit, int WorkerProcessors, long MemoryBudget,
    string[] ActiveRoots, int[] ActiveProcesses, int PeakProcesses, DateTime UpdatedUtc)
{ public string[] ScansStarted { get; init; } = []; }
