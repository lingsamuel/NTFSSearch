using System.Globalization;

namespace NtfsSearch.Core;

internal static class LocalSearchEndpoint
{
    internal const string PortVariable = "NTFS_SEARCH_PORT";
    internal const string StatusPath = "/v1/status";
    internal const string SearchPath = "/v1/search";

    internal static Uri Resolve(string? configuredPort)
    {
        const int defaultPort = 18791;
        int port = defaultPort;
        if (!string.IsNullOrWhiteSpace(configuredPort) &&
            (!int.TryParse(configuredPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535))
            throw new ArgumentException($"{PortVariable} 必须是 1–65535 之间的端口号。");
        return new UriBuilder("http", "127.0.0.1", port).Uri;
    }
}

internal sealed record LocalSearchRequest
{
    public string? Text { get; init; }
    public bool MatchPath { get; init; } = true;
    public string Sort { get; init; } = "name";
    public bool Descending { get; init; }
    public int Offset { get; init; }
    public int Limit { get; init; } = 100;
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public string? ModifiedFrom { get; init; }
    public string? ModifiedBefore { get; init; }
    public string? CreatedFrom { get; init; }
    public string? CreatedBefore { get; init; }
}
