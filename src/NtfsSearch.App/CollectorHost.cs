using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed record CollectorProgress(string Message, DateTime UpdatedUtc);

internal static class CollectorHost
{
    public static bool IsRunning(string root)
    {
        try { using var file = new FileStream(Path.Combine(root, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); return false; }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return true; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[2], out int parentId)
            || !long.TryParse(args[3], out long parentStarted) || !Guid.TryParseExact(args[4], "N", out _)) return 2;
        string root = Path.GetFullPath(args[1]), session = args[4];
        Directory.CreateDirectory(root);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        using var lifetime = new CancellationTokenSource();
        var monitor = MonitorParentAsync(parentId, parentStarted, Path.Combine(root, session + ".stop"), lifetime);
        try
        {
            return args[0] == "--collect" ? await CollectAsync(root, lifetime.Token) : 2;
        }
        catch (OperationCanceledException) { return 130; }
        finally
        {
            await lifetime.CancelAsync(); await monitor;
            try { File.Delete(Path.Combine(root, session + ".stop")); } catch (IOException) { }
        }
    }

    private static async Task<int> CollectAsync(string root, CancellationToken token)
    {
        FileStream ownership;
        try { ownership = new(Path.Combine(root, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return 0; }
        using (ownership)
        {
            var scanned = new HashSet<string>(StringComparer.Ordinal);
            var repairs = new Dictionary<string, string>(StringComparer.Ordinal);
            string? refresh = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string request = Path.Combine(root, "refresh.request.json");
                string? nextRefresh = File.Exists(request) ? AtomicJson.ReadText(request) : null;
                if (refresh != nextRefresh) { scanned.Clear(); refresh = nextRefresh; }
                var volumes = VolumeDiscovery.Discover();
                scanned.IntersectWith(volumes.Select(VolumeCache.Key));
                foreach (var volume in volumes.Where(v => !scanned.Contains(VolumeCache.Key(v))))
                {
                    try { AtomicJson.Write(Path.Combine(root, VolumeCache.Key(volume) + ".progress.json"), new CollectorProgress("排队读取…", DateTime.UtcNow)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
                foreach (var volume in volumes)
                {
                    token.ThrowIfCancellationRequested();
                    string key = VolumeCache.Key(volume);
                    try
                    {
                        string repairPath = Path.Combine(root, key + ".rebuild.json");
                        if (File.Exists(repairPath))
                        {
                            string repair = AtomicJson.ReadText(repairPath);
                            if (!repairs.TryGetValue(key, out string? previous) || previous != repair)
                            { repairs[key] = repair; scanned.Remove(key); }
                        }
                        if (!scanned.Add(key)) continue;
                        await ScanInChildAsync(root, volume, token);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                    {
                        try { AtomicJson.Write(Path.Combine(root, key + ".progress.json"), new CollectorProgress(error.Message, DateTime.UtcNow)); }
                        catch (IOException) { }
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            }
        }
    }

    private static async Task ScanInChildAsync(string root, NtfsVolume volume, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string state = Path.Combine(root, VolumeCache.Key(volume) + ".progress.json");
        void Report(string message) => AtomicJson.Write(state, new CollectorProgress(message, DateTime.UtcNow));
        Report("准备读取 NTFS…");
        using var parent = Process.GetCurrentProcess();
        using var child = AppProcess.Start(["--scan-volume", root,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(volume))),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)], elevate: false);
        var timer = Stopwatch.StartNew();
        try
        {
            while (!child.HasExited)
            {
                token.ThrowIfCancellationRequested();
                if (timer.Elapsed > TimeSpan.FromMinutes(30)) throw new TimeoutException("本卷扫描超过30分钟，已停止；其余磁盘继续采集。");
                await Task.Delay(500, token);
            }
            if (child.ExitCode != 0 && child.ExitCode != 130)
            {
                string detail = File.Exists(state) ? JsonSerializer.Deserialize<CollectorProgress>(AtomicJson.ReadText(state))?.Message ?? "" : "";
                Report($"本卷采集失败：{detail}（退出码 {child.ExitCode}）；其他磁盘继续处理。");
            }
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            Report("采集已取消，已有索引继续可用。"); throw;
        }
        catch (TimeoutException e)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            Report(e.Message);
        }
    }

    public static async Task<int> ScanVolumeAsync(string[] args)
    {
        if (args.Length != 5 || !int.TryParse(args[3], out int parentId) || !long.TryParse(args[4], out long parentStarted)) return 2;
        string root = Path.GetFullPath(args[1]);
        var volume = JsonSerializer.Deserialize<NtfsVolume>(Encoding.UTF8.GetString(Convert.FromBase64String(args[2])))
            ?? throw new InvalidDataException("卷参数无效。");
        if (!VolumeDiscovery.Discover().Any(v => v.Id == volume.Id && v.Root == volume.Root)) return 3;
        using var lifetime = new CancellationTokenSource();
        var monitor = MonitorParentAsync(parentId, parentStarted, null, lifetime);
        string state = Path.Combine(root, VolumeCache.Key(volume) + ".progress.json");
        long lastReport = 0;
        void Report(string message)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - lastReport < Stopwatch.Frequency / 2) return;
            lastReport = now;
            AtomicJson.Write(state, new CollectorProgress(message, DateTime.UtcNow));
        }
        try
        {
            long skipped = 0;
            var index = await Task.Run(() => NtfsEnumerator.Scan(volume.Root[0], lifetime.Token, new InlineProgress(Report), count => skipped = count), lifetime.Token);
            AtomicJson.Write(state, new CollectorProgress("保存本卷索引…", DateTime.UtcNow));
            new VolumeCache(root).Publish(volume, index, lifetime.Token);
            AtomicJson.Write(state, new CollectorProgress($"已读取 {index.Count:N0} 个对象" + (skipped > 0 ? $"；{skipped:N0} 条记录路径不可达，未纳入搜索" : ""), DateTime.UtcNow));
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AtomicJson.Write(state, new CollectorProgress(error.Message, DateTime.UtcNow)); return 1;
        }
        finally { await lifetime.CancelAsync(); await monitor; }
    }

    private static async Task MonitorParentAsync(int parentId, long started, string? stopFile, CancellationTokenSource lifetime)
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool alive;
                try { using var parent = Process.GetProcessById(parentId); alive = !parent.HasExited && parent.StartTime.ToUniversalTime().Ticks == started; }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { alive = false; }
                if (!alive || stopFile is not null && File.Exists(stopFile))
                {
                    await lifetime.CancelAsync();
                    // 父进程消失后，即使驱动调用不响应取消，也不留下孤立采集进程。
                    _ = Task.Run(async () => { await Task.Delay(10_000); Environment.Exit(130); });
                    return;
                }
                await Task.Delay(500, lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    { public void Report(string value) => report(value); }
}
