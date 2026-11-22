using System.Globalization;
using System.Net.Http.Json;
using System.Numerics;
using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.Cli;

internal static class LocalSearchClient
{
    internal static async Task RunAsync(string[] args, CancellationToken token)
    {
        bool isSearch = args[0] == "search";
        if (!isSearch && args.Length != 1) throw new ArgumentException("status 不接受参数。");
        var query = isSearch ? ParseQuery(args) : null;
        Uri baseAddress = ResolveAddress();
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false })
        {
            BaseAddress = baseAddress,
            Timeout = isSearch ? LocalSearchEndpoint.OnDemandSortTimeout + TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(12)
        };
        try
        {
            using var response = query is not null
                ? await client.PostAsJsonAsync(LocalSearchEndpoint.SearchPath, query, token)
                : await client.GetAsync(LocalSearchEndpoint.StatusPath, token);
            string body = await response.Content.ReadAsStringAsync(token);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"本机查询失败（HTTP {(int)response.StatusCode}）：{ReadError(body) ?? response.ReasonPhrase}");
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new JsonException();
            Console.WriteLine(body);
        }
        catch (HttpRequestException error)
        {
            throw new InvalidOperationException($"无法连接本机搜索后端 {baseAddress}，请确认桌面程序已启动。{error.Message}", error);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException($"等待本机搜索后端 {baseAddress} 响应超时。", error);
        }
        catch (JsonException error)
        {
            throw new InvalidOperationException($"本机搜索后端 {baseAddress} 返回了无效的 JSON 响应。", error);
        }
    }

    private static Uri ResolveAddress()
    {
        string? portOverride = Environment.GetEnvironmentVariable(LocalSearchEndpoint.PortVariable);
        if (LocalSearchEndpoint.IsPortOverridden(portOverride)) return LocalSearchEndpoint.Resolve(portOverride);

        DesktopSettings settings;
        try { settings = DesktopSettings.Load(PortableDataDirectory.Resolve()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            Console.Error.WriteLine("无法读取应用设置：" + error.Message);
            settings = new();
        }
        return LocalSearchEndpoint.Resolve(portOverride, settings.SearchServicePort);
    }

    private static string? ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String ? error.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    private static LocalSearchRequest ParseQuery(string[] args)
    {
        var query = new LocalSearchRequest();
        bool parseOptions = true;
        for (int i = 1; i < args.Length; i++)
        {
            string argument = args[i];
            if (parseOptions && argument == "--")
            {
                parseOptions = false;
                continue;
            }
            if (!parseOptions || !argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (query.Text is not null) throw new ArgumentException("search 只接受一个查询文本，多词查询请加引号。");
                query = query with { Text = argument };
                continue;
            }
            string Value() => ++i < args.Length ? args[i] : throw new ArgumentException($"{argument} 缺少值。");
            query = argument switch
            {
                "--name-only" => query with { MatchPath = false },
                "--match-path" => query with { MatchPath = true },
                "--sort" => query with { Sort = Value() },
                "--descending" => query with { Descending = true },
                "--offset" => query with { Offset = ParseInteger<int>(Value(), argument) },
                "--limit" => query with { Limit = ParseInteger<int>(Value(), argument) },
                "--min-size-bytes" => query with { MinSizeBytes = ParseInteger<long>(Value(), argument) },
                "--max-size-bytes" => query with { MaxSizeBytes = ParseInteger<long>(Value(), argument) },
                "--modified-from" => query with { ModifiedFrom = Value() },
                "--modified-before" => query with { ModifiedBefore = Value() },
                "--created-from" => query with { CreatedFrom = Value() },
                "--created-before" => query with { CreatedBefore = Value() },
                _ => throw new ArgumentException($"未知搜索选项 {argument}。")
            };
        }
        return query;
    }

    private static T ParseInteger<T>(string value, string option) where T : struct, INumberBase<T> =>
        T.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out T result)
            ? result : throw new ArgumentException($"{option} 必须为范围内的整数。");
}
