using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.App;

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
            return args[0] == "--collect" ? await CollectAsync(root, session, lifetime.Token) : 2;
        }
        catch (OperationCanceledException) { return 130; }
        finally
        {
            await lifetime.CancelAsync(); await monitor;
            try { File.Delete(Path.Combine(root, session + ".stop")); } catch (IOException) { }
        }
    }

    private static async Task<int> CollectAsync(string root, string session, CancellationToken token)
    {
        FileStream ownership;
        try { ownership = new(Path.Combine(root, "collector.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { return 0; }
        using (ownership)
        {
            var policy = IndexingConcurrencyPolicy.Create(Environment.ProcessorCount, VolumeResources.AvailableMemoryBytes());
            var metrics = new CollectorMetricsWriter(root, session, policy); metrics.Initialize();
            var cache = new VolumeCache(root);
            await using var watches = new VolumeWatchCoordinator(root, token);
            var estimates = new Dictionary<string, long>(StringComparer.Ordinal);
            var announced = new Dictionary<string, string>(StringComparer.Ordinal);
            await using var scheduler = new VolumeWorkScheduler(policy.ScanLimit, policy.MemoryBudget, async (work, cancellation) =>
            {
                try { await ScanInChildAsync(root, work.Volume, cancellation, policy.WorkerProcessors, metrics); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                {
                    try { AtomicJson.Write(Path.Combine(root, VolumeCache.Key(work.Volume) + ".progress.json"), new CollectorProgress(error.Message, DateTime.UtcNow)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            });
            DateTime nextDiscovery = DateTime.MinValue;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (DateTime.UtcNow >= nextDiscovery)
                {
                    string request = Path.Combine(root, "refresh.request.json");
                    string refresh = File.Exists(request) ? AtomicJson.ReadText(request) : "initial";
                    var volumes = VolumeDiscovery.Discover();
                    await watches.RemoveExceptAsync(volumes);
                    var work = new List<VolumeWork>();
                    foreach (var volume in volumes)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            string key = VolumeCache.Key(volume);
                            string repairPath = Path.Combine(root, key + ".rebuild.json");
                            var manifest = cache.CurrentManifest(volume);
                            bool refreshNeeded = manifest is null
                                || File.Exists(request) && File.GetLastWriteTimeUtc(request) > manifest.IndexedUtc
                                || File.Exists(repairPath) && File.GetLastWriteTimeUtc(repairPath) > manifest.IndexedUtc;
                            if (manifest?.Journal is not null && !refreshNeeded)
                            {
                                await watches.EnsureAsync(volume, manifest);
                                // 清单先于子进程退出提交。保留已发布任务直到正常收尾，
                                // 避免调度器把成功发布误判为移除并取消进程。
                                if (scheduler.ActiveRoots.Contains(volume.Root)
                                    && announced.TryGetValue(key, out string? finishing)
                                    && estimates.TryGetValue(key, out long reserved))
                                    work.Add(new(volume, finishing, reserved));
                                continue;
                            }
                            string revision = refresh + "|" + (File.Exists(repairPath) ? AtomicJson.ReadText(repairPath) : "");
                            bool changed = !announced.TryGetValue(key, out string? previous) || previous != revision;
                            if (!estimates.TryGetValue(key, out long estimate) || changed)
                                estimates[key] = estimate = VolumeResources.EstimateBuildBytes(volume.Root[0], cache.CachedSnapshotBytes(volume));
                            work.Add(new(volume, revision, estimate));
                            if (changed)
                            {
                                announced[key] = revision;
                                if (!scheduler.ActiveRoots.Contains(volume.Root))
                                {
                                    try { AtomicJson.Write(Path.Combine(root, key + ".progress.json"), new CollectorProgress("排队读取…", DateTime.UtcNow)); }
                                    catch (IOException) { }
                                    catch (UnauthorizedAccessException) { }
                                }
                            }
                        }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
                        {
                            try { AtomicJson.Write(Path.Combine(root, VolumeCache.Key(volume) + ".progress.json"), new CollectorProgress(error.Message, DateTime.UtcNow)); }
                            catch (IOException) { }
                            catch (UnauthorizedAccessException) { }
                        }
                    }
                    var keys = volumes.Select(VolumeCache.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (string key in announced.Keys.Where(k => !keys.Contains(k)).ToArray()) { announced.Remove(key); estimates.Remove(key); }
                    scheduler.Reconcile(work);
                    nextDiscovery = DateTime.UtcNow.AddSeconds(2);
                }
                // 保留系统余量；已经运行的任务继续完成，内存紧张时停止接纳新任务。
                scheduler.Pump(token, allowNew: VolumeResources.AvailableMemoryBytes() >= Math.Max(1, policy.MemoryBudget / 2));
                await Task.Delay(200, token);
            }
        }
    }

    private static async Task ScanInChildAsync(string root, NtfsVolume volume, CancellationToken token, int processors, CollectorMetricsWriter metrics)
    {
        token.ThrowIfCancellationRequested();
        string state = Path.Combine(root, VolumeCache.Key(volume) + ".progress.json");
        void Report(string message, string? details = null) => AtomicJson.Write(state,
            new CollectorProgress(message, DateTime.UtcNow) { Details = details });
        Report("准备读取 NTFS…");
        using var parent = Process.GetCurrentProcess();
        using var child = AppProcess.Start(["--scan-volume", root,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(volume))),
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture), parent.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture)], elevate: false, processorLimit: processors);
        try { child.PriorityClass = ProcessPriorityClass.BelowNormal; }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException) { }
        if (!child.HasExited) metrics.Started(volume, child.Id);
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
                var failure = File.Exists(state) ? JsonSerializer.Deserialize<CollectorProgress>(AtomicJson.ReadText(state)) : null;
                Report($"本卷采集失败：{failure?.Message}（退出码 {child.ExitCode}）；其他磁盘继续处理。", failure?.Details);
            }
        }
        catch (OperationCanceledException)
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Report("采集已取消，已有索引继续可用。"); throw;
        }
        catch (TimeoutException e)
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
            Report(e.Message);
        }
        finally { metrics.Stopped(volume); }
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
            var scan = await Task.Run(() => NtfsEnumerator.ScanWithJournal(volume.Root[0], Path.Combine(root, "Capture"), lifetime.Token,
                new InlineProgress(Report), count => skipped = count, excludeDirectory: root), lifetime.Token);
            using var capture = scan.Capture;
            var index = scan.Index;
            AtomicJson.Write(state, new CollectorProgress("保存本卷索引…", DateTime.UtcNow));
            using var publication = new VolumeCache(root).BeginPublication(volume, index, scan.Position, lifetime.Token);
            using var log = new ChangeLogStore(root, publication.Manifest.SnapshotFile, scan.Position, writable: true);
            var resolver = VolumeJournalService.CreateResolver(root, volume.Root[0]);
            JournalPosition safe = scan.Position;
            void ApplyCaptured(JournalBatch batch)
            {
                var transaction = resolver.Resolve(batch, log.Checkpoint.Position, lifetime.Token);
                safe = batch.To;
                if (transaction.Files.Count > 0) VolumeJournalService.PersistResolved(log, transaction, resolver, volume.Root[0], lifetime.Token);
            }
            AtomicJson.Write(state, new CollectorProgress("回放扫描期间的变化…", DateTime.UtcNow));
            while (capture.ReadCaptured() is { } batch) ApplyCaptured(batch);
            capture.Stop();
            while (capture.ReadCaptured() is { } batch) ApplyCaptured(batch);
            if (safe != log.Checkpoint.Position) log.Append(new(log.Checkpoint.Position, safe, []), lifetime.Token);
            NativeJournal.Query(volume.Root[0]).Validate(log.Checkpoint.Position);
            publication.Commit(lifetime.Token);
            AtomicJson.Write(state, new CollectorProgress($"已读取 {index.Count:N0} 个对象" + (skipped > 0 ? $"；{skipped:N0} 条记录路径不可达，未纳入搜索" : ""), DateTime.UtcNow));
            return 0;
        }
        catch (OperationCanceledException) { return 130; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            AtomicJson.Write(state, new CollectorProgress(error.Message, DateTime.UtcNow) { Details = error.ToString() }); return 1;
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
