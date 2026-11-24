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
#if HTTP_SEARCH_SERVICE
    private readonly LocalSearchService searchService;
    private readonly bool enableServiceByDefault;
#endif
    private readonly Queue<(Func<Task> Apply, bool ExitPreference)> settingsChanges = new();
    private readonly SpaceAnalysisSession spaceAnalysis;
    private readonly DirectorySizeWorker directorySizes;
    private readonly QueryRefreshState queryState = new();
    private readonly FileTypeIcons fileTypeIcons = new();
    private CancellationTokenSource? search;
    private Task? searchTask;
    private Task? fileExtensionTask;
    private long refreshGeneration;
    private bool refreshPending;
    private SearchOptions options = new(0, "", SortField.Name, false, true, 0);
    private string? progress = "正在发现本机 NTFS 磁盘…";
    private string? indexError;
    private volatile DesktopSettings settings = new();
    private SettingsStatus settingsStatus = null!;
    private Task<string?>? settingsChange;
    private bool refreshing, discoveryComplete, collectionCancelled, quitRequested;
    private int catalogNoticeQueued;
    private int catalogRefreshRequested;
    private int directoryNoticeQueued;
    private SearchResult? displayedResult;
    private SearchOptions? displayedPage;
    private DirectorySizeText[] displayedDirectorySizes = [];

    internal DesktopSession(SearchHost host, Action<object> publish, string? settingsDirectory,
        bool isDemo)
    {
        this.host = host;
        this.publish = publish;
        this.settingsDirectory = settingsDirectory;
#if HTTP_SEARCH_SERVICE
        string? portOverride = Environment.GetEnvironmentVariable(LocalSearchEndpoint.PortVariable);
        // 演示模式仅使用显式启用的服务，避免绑定正式桌面的默认端口。
        enableServiceByDefault = !isDemo || LocalSearchEndpoint.IsPortOverridden(portOverride);
        searchService = new(host, portOverride);
#endif
        spaceAnalysis = new(host, publish, Post);
        directorySizes = new(host.Catalog, host.CaptureAnalysisVolumes, QueueDirectoryRefresh);
    }

#if HTTP_SEARCH_SERVICE
    private bool SearchServiceEnabled => settings.SearchServiceEnabled ?? enableServiceByDefault;
