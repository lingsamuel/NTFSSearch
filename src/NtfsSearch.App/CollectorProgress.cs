namespace NtfsSearch.App;

internal sealed record CollectorProgress(string Message, DateTime UpdatedUtc)
{
    public int ProcessorCount { get; init; } = Environment.ProcessorCount;
    public string? Details { get; init; }
}
