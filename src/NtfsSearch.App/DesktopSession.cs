using System.Text.Json;
using System.Threading.Channels;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class DesktopSession(SearchHost host, Action<object> publish)
{
    private const int PageSize = 200;
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMilliseconds(200);

    private readonly Channel<Action> messages = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly QueryRefreshState queryState = new();
    private CancellationTokenSource? search;
    private Task? searchTask;
    private long refreshGeneration;
    private bool refreshPending;
    private SearchOptions options = new(0, "", SortField.Name, false, true, 0);
    private string? progress = "正在发现本机 NTFS 磁盘…";
    private string? indexError;
    private bool refreshing, discoveryComplete, collectionCancelled;
    private int catalogNoticeQueued;

    internal async Task RunAsync(TextReader input)
    {
        using var lifetime = new CancellationTokenSource();
        host.Catalog.Changed += QueueCatalogRefresh;
        host.Loader.StatusChanged += () => Post(() =>
        {
            discoveryComplete = true;
            PublishStatus();
        });
        host.Progress += (message, failure) => Post(() =>
        {
            if (failure) indexError = message;
            else progress = message;
            PublishStatus();
        });

        PublishStatus();
        RunQuery();
        _ = ReadInputAsync(input, lifetime.Token);
        Task automatic = ObserveAutomaticAsync(lifetime.Token);
        try
        {
            await foreach (var action in messages.Reader.ReadAllAsync())
                action();
        }
        finally
        {
            queryState.Close();
            var activeSearch = search;
            activeSearch?.Cancel();
            await lifetime.CancelAsync();
            await Task.WhenAll(automatic, searchTask ?? Task.CompletedTask);
            activeSearch?.Dispose();
        }
    }

    private void Post(Action action) => messages.Writer.TryWrite(action);

    private async Task ReadInputAsync(TextReader input, CancellationToken token)
    {
        try
        {
            while (await input.ReadLineAsync(token) is { } line)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<DesktopRequest>(line, JsonSerializerOptions.Web)
                        ?? throw new JsonException("缺少桌面请求。");
                    Post(() => HandleRequest(request, token));
                }
                catch (JsonException error) { Post(() => PublishError("action", error.Message)); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (IOException) { }
        finally { messages.Writer.TryComplete(); }
    }

    private async Task ObserveAutomaticAsync(CancellationToken token)
    {
        try { await host.RunAutomaticAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            Post(() =>
            {
                PublishError("fatal", error.Message);
                messages.Writer.TryComplete();
            });
        }
    }

    private void HandleRequest(DesktopRequest request, CancellationToken token)
    {
        try
        {
            switch (request.Type)
            {
                case "query":
                    if (request.RequestId < options.RequestId) break;
                    options = new(request.RequestId, request.Text, request.Sort,
                        request.Descending, request.MatchPath, request.Offset);
                    RunQuery();
                    break;
                case "rescan":
                    if (refreshing) break;
                    indexError = null;
                    progress = "正在请求重新扫描…";
                    refreshing = true;
                    collectionCancelled = false;
                    PublishStatus();
                    _ = RescanAsync(token);
                    break;
                case "cancel":
                    host.CancelCollection();
                    collectionCancelled = true;
                    progress = "已取消采集。";
                    PublishStatus();
                    break;
                case "open":
                case "reveal":
                case "copy":
                    DesktopFileActions.Run(request.Type, request.Key, request.WindowHandle);
                    if (request.Type == "copy")
                        publish(new { Type = "notice", Message = "已复制完整路径" });
                    break;
                default:
                    throw new ArgumentException("未知的桌面操作。");
            }
        }
        catch (Exception error) { PublishError("action", error.Message); }
    }

    private async Task RescanAsync(CancellationToken token)
    {
        string? error = null;
        try { await host.RefreshAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception failure) { error = failure.Message; }
        Post(() =>
        {
            refreshing = false;
            if (error is not null) indexError = error;
            progress = null;
            PublishStatus();
        });
    }

    private void RunQuery()
    {
        queryState.RequestUser();
        refreshPending = false;
        refreshGeneration++;
        search?.Cancel();
        TryStartQuery();
    }

    private void QueueCatalogRefresh()
    {
        // 层更新可能来自多个卷，队列最多保留一个尚未处理的通知。
        if (Interlocked.Exchange(ref catalogNoticeQueued, 1) != 0) return;
        Post(() =>
        {
            Interlocked.Exchange(ref catalogNoticeQueued, 0);
            queryState.RequestRefresh();
            if (refreshPending) return;
            refreshPending = true;
            _ = WaitForRefreshAsync(++refreshGeneration);
        });
    }

    private async Task WaitForRefreshAsync(long generation)
    {
        await Task.Delay(RefreshWindow);
        Post(() =>
        {
            if (generation != refreshGeneration) return;
            refreshPending = false;
            queryState.RefreshWindowElapsed();
            TryStartQuery();
        });
    }

    private void TryStartQuery()
    {
        if (!queryState.TryStart(out var request)) return;
        var source = new CancellationTokenSource();
        search = source;
        searchTask = ExecuteQueryAsync(request, options, source);
    }

    private async Task ExecuteQueryAsync(QueryRequest request, SearchOptions snapshot, CancellationTokenSource source)
    {
        SearchResult? result = null;
        string? failure = null;
        int offset = snapshot.Offset;
        try
        {
            while (true)
            {
                result = await Task.Run(() => host.Catalog.Search(snapshot.Text, snapshot.Sort, snapshot.Descending,
                    snapshot.MatchPath, offset, PageSize, source.Token), source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (offset == 0 || offset < result.TotalCount) break;
                // 删除可能使最后一页失效，只回退到仍存在的最后一页。
                offset = result.TotalCount == 0 ? 0 : (int)((result.TotalCount - 1) / PageSize * PageSize);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) { failure = error.Message; }
        Post(() =>
        {
            bool cancelled = source.IsCancellationRequested;
            search = null;
            source.Dispose();
            if (queryState.Complete(request, !cancelled && failure is null ? result?.Elapsed : null) && !cancelled)
            {
                if (failure is not null) PublishError(request.UserInitiated ? "query" : "refresh", failure, snapshot.RequestId);
                else if (result is not null)
                {
                    options = snapshot with { Offset = offset };
                    PublishResult(result, snapshot.RequestId, offset, request.UserInitiated);
                }
            }
            TryStartQuery();
        });
    }

    private void PublishResult(SearchResult result, long requestId, int offset, bool userInitiated)
    {
        publish(new
        {
            Type = "result", RequestId = requestId, UserInitiated = userInitiated,
            Offset = offset, Total = result.TotalCount, PageSize,
            UserMilliseconds = queryState.UserElapsed?.TotalMilliseconds,
            VolumesReady = host.Catalog.VolumeCount,
            Rows = result.Rows.Select(row => new
            {
                Key = DesktopFileActions.EncodePath(row.FullPath),
                row.Name, row.FullPath, row.IsDirectory,
                SizeText = row.Size is long size ? $"{size:N0} B" : "—",
                ModifiedText = row.ModifiedUtcTicks is long ticks && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                    ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—"
            }).ToArray()
        });
    }

    private void PublishStatus()
    {
        var metrics = host.Metrics;
        publish(new
        {
            Type = "status", Volumes = host.Loader.Statuses,
            Indexed = host.Catalog.Count, VolumesReady = host.Catalog.VolumeCount,
            Active = metrics?.ActiveRoots.Length ?? 0,
            ScanLimit = metrics?.ScanLimit,
            Refreshing = refreshing, DiscoveryComplete = discoveryComplete, CollectionCancelled = collectionCancelled,
            Progress = progress, Error = indexError
        });
    }

    private void PublishError(string scope, string message, long? requestId = null) =>
        publish(new { Type = "error", Scope = scope, Message = message, RequestId = requestId });

    private sealed record SearchOptions(long RequestId, string Text, SortField Sort, bool Descending, bool MatchPath, int Offset);

    private sealed record DesktopRequest(string Type, long RequestId, string Text = "", SortField Sort = SortField.Name,
        bool Descending = false, bool MatchPath = true, int Offset = 0, string Key = "", long WindowHandle = 0);
}
