using System.Globalization;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 与桌面消息循环同步安排工作；同一时间只有一个任务能读取或更新汇总。
internal sealed class SpaceAnalysisSession(SearchHost host, Action<object> publish, Action<Action> post)
{
    private const int VisibleItems = 200;
    private readonly QueryRefreshState work = new();
    private (long RequestId, string Root, ulong Directory, string? Path, string? VolumeKey)? selection;
    private (string Root, ulong[] BreadcrumbIds, string? VolumeKey) pathOrigin;
    private long latestRequestId;
    private SpaceUsageIndex? usage;
    private ulong[] trail = [];
    private string? displayedVolumeKey;
    private string[] lastRoots = [];
    private IReadOnlyList<SearchIndex>? lastLayers;
    private string? volumeKey;
    private string? activeRoot;
    private CancellationTokenSource? cancellation;
    private Task? task;

    internal void View(long requestId, string root, string directoryId)
        => Navigate(requestId, root, ParseDirectoryId(directoryId), null);

    internal void ViewPath(long requestId, string input, string originRoot, string[]? originBreadcrumbIds, string? originVolumeKey)
    {
        if (requestId < latestRequestId) return;
        pathOrigin = (originRoot, originBreadcrumbIds?.Select(ParseDirectoryId).ToArray() ?? [], originVolumeKey);
        if (SpaceUsageIndex.NormalizeLocalPath(input) is { } path)
        {
            Navigate(requestId, path[..3], 0, path);
            return;
        }
        latestRequestId = requestId;
        ReportPathError(requestId, "请输入本地磁盘的绝对目录路径，例如 D:\\Games。");
    }

    private void Navigate(long requestId, string root, ulong directory, string? path)
    {
        if (requestId < latestRequestId) return;
        latestRequestId = requestId;
        selection = (requestId, root, directory, path, null);
        work.RequestUser();
        if (activeRoot is not null && !string.IsNullOrEmpty(root) && !SameRoot(root, activeRoot))
            ResetVolume();
        TryStart();
    }

    internal void Refresh()
    {
        if (selection is null) return;
        var views = host.CaptureAnalysisVolumes();
        if (activeRoot is not null && !ContainsVolume(views)) ResetVolume();
        work.RequestRefresh();
        work.RefreshWindowElapsed();
        TryStart(views);
    }

    internal void Close(long requestId)
    {
        if (requestId < latestRequestId) return;
        latestRequestId = requestId;
        selection = null;
        trail = [];
        displayedVolumeKey = null;
        work.Cancel();
        ResetVolume();
    }

    internal async Task StopAsync()
    {
        Close(long.MaxValue);
        work.Close();
        var source = cancellation;
        if (task is not null) await task;
        source?.Dispose();
    }

    private void ResetVolume()
    {
        cancellation?.Cancel();
        activeRoot = volumeKey = null;
        lastRoots = [];
        lastLayers = null;
        usage = null;
    }

