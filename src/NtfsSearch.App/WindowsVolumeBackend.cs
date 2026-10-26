using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed class WindowsVolumeBackend(Dispatcher dispatcher) : IAutomaticVolumeBackend
{
    private readonly VolumeCache cache = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NtfsSearch", "Volumes"));
    private readonly string session = Guid.NewGuid().ToString("N");
    private Process? launchedCollector;
    private bool expectingCollector, suspended, permissionRefused;
    private int launchAttempts;
    private DateTime nextLaunchUtc;
    public bool CollectorNeedsRestart => expectingCollector && !suspended && !permissionRefused
        && launchAttempts < 3 && DateTime.UtcNow >= nextLaunchUtc
        && (launchedCollector is null || launchedCollector.HasExited) && !CollectorHost.IsRunning(cache.DirectoryPath);
    public event Action<string>? Progress;
    public void RequestRebuild(NtfsVolume volume) => AtomicJson.Write(Path.Combine(cache.DirectoryPath,
        VolumeCache.Key(volume) + ".rebuild.json"), new { Id = Guid.NewGuid().ToString("N") });
    public IReadOnlyList<NtfsVolume> Discover() => VolumeDiscovery.Discover();
    public string CacheRevision(NtfsVolume volume) => cache.Revision(volume);
    public Task<CachedVolume?> LoadAsync(NtfsVolume volume, CancellationToken token) => Task.Run(() =>
        cache.Load(volume, token, new Progress<string>(message => Progress?.Invoke(volume.Root + " " + message))), token);

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
