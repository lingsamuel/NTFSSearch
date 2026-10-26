namespace NtfsSearch.Core;

public sealed record IndexingConcurrencyPolicy(int ScanLimit, int CacheLoadLimit, int WorkerProcessors, long MemoryBudget)
{
    public static IndexingConcurrencyPolicy Create(int processors, long availableMemory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processors);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(availableMemory);
        int scans = Math.Clamp(processors / 4, 1, 4);
        int cache = Math.Clamp(processors / 4, 1, 3);
        int foreground = Math.Max(1, processors / 4);
        return new(scans, cache, Math.Max(1, (processors - foreground) / scans), Math.Max(1, availableMemory / 2));
    }
}