#endif
    private bool IndexesFileSize => host.Metadata.HasFlag(IndexMetadata.FileSize);
    private bool IndexesModifiedTime => host.Metadata.HasFlag(IndexMetadata.ModifiedTime);
    private bool IndexesCreationTime => host.Metadata.HasFlag(IndexMetadata.CreationTime);
    private bool SearchDirectorySizesEnabled => IndexesFileSize && settings.SearchDirectorySizes;

    internal async Task RunAsync(TextReader input)
    {
        using var lifetime = new CancellationTokenSource();
        host.Catalog.Changed += QueueCatalogRefresh;
        host.Catalog.Changed += directorySizes.Notify;
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
        // 元数据布局在本进程首次发布前固定，排序缓存可随保存的设置切换。
        host.ConfigureIndex(settings.Metadata, settings.PrecomputeSort);
        directorySizes.SetEnabled(SearchDirectorySizesEnabled);
        string? serviceError = null;
#if HTTP_SEARCH_SERVICE
        try { await searchService.InitializeAsync(SearchServiceEnabled, settings.SearchServicePort); }
        catch (ArgumentException error)
        { serviceError = "本机 HTTP 接口启动失败：" + error.Message; }
        catch (InvalidOperationException error)
        { serviceError = error.Message; }
        if (serviceError is not null) PublishError("action", serviceError);
#endif
        PublishSettingsStatus(serviceError);
        PublishStatus();
        RunQuery();
        using var inputLifetime = new CancellationTokenSource();
        var exitSave = new ExitSave(publish);
        _ = ReadInputAsync(input, exitSave, lifetime.Token, inputLifetime.Token);
        Task automatic = Task.Run(() => ObserveAutomaticAsync(lifetime.Token));
        Task folderSizes = Task.Run(() => directorySizes.RunAsync(lifetime.Token));
        string? exitError = null;
        try
        {
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
                await Task.WhenAll(automatic, folderSizes, searchTask ?? Task.CompletedTask,
                    fileExtensionTask ?? Task.CompletedTask, spaceAnalysis.StopAsync(), StopSettingsAsync(exitSave));
                directorySizes.Dispose();
                activeSearch?.Dispose();
            }

            if (quitRequested && !settings.SkipExitIndexSave)
                try { host.SaveExitSnapshot(exitSave.ReportWritten, exitSave.Token, exitSave.BeginCommit); }
                catch (OperationCanceledException) when (exitSave.Token.IsCancellationRequested) { }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException)
                { exitError = "无法保存退出快照：" + failure.Message; }
        }
        finally
        {
            exitSave.Dispose();
            // 父进程收到退出确认后才关闭 stdin，已在等待读取的管道不能阻塞确认。
            inputLifetime.Cancel();
        }

        if (quitRequested) publish(new { Type = "quit-complete", Error = exitError });
    }

    private void Post(Action action) => messages.Writer.TryWrite(action);

    private async Task ReadInputAsync(TextReader input, ExitSave exitSave, CancellationToken workToken,
        CancellationToken inputToken)
    {
        bool quitReceived = false;
        try
        {
            while (await input.ReadLineAsync(inputToken) is { } line)
            {
                try
                {
                    var request = JsonSerializer.Deserialize<DesktopRequest>(line, JsonSerializerOptions.Web)
                        ?? throw new JsonException("缺少桌面请求。");
                    // quit 之后只直接处理保存控制，普通工作队列随退出关闭。
                    if (request.Type == "skipExitSave")
                    {
                        if (quitReceived) exitSave.Skip();
                    }
                    else if (!quitReceived)
                    {
                        quitReceived = request.Type == "quit";
                        Post(() => HandleRequest(request, workToken));
                    }
                }
                catch (JsonException error) { Post(() => PublishError("action", error.Message)); }
            }
        }
        catch (OperationCanceledException) when (inputToken.IsCancellationRequested) { }
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
            if (!IndexesFileSize && request.Type is ("spaceAnalysis" or "spacePath" or "spaceDetails" or "spaceExclude" or "spaceRestore"))
                throw new InvalidOperationException("请在设置中开启「索引文件大小」并重启应用以使用空间分析。");
            switch (request.Type)
            {
                case "query":
                    if (request.RequestId < options.RequestId) break;
                    bool unavailableSort = request.Sort switch
                    {
                        SortField.Size => !IndexesFileSize,
                        SortField.Modified => !IndexesModifiedTime,
                        _ => false
                    };
                    options = new(request.RequestId, request.Text, unavailableSort ? SortField.Name : request.Sort,
                        !unavailableSort && request.Descending, request.MatchPath, request.Offset, new SearchFilter(
                            request.MinSizeBytes, request.MaxSizeBytes, LocalDayBoundary(request.ModifiedFrom),
                            LocalDayBoundary(request.ModifiedThrough, afterDay: true), LocalDayBoundary(request.CreatedFrom),
                            LocalDayBoundary(request.CreatedThrough, afterDay: true), request.IncludeFiles,
                            request.IncludeDirectories, request.Extensions));
                    RunQuery();
                    break;
                case "quit":
                    quitRequested = true;
                    messages.Writer.TryComplete();
                    break;
                case "setSkipExitIndexSave":
                    ChangeSettings(() =>
                    {
                        SaveSettings(settings with { SkipExitIndexSave = request.SkipExitIndexSave });
                        return Task.CompletedTask;
                    }, exitPreference: true);
                    break;
                case "getSettings":
                    publish(settingsStatus);
                    break;
                case "applySettings":
                    ChangeSettings(() => ApplySettingsAsync(request));
                    break;
#if HTTP_SEARCH_SERVICE
                case "restartSearchService":
                    ChangeSettings(() => searchService.RestartAsync(SearchServiceEnabled, settings.SearchServicePort));
                    break;
#endif
                case "fileTypeIcons":
                    if (request.Extensions is { Length: > 0 } extensions)
                        _ = PublishFileTypeIconsAsync(extensions, token);
                    break;
                case "searchExtensions":
                    if (fileExtensionTask is not { IsCompleted: false })
                        fileExtensionTask = PublishSearchExtensionsAsync(token);
                    break;
                case "spaceAnalysis":
                    spaceAnalysis.View(request.RequestId, request.Root, request.DirectoryId);
                    break;
                case "spaceCapacities":
                    if (settings.ShowSpaceVolumeCapacity && IndexesFileSize)
                        _ = PublishSpaceCapacitiesAsync(request.RequestId, token);
                    else
                        PublishSpaceCapacities(request.RequestId, new());
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
                case "spaceVisibility":
                    spaceAnalysis.SetVisible(request.Enabled == true);
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
                case "reveal":
                    _ = RunShellActionAsync(request);
                    break;
                case "copy":
                    DesktopFileActions.Copy(request.Key, request.WindowHandle);
                    publish(new { Type = "notice", Message = "已复制完整路径" });
                    break;
                default:
                    throw new ArgumentException("未知的桌面操作。");
            }
        }
        catch (Exception error) { PublishError("action", error.Message); }
    }

    private async Task RunShellActionAsync(DesktopRequest request)
    {
        try { await DesktopFileActions.RunShellActionAsync(request.Type, request.Key); }
        catch (Exception error) { Post(() => PublishError("action", error.Message)); }
    }

    private static long? LocalDayBoundary(DateOnly? value, bool afterDay = false)
    {
        if (value is not DateOnly date) return null;
        // 界面两端都是包含的本地日期，查询引擎比较下一个本地午夜的 UTC 值。
        if (afterDay && date == DateOnly.MaxValue) return DateTime.MaxValue.Ticks + 1;
        if (afterDay) date = date.AddDays(1);
        return date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime().Ticks;
    }

    private async Task PublishSearchExtensionsAsync(CancellationToken token)
    {
        try
        {
            var extensions = await Task.Run(() => host.Catalog.GetFileExtensions(token), token);
            publish(new { Type = "searchExtensions", Extensions = extensions });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        { publish(new { Type = "searchExtensions", Error = error.Message }); }
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

    private void QueueDirectoryRefresh()
    {
        host.SortPrecompute.Notify();
        if (Interlocked.Exchange(ref directoryNoticeQueued, 1) != 0) return;
        Post(() =>
        {
            Interlocked.Exchange(ref directoryNoticeQueued, 0);
            if (!directorySizes.Enabled) return;
            if (options.Sort == SortField.Size)
            {
                if (search is not null || displayedResult?.DirectorySortPending == true) QueueCatalogRefresh();
            }
            else if (displayedPage == options)
                PublishDirectorySizes(options);
        });
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
        bool includeDirectorySizes = directorySizes.Enabled;
        if (request.UserInitiated)
        {
            host.SortPrecompute.Pause();
            if (includeDirectorySizes) directorySizes.Pause();
        }
        searchTask = ExecuteQueryAsync(request, options, source, includeDirectorySizes);
    }

    private async Task ExecuteQueryAsync(QueryRequest request, SearchOptions snapshot, CancellationTokenSource source,
        bool includeDirectorySizes)
    {
        SearchResult? result = null;
        string? failure = null;
        int offset = snapshot.Offset;
        try
        {
            while (true)
            {
                result = await Task.Run(() => host.Catalog.SearchDesktop(snapshot.Text, snapshot.Sort, snapshot.Descending,
                    snapshot.MatchPath, offset, PageSize, source.Token, includeDirectorySizes, snapshot.Filter), source.Token);
                source.Token.ThrowIfCancellationRequested();
                if (offset == 0 || offset < result.TotalCount) break;
                // 删除可能使最后一页失效，只回退到仍存在的最后一页。
                offset = result.TotalCount == 0 ? 0 : (int)((result.TotalCount - 1) / PageSize * PageSize);
            }
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception error) { failure = error.Message; }
        finally
        {
            if (request.UserInitiated)
            {
                if (includeDirectorySizes) directorySizes.Resume();
                host.SortPrecompute.Resume();
            }
        }
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
                    PublishResult(result, options, request.UserInitiated);
                }
            }
            TryStartQuery();
        });
    }

    private void PublishResult(SearchResult result, SearchOptions page, bool userInitiated)
    {
        string[] terms = SearchQuery.Parse(page.Text);
        displayedPage = page;
        displayedResult = result;
        displayedDirectorySizes = result.DirectoryRows?.Select(source => ReadDirectorySize(source,
            page.Sort == SortField.Size ? source.SortSizes : source.Volume.DirectorySizes)).ToArray() ?? [];
        int folder = 0;
        publish(new
        {
            Type = "result", page.RequestId, UserInitiated = userInitiated,
            page.Offset, Total = result.TotalCount, PageSize,
            UserMilliseconds = queryState.UserElapsed?.TotalMilliseconds,
            result.VolumesReady,
            result.DirectorySortPending, result.DirectorySortFailed,
            Rows = result.Rows.Select((row, index) =>
            {
                var size = result.DirectoryRows is { } directories && folder < directories.Count && directories[folder].PageIndex == index
                    ? displayedDirectorySizes[folder++] : new DirectorySizeText(FileSizeDisplay.Format(row.Size),
                        row.Size is long bytes ? $"{bytes:N0} B" : null, null);
                return new
                {
                    Key = DesktopFileActions.EncodePath(row.FullPath),
                    row.Name, row.FullPath, row.IsDirectory,
                    MatchRanges = SearchQuery.MatchRanges(row.FullPath, row.Name.Length, terms, page.MatchPath),
                    size.SizeText, size.SizeBytesText, size.SizeHint,
                    ModifiedText = row.ModifiedUtcTicks is long ticks && ticks >= DateTime.MinValue.Ticks && ticks <= DateTime.MaxValue.Ticks
                        ? new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—"
                };
            }).ToArray()
        });
    }

    private void PublishDirectorySizes(SearchOptions page)
    {
        if (displayedResult?.DirectoryRows is not { } folders) return;
        var changes = new List<object>();
        for (int index = 0; index < folders.Count; index++)
        {
            var source = folders[index];
            if (host.Catalog.FindVolume(source.Volume.Id) != source.Volume) continue;
            var size = ReadDirectorySize(source, source.Volume.DirectorySizes);
            if (displayedDirectorySizes[index] == size) continue;
            displayedDirectorySizes[index] = size;
            var row = displayedResult.Rows[source.PageIndex];
            changes.Add(new
            {
                Key = DesktopFileActions.EncodePath(row.FullPath), size.SizeText, size.SizeBytesText, size.SizeHint
            });
        }
        if (changes.Count != 0) publish(new { Type = "directorySizes", page.RequestId, Rows = changes });
    }

    private static DirectorySizeText ReadDirectorySize(DirectorySearchRow source, DirectorySizeOrder[]? orders)
    {
        return orders switch
        {
            { Length: > 0 } sizes when sizes[source.Layer].GetSize(source.Row) is Int128 bytes =>
                new(FileSizeDisplay.Format(bytes), $"{bytes:N0} B", null),
            { Length: > 0 } => new("—", null, "部分后代文件大小未知"),
            { Length: 0 } => new("—", null, "目录大小统计失败"),
            _ when source.Volume.SupportsDirectorySizes => new("—", null, "正在计算目录大小"),
            _ => new("—", null, null)
        };
    }

    private readonly record struct DirectorySizeText(string SizeText, string? SizeBytesText, string? SizeHint);

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
            settings.SkipExitIndexSave, IndexFileSize = IndexesFileSize, IndexModifiedTime = IndexesModifiedTime,
            IndexCreationTime = IndexesCreationTime,
            Progress = progress, Error = indexError
        });
    }

    private void ChangeSettings(Func<Task> change, bool exitPreference = false)
    {
        settingsChanges.Enqueue((change, exitPreference));
        RunNextSettingsChange();
    }

    private void RunNextSettingsChange()
    {
        if (settingsChange is not null || !settingsChanges.TryDequeue(out var change)) return;
        if (!change.ExitPreference)
        {
            settingsStatus = settingsStatus with { Busy = true, Error = null };
            publish(settingsStatus);
        }
        // 同一队列串行保存设置页与索引状态的偏好。等待 HTTP 切换时仍处理桌面查询。
        settingsChange = Task.Run(async () =>
        {
            string? error = await ApplySettingsChangeAsync(change.Apply);
            Post(() =>
            {
                settingsChange = null;
                if (change.ExitPreference)
                {
                    settingsStatus = settingsStatus with { SkipExitIndexSave = settings.SkipExitIndexSave };
                    publish(new { Type = "exitSavePreference", settings.SkipExitIndexSave, Error = error });
                }
                else
                {
                    RefreshDirectorySizePreference();
                    PublishSettingsStatus(error);
                }
                RunNextSettingsChange();
            });
            return error;
        });
    }

    private static async Task<string?> ApplySettingsChangeAsync(Func<Task> apply)
    {
        try { await apply(); return null; }
        catch (Exception failure) { return failure.Message; }
    }

    private Task ApplySettingsAsync(DesktopRequest request)
    {
        if (request.SearchDirectorySizes is not bool includeDirectories)
            throw new ArgumentException("设置项不完整。");
        var next = settings with
        {
            SearchDirectorySizes = includeDirectories,
            SkipExitIndexSave = request.SkipExitIndexSave,
            PrecomputeSort = request.PrecomputeSort ?? settings.PrecomputeSort,
            IndexCreationTime = request.IndexCreationTime ?? settings.IndexCreationTime,
            IndexModifiedTime = request.IndexModifiedTime ?? settings.IndexModifiedTime,
            IndexFileSize = request.IndexFileSize ?? settings.IndexFileSize,
            ShowSpaceVolumeCapacity = request.ShowSpaceVolumeCapacity ?? settings.ShowSpaceVolumeCapacity,
            SpaceVolumeShowFree = request.SpaceVolumeShowFree ?? settings.SpaceVolumeShowFree,
            SpaceVolumeCapacityBar = request.SpaceVolumeCapacityBar ?? settings.SpaceVolumeCapacityBar
        };
#if HTTP_SEARCH_SERVICE
        if (request.Enabled is not bool enable) throw new ArgumentException("设置项不完整。");
        if (enable != SearchServiceEnabled || request.Port != settings.SearchServicePort)
            return searchService.ApplyAsync(enable, request.Port,
                () => SaveSettings(next with { SearchServiceEnabled = enable, SearchServicePort = request.Port }));
#endif
        SaveSettings(next);
        return Task.CompletedTask;
    }

    private void RefreshDirectorySizePreference()
    {
        if (!directorySizes.SetEnabled(SearchDirectorySizesEnabled)) return;
        // 丢弃展示页和在途搜索捕获的统计引用，再按原搜索条件刷新。
        displayedResult = null;
        displayedPage = null;
        displayedDirectorySizes = [];
        search?.Cancel();
        queryState.RequestRefresh();
        queryState.RefreshWindowElapsed();
        TryStartQuery();
    }

    private void PublishSettingsStatus(string? error)
    {
#if HTTP_SEARCH_SERVICE
        settingsStatus = new(true, SearchServiceEnabled, settings.SearchServicePort,
            searchService.Address?.GetLeftPart(UriPartial.Authority), searchService.PortOverridden,
            searchService.Running,
#else
        settingsStatus = new(false, false, settings.SearchServicePort, null, false, false,
#endif
            Busy: false, error, settings.SearchDirectorySizes, settings.SkipExitIndexSave,
            settings.PrecomputeSort, settings.IndexCreationTime, settings.IndexModifiedTime, settings.IndexFileSize,
            settings.Metadata != host.Metadata, settings.ShowSpaceVolumeCapacity, settings.SpaceVolumeShowFree,
            settings.SpaceVolumeCapacityBar);
        publish(settingsStatus);
    }

    private Task PublishSpaceCapacitiesAsync(long requestId, CancellationToken token)
    {
        // 可移动卷的容量查询可能等待 I/O，在线程池读取以免阻塞导航和设置保存。
        var volumes = host.CaptureAnalysisVolumes().Select(view => view.Volume).ToArray();
        return Task.Run(() =>
        {
            var capacities = new Dictionary<string, SpaceVolumeCapacity>(StringComparer.OrdinalIgnoreCase);
            foreach (var volume in volumes)
            {
                if (token.IsCancellationRequested) return;
                if (host.ReadSpaceVolumeCapacity(volume) is { } capacity) capacities[volume.Root] = capacity;
            }
            if (!token.IsCancellationRequested) PublishSpaceCapacities(requestId, capacities);
        }, token);
    }

    private void PublishSpaceCapacities(long requestId, Dictionary<string, SpaceVolumeCapacity> capacities)
        => publish(new { Type = "spaceCapacities", RequestId = requestId, Capacities = capacities });

    private async Task StopSettingsAsync(ExitSave exitSave)
    {
#if HTTP_SEARCH_SERVICE
        await using var service = searchService;
#endif
        string? activeError = settingsChange is not null ? await settingsChange : null;
        if (!quitRequested) return;
        if (activeError is not null) PublishError("action", activeError);
        // 此时桌面消息通道已关闭，直接完成已接收的设置再决定是否保存索引。
        while (settingsChanges.TryDequeue(out var change))
            if (await ApplySettingsChangeAsync(change.Apply) is { } error)
                PublishError("action", error);
        if (!settings.SkipExitIndexSave)
            exitSave.Start(host.Catalog.VolumeCount);
    }

    private sealed class ExitSave(Action<object> publish) : IDisposable
    {
        private readonly object gate = new();
        private readonly CancellationTokenSource cancellation = new();
        private string? phase;
        private int written, total;

        internal CancellationToken Token => cancellation.Token;

        internal void Start(int volumes)
        {
            lock (gate)
            {
                total = volumes;
                Publish(cancellation.IsCancellationRequested ? "skipping" : "writing");
            }
        }

        internal void ReportWritten(int writtenVolumes, int totalVolumes)
        {
            lock (gate)
            {
                if (phase != "writing") return;
                written = writtenVolumes;
                total = totalVolumes;
                Publish("writing");
            }
        }

        internal void Skip()
        {
            lock (gate)
            {
                if (phase is not (null or "writing") || cancellation.IsCancellationRequested) return;
                cancellation.Cancel();
                if (phase == "writing") Publish("skipping");
            }
        }

        internal void BeginCommit()
        {
            // 跳过与原子替换使用同一个边界。提交赢得边界后必须完成替换。
            lock (gate)
            {
                Token.ThrowIfCancellationRequested();
                Publish("committing");
            }
        }

        private void Publish(string next)
        {
            phase = next;
            publish(new { Type = "saving", Phase = next, Written = written, Total = total });
        }

        public void Dispose()
        {
            lock (gate)
            {
                phase = "finished";
                cancellation.Dispose();
            }
        }
    }

    private void SaveSettings(DesktopSettings next)
    {
        if (next == settings) return;
        try
        {
            if (settingsDirectory is not null) next.Save(settingsDirectory);
            settings = next;
            host.SortPrecompute.SetEnabled(next.PrecomputeSort);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        { throw new IOException("无法保存应用设置：" + failure.Message, failure); }
    }

    private void PublishError(string scope, string message, long? requestId = null) =>
        publish(new { Type = "error", Scope = scope, Message = message, RequestId = requestId });

    private sealed record SearchOptions(long RequestId, string Text, SortField Sort, bool Descending, bool MatchPath, int Offset,
        SearchFilter? Filter = null);

    private sealed record SettingsStatus(bool SearchServiceAvailable, bool Enabled, int Port, string? Address, bool PortOverridden,
        bool Running, bool Busy, string? Error, bool SearchDirectorySizes, bool SkipExitIndexSave,
        bool PrecomputeSort, bool IndexCreationTime, bool IndexModifiedTime, bool IndexFileSize, bool IndexRestartRequired,
        bool ShowSpaceVolumeCapacity, bool SpaceVolumeShowFree, bool SpaceVolumeCapacityBar)
    {
        public string Type => "settingsStatus";
    }

    private sealed record DesktopRequest(string Type, long RequestId, string Text = "", SortField Sort = SortField.Name,
        bool Descending = false, bool MatchPath = true, int Offset = 0, string Key = "", long WindowHandle = 0, string[]? Extensions = null,
        string Root = "", string DirectoryId = "", string Path = "", string? VolumeKey = null, string[]? BreadcrumbIds = null,
        long DetailId = 0, string[]? DirectoryIds = null, string[]? ManualDirectoryIds = null, string[]? BranchIds = null,
        bool SkipExitIndexSave = false, string? FileReference = null, bool? Enabled = null, int Port = 0,
        bool? SearchDirectorySizes = null, bool? PrecomputeSort = null, bool? IndexCreationTime = null,
        bool? IndexModifiedTime = null, bool? IndexFileSize = null, bool? ShowSpaceVolumeCapacity = null,
        bool? SpaceVolumeShowFree = null, bool? SpaceVolumeCapacityBar = null, bool IncludeFiles = true, bool IncludeDirectories = true,
        long? MinSizeBytes = null, long? MaxSizeBytes = null, DateOnly? CreatedFrom = null, DateOnly? CreatedThrough = null,
        DateOnly? ModifiedFrom = null, DateOnly? ModifiedThrough = null);
}