    private void TryStart((NtfsVolume Volume, IReadOnlyList<SearchIndex> Layers)[]? views = null)
    {
        if (selection is not { } target || !work.TryStart(out var request)) return;
        views ??= host.CaptureAnalysisVolumes();
        var ordered = views.OrderBy(view => view.Volume.Root, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = ordered.FirstOrDefault(view => SameRoot(view.Volume.Root, target.Root));
        if (selected.Volume is null && target.Path is not null)
        {
            work.Complete(request, null);
            var status = host.Statuses.FirstOrDefault(volume => SameRoot(volume.Root, target.Root));
            ReportPathError(target.RequestId, status is null
                ? $"没有可用的 {target.Root} 本地 NTFS 索引。"
                : $"{target.Root} 索引尚不可用。{status.Message}");
            return;
        }
        if (selected.Volume is null && ordered.Length > 0) selected = ordered[0];
        string[] roots = ordered.Select(view => view.Volume.Root).ToArray();
        string? key = selected.Volume is { } volume ? VolumeCache.Key(volume) : null;
        bool sameVolume = key == volumeKey;
        if (!request.UserInitiated && sameVolume && roots.SequenceEqual(lastRoots)
            && (selected.Layers == lastLayers || selected.Layers is not null && lastLayers is not null && selected.Layers.SequenceEqual(lastLayers)))
        {
            work.Complete(request, null);
            return;
        }
        if (!sameVolume) ResetVolume();
        volumeKey = key;
        lastRoots = roots;
        lastLayers = selected.Layers;
        if (selected.Volume is null)
        {
            work.Complete(request, null);
            trail = [];
            displayedVolumeKey = null;
            Publish(target.RequestId, roots, null, null, null);
            return;
        }

        string root = selected.Volume.Root;
        bool sameDisplayedVolume = key == displayedVolumeKey;
        bool sameLocation = target.VolumeKey is { } originKey ? key == originKey : sameVolume || sameDisplayedVolume;
        ulong directory = SameRoot(root, target.Root) && sameLocation ? target.Directory : 0;
        var source = new CancellationTokenSource();
        activeRoot = root;
        cancellation = source;
        task = ExecuteAsync(request, target.RequestId, roots, root, directory, target.Path, selected.Layers!,
            usage, sameDisplayedVolume ? trail : [], source);
    }

    private async Task ExecuteAsync(QueryRequest request, long requestId, string[] roots, string root, ulong directory, string? path,
        IReadOnlyList<SearchIndex> layers, SpaceUsageIndex? previous, ulong[] fallback, CancellationTokenSource source)
    {
        SpaceUsageIndex? updated = null;
        SpaceFolder? result = null;
        string? error = null;
        try
        {
            (updated, result, error) = await Task.Run<(SpaceUsageIndex, SpaceFolder?, string?)>(() =>
            {
                var token = source.Token;
                var index = previous?.Update(layers, token) ?? SpaceUsageIndex.Create(root, layers, token);
                if (path is not null)
                {
                    var (found, ambiguous) = index.FindDirectory(path, token);
                    if (!found.HasValue)
                    {
                        string reason = ambiguous ? "该路径有多个匹配，请使用目录名称的原大小写。"
                            : "索引中找不到此目录，请确认路径指向已索引的文件夹。";
                        return (index, null, reason);
                    }
                    directory = found.Value;
                }
                else if (!index.Contains(directory))
                    directory = fallback.Reverse().FirstOrDefault(index.Contains);
                return (index, index.Read(directory, VisibleItems, token), (string?)null);
            }, source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { }
        catch (Exception failure) { error = failure.Message; }
        post(() =>
        {
            bool cancelled = source.IsCancellationRequested;
            source.Dispose();
            cancellation = null;
            activeRoot = null;
            if (!cancelled && !ContainsVolume(host.CaptureAnalysisVolumes()))
            {
                ResetVolume();
                work.RequestRefresh();
                work.RefreshWindowElapsed();
                cancelled = true;
            }
            if (!cancelled) usage = updated;
            if (work.Complete(request, null) && !cancelled)
            {
                if (path is not null && error is not null) ReportPathError(requestId, error);
                else
                {
                    if (result is not null)
                    {
                        trail = result.Breadcrumbs.Select(crumb => crumb.Id).ToArray();
                        displayedVolumeKey = volumeKey;
                        selection = (requestId, root, directory, null, volumeKey);
                    }
                    Publish(requestId, roots, root, result, error);
                }
            }
            TryStart();
        });
    }

    private void ReportPathError(long requestId, string message)
    {
        // 桌面可能已忽略后端发出的旧结果，失败时从提交地址时实际显示的目录恢复。
        displayedVolumeKey = pathOrigin.VolumeKey;
        trail = pathOrigin.BreadcrumbIds;
        selection = (requestId, pathOrigin.Root, trail.LastOrDefault(), null, pathOrigin.VolumeKey);
        publish(new { Type = "spacePathError", RequestId = requestId, Message = message });
        // 路径查找可能已读到最新索引，但当前树图尚未使用这个版本。
        work.RequestUser();
        TryStart();
    }

    private void Publish(long requestId, string[] roots, string? root, SpaceFolder? folder, string? error)
        => publish(new
        {
            Type = "spaceAnalysis", RequestId = requestId, Roots = roots, Root = root, VolumeKey = volumeKey, Error = error,
            Folder = folder is null ? null : new
            {
                folder.Path,
                Breadcrumbs = folder.Breadcrumbs.Select(crumb => new { Id = crumb.Id.ToString("X16"), crumb.Name }),
                Bytes = folder.Bytes.ToString(CultureInfo.InvariantCulture), SizeText = FileSizeDisplay.Format(folder.Bytes),
                SizeBytesText = $"{folder.Bytes:N0} B", folder.UnknownFiles, folder.IsEmpty,
                Children = folder.Children.Select(item => new
                {
                    DirectoryId = item.DirectoryId?.ToString("X16"), item.Name, item.Count,
                    Key = item.Path is { } path ? DesktopFileActions.EncodePath(path) : null,
                    Bytes = item.Bytes.ToString(CultureInfo.InvariantCulture), SizeText = FileSizeDisplay.Format(item.Bytes),
                    SizeBytesText = $"{item.Bytes:N0} B"
                })
            }
        });

    private bool ContainsVolume((NtfsVolume Volume, IReadOnlyList<SearchIndex> Layers)[] views)
        => views.Any(view => VolumeCache.Key(view.Volume) == volumeKey);

    private static bool SameRoot(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static ulong ParseDirectoryId(string id)
        => ulong.TryParse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong directory) ? directory : 0;
}
