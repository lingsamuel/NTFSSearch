using System.Text.Json;
using System.Threading.Channels;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class DesktopSession
{
    private const int PageSize = 200;
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMilliseconds(200);

    private readonly Channel<Action> messages = Channel.CreateUnbounded<Action>(new() { SingleReader = true });
    private readonly SearchHost host;
    private readonly Action<object> publish;
    private readonly string? settingsDirectory;
    private readonly SpaceAnalysisSession spaceAnalysis;
    private readonly QueryRefreshState queryState = new();
    private readonly FileTypeIcons fileTypeIcons = new();
    private CancellationTokenSource? search;
    private Task? searchTask;
    private long refreshGeneration;
    private bool refreshPending;
    private SearchOptions options = new(0, "", SortField.Name, false, true, 0);
    private string? progress = "正在发现本机 NTFS 磁盘…";
    private string? indexError;
    private DesktopSettings settings = new();
    private bool refreshing, discoveryComplete, collectionCancelled, quitRequested;
    private int catalogNoticeQueued;
    private int catalogRefreshRequested;

    internal DesktopSession(SearchHost host, Action<object> publish, string? settingsDirectory)
    {
        this.host = host;
        this.publish = publish;
        this.settingsDirectory = settingsDirectory;
        spaceAnalysis = new(host, publish, Post);
    }

    internal async Task RunAsync(TextReader input)
    {
        using var lifetime = new CancellationTokenSource();
        host.Catalog.Changed += QueueCatalogRefresh;
        host.FileActivity += (observation, parents) =>
        {
            if (spaceAnalysis.QueueActivity(observation, parents)) QueueRefresh();
        };
        host.StatusChanged += () => Post(() =>
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

        if (settingsDirectory is not null)
            try { settings = DesktopSettings.Load(settingsDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
            { PublishError("action", "无法读取应用设置：" + error.Message); }
        PublishStatus();
        RunQuery();
        _ = ReadInputAsync(input, lifetime.Token);
        Task automatic = Task.Run(() => ObserveAutomaticAsync(lifetime.Token));
        try
        {
            await foreach (var action in messages.Reader.ReadAllAsync())
            {
                action();
                if (quitRequested) break;
            }
        }
        finally
        {
            queryState.Close();
            var activeSearch = search;
            activeSearch?.Cancel();
            await lifetime.CancelAsync();
            await Task.WhenAll(automatic, searchTask ?? Task.CompletedTask, spaceAnalysis.StopAsync());
            activeSearch?.Dispose();
        }

        if (quitRequested)
        {
            string? error = null;
            if (!settings.SkipExitIndexSave)
                try { host.SaveExitSnapshot((written, total) => publish(new { Type = "saving", Written = written, Total = total })); }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                { error = "无法保存退出快照：" + failure.Message; }
            publish(new { Type = "quit-complete", Error = error });
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
                case "quit":
                    quitRequested = true;
                    if (!settings.SkipExitIndexSave)
                        publish(new { Type = "saving", Written = 0, Total = host.Catalog.VolumeCount });
                    messages.Writer.TryComplete();
                    break;
                case "setSkipExitIndexSave":
                    SaveExitPreference(request.SkipExitIndexSave);
                    break;
                case "fileTypeIcons":
                    if (request.Extensions is { Length: > 0 } extensions)
                        _ = PublishFileTypeIconsAsync(extensions, token);
                    break;
                case "spaceAnalysis":
                    spaceAnalysis.View(request.RequestId, request.Root, request.DirectoryId);
                    break;
                case "spacePath":
                    spaceAnalysis.ViewPath(request.RequestId, request.Path, request.Root, request.BreadcrumbIds, request.VolumeKey);
                    break;
                case "spaceDetails":
                    spaceAnalysis.Details(request.RequestId, request.DetailId, request.DirectoryIds, request.ManualDirectoryIds, request.BranchIds);
                    break;
                case "spaceExclude":
                    spaceAnalysis.Exclude(request.RequestId, request.VolumeKey, request.Key, request.FileReference);
                    break;
                case "spaceRestore":
                    spaceAnalysis.Restore(request.RequestId, request.VolumeKey, request.Key);
                    break;
                case "closeSpaceAnalysis":
                    spaceAnalysis.Close(request.RequestId);
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
                case "openWith":
                    _ = OpenFileAsync(request);
                    break;
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

    private async Task OpenFileAsync(DesktopRequest request)
    {
        try { await DesktopFileActions.OpenAsync(request.Key, request.Type == "openWith"); }
        catch (Exception error) { Post(() => PublishError("action", error.Message)); }
    }

    private async Task PublishFileTypeIconsAsync(string[] extensions, CancellationToken token)
    {
        try
        {
            var icons = await fileTypeIcons.GetAsync(extensions, token);
            Post(() => publish(new { Type = "fileTypeIcons", Icons = icons }));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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
        Interlocked.Exchange(ref catalogRefreshRequested, 1);
        QueueRefresh();
    }

    private void QueueRefresh()
    {
        // 层更新可能来自多个卷，队列最多保留一个尚未处理的通知。
        if (Interlocked.Exchange(ref catalogNoticeQueued, 1) != 0) return;
        Post(() =>
        {
            Interlocked.Exchange(ref catalogNoticeQueued, 0);
            if (Interlocked.Exchange(ref catalogRefreshRequested, 0) != 0) queryState.RequestRefresh();
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
            spaceAnalysis.Refresh();
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
                SizeText = FileSizeDisplay.Format(row.Size),
                SizeBytesText = row.Size is long size ? $"{size:N0} B" : null,
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
            Type = "status", Volumes = host.Statuses,
            Indexed = host.Catalog.Count, VolumesReady = host.Catalog.VolumeCount,
            Active = metrics?.ActiveRoots.Length ?? 0,
            ScanLimit = metrics?.ScanLimit,
            Refreshing = refreshing, DiscoveryComplete = discoveryComplete, CollectionCancelled = collectionCancelled,
            settings.SkipExitIndexSave,
            Progress = progress, Error = indexError
        });
    }

    private void SaveExitPreference(bool skip)
    {
        string? error = null;
        if (settings.SkipExitIndexSave != skip)
        {
            var next = settings with { SkipExitIndexSave = skip };
            try
            {
                if (settingsDirectory is not null) next.Save(settingsDirectory);
                settings = next;
            }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
            { error = "无法保存应用设置：" + failure.Message; }
        }
        publish(new { Type = "exitSavePreference", settings.SkipExitIndexSave, Error = error });
    }

    private void PublishError(string scope, string message, long? requestId = null) =>
        publish(new { Type = "error", Scope = scope, Message = message, RequestId = requestId });

    private sealed record SearchOptions(long RequestId, string Text, SortField Sort, bool Descending, bool MatchPath, int Offset);

    private sealed record DesktopRequest(string Type, long RequestId, string Text = "", SortField Sort = SortField.Name,
        bool Descending = false, bool MatchPath = true, int Offset = 0, string Key = "", long WindowHandle = 0, string[]? Extensions = null,
        string Root = "", string DirectoryId = "", string Path = "", string? VolumeKey = null, string[]? BreadcrumbIds = null,
        long DetailId = 0, string[]? DirectoryIds = null, string[]? ManualDirectoryIds = null, string[]? BranchIds = null,
        bool SkipExitIndexSave = false, string? FileReference = null);
}
