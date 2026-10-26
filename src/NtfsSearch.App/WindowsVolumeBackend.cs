using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class WindowsVolumeBackend(Dispatcher dispatcher, string? cacheDirectory = null) : IAutomaticVolumeBackend
{
    private readonly VolumeCache cache = new(cacheDirectory ?? PortableDataDirectory.Resolve());
    private readonly LiveIndexFeed liveFeed = new(cacheDirectory ?? PortableDataDirectory.Resolve());
    private readonly object migrationGate = new();
    private Task<CacheMigrationResult>? migration;
    public event Action<LiveIndexUpdate>? LiveUpdated;
    private CollectorMetrics? metrics;
    public CollectorMetrics? Metrics => Volatile.Read(ref metrics);
    public int LiveVolumeCount => liveFeed.ActiveVolumeCount;
    private readonly string session = Guid.NewGuid().ToString("N");
    private Process? launchedCollector;
    private bool expectingCollector, suspended, permissionRefused;
    private int launchAttempts;
    private DateTime nextLaunchUtc;
    public bool CollectorNeedsRestart => expectingCollector && !suspended && !permissionRefused
        && launchAttempts < 3 && DateTime.UtcNow >= nextLaunchUtc
        && (launchedCollector is null || launchedCollector.HasExited) && !CollectorHost.IsRunning(cache.DirectoryPath);
    public event Action<string>? Progress;
    public void RequestRebuild(NtfsVolume volume) => RequestRebuild(volume, "缓存缺失、校验失败或旧格式需要升级。");
    private void RequestRebuild(NtfsVolume volume, string reason) => AtomicJson.Write(Path.Combine(cache.DirectoryPath,
        VolumeCache.Key(volume) + ".rebuild.json"), new { Id = Guid.NewGuid().ToString("N"), Reason = reason, RequestedUtc = DateTime.UtcNow });
    public IReadOnlyList<NtfsVolume> Discover()
    {
        try
        {
            string path = Path.Combine(cache.DirectoryPath, "collector-metrics.json");
            if (File.Exists(path) && JsonSerializer.Deserialize<CollectorMetrics>(AtomicJson.ReadText(path)) is
                { ActiveRoots: not null, ActiveProcesses: not null } current
                && Guid.TryParseExact(current.Session, "N", out _) && current.ScanLimit > 0 && current.WorkerProcessors > 0
                && current.PeakProcesses >= 0 && current.ActiveRoots.Length == current.ActiveProcesses.Length)
                Volatile.Write(ref metrics, current);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { }
        var volumes = VolumeDiscovery.Discover();
        liveFeed.RemoveExcept(volumes);
        return volumes;
    }
    public string CacheRevision(NtfsVolume volume) => cache.Revision(volume);
    public async Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token)
    {
        if (cacheDirectory is null)
        {
            Task<CacheMigrationResult> pending;
            lock (migrationGate)
            {
                if (migration is { IsFaulted: true } or { IsCanceled: true }) migration = null;
                pending = migration ??= MigrateLegacyAsync(token);
            }
            await pending;
        }
        var loaded = await Task.Run(() => cache.Load(volume, token,
            new Progress<string>(message => Progress?.Invoke(volume.Root + " " + message))), token);
        if (loaded is null) return null;
        return loaded with { Layers = await liveFeed.AttachAsync(volume, loaded, token) };
    }

    private async Task<CacheMigrationResult> MigrateLegacyAsync(CancellationToken token)
    {
        string marker = Path.Combine(cache.DirectoryPath, "migration.completed.json");
        if (File.Exists(marker)) return new([], false, false);
        var result = await CacheMigration.MigrateAsync(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtfsSearch", "Volumes"), cache.DirectoryPath, token);
        if (result.Volumes.All(v => v.Status != CacheMigrationStatus.Failed))
            AtomicJson.Write(marker, new { CompletedUtc = DateTime.UtcNow, SourceRetained = true });
        return result;
    }

    public Task RunLiveAsync(CancellationToken token)
    {
        liveFeed.Updated += update => LiveUpdated?.Invoke(update);
        liveFeed.Failed += (volume, message) =>
        {
            Progress?.Invoke(volume.Root + " " + message);
            try { RequestRebuild(volume, message); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        };
        return liveFeed.RunAsync(token);
    }

    public void OnPublished(NtfsVolume volume, string? generation) => liveFeed.Activate(volume, generation);

    public async Task EnsureCollectorAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (CollectorHost.IsRunning(cache.DirectoryPath)) { expectingCollector = true; return; }
        if (launchedCollector is not null && !launchedCollector.HasExited) return;
        await dispatcher.InvokeAsync(() =>
        {
            token.ThrowIfCancellationRequested();
            try
            {
                File.Delete(Path.Combine(cache.DirectoryPath, session + ".stop"));
                permissionRefused = false;
                using var parent = Process.GetCurrentProcess();
                launchedCollector?.Dispose();
                launchedCollector = AppProcess.Start(["--collect", cache.DirectoryPath,
                    Environment.ProcessId.ToString(), parent.StartTime.ToUniversalTime().Ticks.ToString(), session], elevate: true);
                launchAttempts++;
                nextLaunchUtc = DateTime.UtcNow.AddSeconds(5 * launchAttempts);
                expectingCollector = true;
                Progress?.Invoke("已启动后台采集，完成的磁盘会自动加入搜索。");
            }
            catch (Win32Exception e) when (e.NativeErrorCode == 1223)
            { permissionRefused = true; throw new UnauthorizedAccessException("Windows 授权已取消。点击“重新扫描磁盘”可重试，已有缓存仍可搜索。"); }
        });
    }

    public string ReadStatus(NtfsVolume volume)
    {
        if (suspended) return "已暂停采集；已有索引仍可查询";
        string path = Path.Combine(cache.DirectoryPath, VolumeCache.Key(volume) + ".progress.json");
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<CollectorProgress>(AtomicJson.ReadText(path))?.Message ?? "等待后台采集";
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return "等待后台采集";
    }

    public void RequestRefresh() { suspended = false; launchAttempts = 0; AtomicJson.Write(Path.Combine(cache.DirectoryPath, "refresh.request.json"), new { Id = Guid.NewGuid().ToString("N") }); }
    public void CancelCollection() { suspended = true; File.WriteAllText(Path.Combine(cache.DirectoryPath, session + ".stop"), "stop"); }
}
