using System.Globalization;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 与桌面消息循环同步安排工作；同一时间只有一个任务能读取或更新汇总。
internal sealed class SpaceAnalysisSession(SearchHost host, Action<object> publish, Action<Action> post)
{
    private const int VisibleItems = 200;
    private readonly QueryRefreshState work = new();
    private (long RequestId, string Root, ulong Directory)? selection;
    private long latestRequestId;
    private SpaceUsageIndex? usage;
    private SpaceBreadcrumb[] trail = [];
    private string[] lastRoots = [];
    private IReadOnlyList<SearchIndex>? lastLayers;
    private string? volumeKey;
    private string? activeRoot;
    private CancellationTokenSource? cancellation;
    private Task? task;

    internal void View(long requestId, string root, string directoryId)
    {
        if (requestId < latestRequestId) return;
        latestRequestId = requestId;
        ulong.TryParse(directoryId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong directory);
        selection = (requestId, root, directory);
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
        trail = [];
        usage = null;
    }

    private void TryStart((NtfsVolume Volume, IReadOnlyList<SearchIndex> Layers)[]? views = null)
    {
        if (selection is not { } target || !work.TryStart(out var request)) return;
        views ??= host.CaptureAnalysisVolumes();
        var ordered = views.OrderBy(view => view.Volume.Root, StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = ordered.FirstOrDefault(view => SameRoot(view.Volume.Root, target.Root));
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
            Publish(target.RequestId, roots, null, null, null);
            return;
        }

        string root = selected.Volume.Root;
        ulong directory = SameRoot(root, target.Root) && (sameVolume || target.Directory == 0) ? target.Directory : 0;
        var source = new CancellationTokenSource();
        activeRoot = root;
        cancellation = source;
        task = ExecuteAsync(request, target.RequestId, roots, root, directory, selected.Layers!, usage, trail, source);
    }

    private async Task ExecuteAsync(QueryRequest request, long requestId, string[] roots, string root, ulong directory,
        IReadOnlyList<SearchIndex> layers, SpaceUsageIndex? previous, SpaceBreadcrumb[] fallback, CancellationTokenSource source)
    {
        SpaceUsageIndex? updated = null;
        SpaceFolder? result = null;
        string? error = null;
        try
        {
            (updated, result) = await Task.Run(() =>
            {
                var token = source.Token;
                var index = previous?.Update(layers, token) ?? SpaceUsageIndex.Create(root, layers, token);
                if (!index.Contains(directory))
                    directory = fallback.Reverse().FirstOrDefault(crumb => index.Contains(crumb.Id))?.Id ?? 0;
                return (index, index.Read(directory, VisibleItems, token));
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
                if (result is not null)
                {
                    trail = result.Breadcrumbs;
                    selection = (requestId, root, directory);
                }
                Publish(requestId, roots, root, result, error);
            }
            TryStart();
        });
    }

    private void Publish(long requestId, string[] roots, string? root, SpaceFolder? folder, string? error)
        => publish(new
        {
            Type = "spaceAnalysis", RequestId = requestId, Roots = roots, Root = root, Error = error,
            Folder = folder is null ? null : new
            {
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
}
