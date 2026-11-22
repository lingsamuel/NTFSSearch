using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NtfsSearch.Core;
using HttpProtocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols;

namespace NtfsSearch.App;

internal sealed class LocalSearchApi(WebApplication application) : IAsyncDisposable
{
    private const string SearchConcurrency = "search";
    private const int MaxPageSize = 1000;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] DateTimeFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK"];

    /// <summary>只在桌面后端启动回环监听，所有查询复用同一份已发布索引。</summary>
    internal static async Task<LocalSearchApi> StartAsync(SearchHost host, Uri address)
    {
        // 后端标准输出是桌面 JSON 通道。空宿主也可防止通用 Web 环境变量或配置文件增加监听地址。
        var builder = WebApplication.CreateEmptyBuilder(new() { EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrelCore().ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, address.Port, listener => listener.Protocols = HttpProtocols.Http1);
            options.Limits.MaxRequestBodySize = 32 * 1024;
            options.Limits.MaxConcurrentConnections = 32;
        });
        builder.Services.AddRouting();
        builder.Services.AddHostFiltering(options => options.AllowedHosts = [address.Host, "localhost"]);
        builder.Services.AddRateLimiter(options =>
        {
            // 大索引查询占用共享 CPU 与内存，满载时直接返回，避免积压阻塞退出。
            options.AddConcurrencyLimiter(SearchConcurrency, limiter =>
            {
                limiter.PermitLimit = 4;
                limiter.QueueLimit = 0;
            });
            options.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
            options.OnRejected = (rejected, token) => new(rejected.HttpContext.Response.WriteAsJsonAsync(
                new { Error = "本机查询正忙，请稍后重试。" }, token));
        });
        var app = builder.Build();
        app.UseHostFiltering();
        app.UseRateLimiter();
        app.MapGet(LocalSearchEndpoint.StatusPath, () => Results.Json(host.ReadApiStatus()));
        app.MapPost(LocalSearchEndpoint.SearchPath, (HttpRequest request) => SearchAsync(host.Catalog, request, app.Lifetime.ApplicationStopping))
            .RequireRateLimiting(SearchConcurrency);
        try
        {
            await app.StartAsync();
            return new(app);
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await using var app = application;
        await app.StopAsync();
    }

    private static async Task<IResult> SearchAsync(SearchCatalog catalog, HttpRequest request, CancellationToken stopping)
    {
        if (!request.HasJsonContentType())
            return Error("查询请求须发送 JSON。", StatusCodes.Status415UnsupportedMediaType);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(request.HttpContext.RequestAborted, stopping);
        deadline.CancelAfter(SearchTimeout);
        try
        {
            var query = await request.ReadFromJsonAsync<LocalSearchRequest>(deadline.Token);
            if (query is null) return Error("缺少 JSON 查询对象。");
            if (query.Limit is < 0 or > MaxPageSize) return Error($"limit 必须在 0–{MaxPageSize} 之间。");
            SortField sort = query.Sort?.ToLowerInvariant() switch
            {
                "name" => SortField.Name,
                "path" => SortField.Path,
                "size" => SortField.Size,
                "modified" => SortField.Modified,
                _ => throw new ArgumentException("sort 必须为 name、path、size 或 modified。")
            };
            var filter = new SearchFilter(query.MinSizeBytes, query.MaxSizeBytes,
                ParseUtcTicks(query.ModifiedFrom, "modifiedFrom"), ParseUtcTicks(query.ModifiedBefore, "modifiedBefore"),
                ParseUtcTicks(query.CreatedFrom, "createdFrom"), ParseUtcTicks(query.CreatedBefore, "createdBefore"));
            var result = catalog.Search(query.Text ?? "", sort, query.Descending, query.MatchPath,
                query.Offset, query.Limit, deadline.Token, filter);
            deadline.Token.ThrowIfCancellationRequested();
            return Results.Json(new
            {
                Total = result.TotalCount, query.Offset, query.Limit, result.VolumesReady,
                Rows = result.Rows.Select(row => new
                {
                    row.Name, row.FullPath, row.IsDirectory, SizeBytes = row.Size,
                    PathKey = DesktopFileActions.EncodePath(row.FullPath),
                    ModifiedUtc = ToUtc(row.ModifiedUtcTicks), CreatedUtc = ToUtc(row.CreatedUtcTicks)
                })
            });
        }
        catch (JsonException) { return Error("查询 JSON 格式或字段类型无效。"); }
        catch (ArgumentException error) { return Error(error.Message); }
        catch (BadHttpRequestException error) { return Error(error.Message, error.StatusCode); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (request.HttpContext.RequestAborted.IsCancellationRequested) return Results.Empty;
            if (stopping.IsCancellationRequested) return Error("本机 HTTP 搜索服务正在停止。", StatusCodes.Status503ServiceUnavailable);
            return Error("本机查询超时。", StatusCodes.Status504GatewayTimeout);
        }
    }

    private static long? ParseUtcTicks(string? value, string field)
    {
        if (value is null) return null;
        bool hasOffset = value.EndsWith('Z') || value.Length >= 6 && value[^6] is '+' or '-' && value[^3] == ':';
        if (hasOffset && DateTimeOffset.TryParseExact(value, DateTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed)) return parsed.UtcTicks;
        throw new ArgumentException($"{field} 必须为带时区的 ISO 8601 时间戳。");
    }

    private static DateTime? ToUtc(long? ticks) =>
        ticks is long value && value >= DateTime.MinValue.Ticks && value <= DateTime.MaxValue.Ticks
            ? new DateTime(value, DateTimeKind.Utc) : null;

    private static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { Error = message }, statusCode: status);
}
