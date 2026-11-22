using System.Globalization;

namespace NtfsSearch.Core;

internal static class LocalSearchEndpoint
{
    internal const string PortVariable = "NTFS_SEARCH_PORT";
    internal const int DefaultPort = 18791;
    internal const string StatusPath = "/v1/status";
    internal const string SearchPath = "/v1/search";

    internal static bool IsPortOverridden(string? portOverride) => !string.IsNullOrWhiteSpace(portOverride);

    internal static void ValidatePort(int port)
    {
        if (!IsValidPort(port)) throw new ArgumentException("端口必须是 1–65535 之间的整数。");
    }

    internal static Uri Resolve(string? portOverride, int savedPort = DefaultPort)
    {
        int port = savedPort;
        if (IsPortOverridden(portOverride) &&
            (!int.TryParse(portOverride, NumberStyles.Integer, CultureInfo.InvariantCulture, out port) || !IsValidPort(port)))
            throw new ArgumentException($"{PortVariable} 必须是 1–65535 之间的端口号。");
        ValidatePort(port);
        return new UriBuilder("http", "127.0.0.1", port).Uri;
    }

    private static bool IsValidPort(int port) => port is >= 1 and <= 65535;
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
