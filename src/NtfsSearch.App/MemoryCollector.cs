using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class MemoryCollector
{
    internal static async Task<int> RunAsync(string name, int parentPid)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 5;
        using var lifetime = new CancellationTokenSource();
        using var pipe = await MemoryCollectorPipe.ConnectAsync(name, parentPid, lifetime.Token);
        var control = new CollectorFrameReader(pipe);
        var jobs = new List<Task>();
        try
        {
            while (true)
            {
                var request = MemoryCollectorRequest.Read(await control.ReadAsync(lifetime.Token));
                jobs.RemoveAll(job => job.IsCompleted);
                jobs.Add(Task.Run(() => HandleVolumeAsync(request, parentPid, lifetime.Token)));
            }
        }
        catch (EndOfStreamException) { return 0; }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(jobs).WaitAsync(TimeSpan.FromSeconds(8)); }
            catch (TimeoutException) { /* 驱动若挂起，离开入口以结束进程。 */ }
        }
    }

    private static async Task HandleVolumeAsync(MemoryCollectorRequest request, int parentPid, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken work = lifetime.Token;
        try
        {
            using var journal = await MemoryCollectorPipe.ConnectAsync(request.JournalPipe, parentPid, work);
            using var scan = request.ScanPipe is { } scanName
                ? await MemoryCollectorPipe.ConnectAsync(scanName, parentPid, work) : null;
            TaskCompletionSource? fence = scan is null ? null : new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task closed = CancelWhenClosedAsync(journal, lifetime, fence: fence);
            var writer = new CollectorFrameWriter(journal);
            try
            {
                EnsureCurrentVolume(request.Volume);
                char drive = request.Volume.Root[0];
                var info = NativeJournal.Query(drive, createIfMissing: scan is not null);
                var start = request.Resume ?? info.Position;
                info.Validate(start);

                // 在扫描开始前固定USN起点，同时发送两个相互独立且有背压的数据流。
                await writer.WriteAsync(CollectorFrameKind.JournalStart, body =>
                {
                    CollectorWire.WritePosition(body, start);
                    CollectorWire.WritePosition(body, info.Position);
                }, work);
                Task watch = Task.Run(() => PumpJournalAsync(request, start, writer, fence?.Task, work));
                Task scanning = scan is null ? Task.CompletedTask : RelayScanAsync(request, start, scan, work);
                await Task.WhenAll(watch, scanning);
            }
            catch (Exception error) when (IsCollectionError(error) && !work.IsCancellationRequested)
            { await CollectorWire.WriteErrorAsync(writer, error, VolumeJournalService.RequiresRebuild(error), work); }
            finally
            {
                await lifetime.CancelAsync();
                await closed;
            }
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested) { }
        catch (Exception error) when (IsCollectionError(error)) { /* 对端关闭后无连接可供报告。 */ }
    }

    private static async Task PumpJournalAsync(MemoryCollectorRequest request, JournalPosition position,
        CollectorFrameWriter writer, Task? fence, CancellationToken token)
    {
        char drive = request.Volume.Root[0];
        try
        {
            using var volume = NativeJournal.Open(drive);
            byte[] buffer = new byte[1 << 20];
            void WaitForState(Exception _)
            {
                EnsureCurrentVolume(request.Volume);
                NativeJournal.Query(volume).Validate(position);
            }
            var resolver = VolumeJournalService.CreateResolver(request.DataDirectory, drive, reference =>
                VolumeJournalService.ReadCurrentWithRetry(
                    () => NativeFileState.Read(volume, reference, id => RawMftState.Read(drive, id)), token, WaitForState));
            long sequence = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (fence?.IsCompletedSuccessfully == true)
                {
                    // 后端建好四列排序后取一次水位。必须确认它之前的所有批次仍可连续交付。
                    var current = NativeJournal.Query(volume);
                    current.Validate(position);
                    await writer.WriteAsync(CollectorFrameKind.JournalFence, body => CollectorWire.WritePosition(body, current.Position), token);
                    fence = null;
                }
                var batch = NativeJournal.Read(volume, position, outputBuffer: buffer);
                if (batch.To == position)
                {
                    await Task.Delay(100, token);
                    continue;
                }
                EnsureCurrentVolume(request.Volume);
                var transaction = resolver.Resolve(batch, position, token);
                foreach (var part in VolumeJournalService.SplitResolved(transaction, resolver, drive, token))
                    await writer.WriteAsync(CollectorFrameKind.Changes, ChangeLogStore.Encode(new(++sequence, part)), token);
                position = batch.To;
            }
        }
        catch (Exception error) when (IsCollectionError(error) && !token.IsCancellationRequested)
        { await CollectorWire.WriteErrorAsync(writer, error, VolumeJournalService.RequiresRebuild(error), token); }
    }

    private static async Task RelayScanAsync(MemoryCollectorRequest request, JournalPosition start, PipeStream destination,
        CancellationToken token)
    {
        var output = new CollectorFrameWriter(destination);
        try
        {
            string name = MemoryCollectorPipe.NewName();
            using var input = MemoryCollectorPipe.CreateServer(name, acrossElevation: false);
            using var child = AppProcess.Start(["--memory-scan", name, Environment.ProcessId.ToString(CultureInfo.InvariantCulture)],
                elevate: false, processorLimit: request.WorkerProcessors);
            try
            {
                try { child.PriorityClass = ProcessPriorityClass.BelowNormal; }
                catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
                await MemoryCollectorPipe.AcceptAsync(input, child.Id, token);
                var command = new CollectorFrameWriter(input);
                await command.WriteAsync(CollectorFrameKind.OpenScan, body =>
                {
                    CollectorWire.WriteVolume(body, request.Volume);
                    body.Write(request.DataDirectory);
                    CollectorWire.WritePosition(body, start);
                }, token);
                await output.WriteAsync(CollectorFrameKind.WorkerStarted, body => body.Write(child.Id), token);
                await input.CopyToAsync(destination, 64 << 10, token);
            }
            finally
            {
                if (!child.HasExited) child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (Exception error) when (IsCollectionError(error) && !token.IsCancellationRequested)
        { await CollectorWire.WriteErrorAsync(output, error, VolumeJournalService.RequiresRebuild(error), token); }
        finally { await destination.DisposeAsync(); }
    }

    internal static async Task<int> RunScanAsync(string name, int parentPid)
    {
        using var lifetime = new CancellationTokenSource();
        using var pipe = await MemoryCollectorPipe.ConnectAsync(name, parentPid, lifetime.Token);
        var writer = new CollectorFrameWriter(pipe);
        using var request = (await new CollectorFrameReader(pipe).ReadAsync(lifetime.Token)).Open(CollectorFrameKind.OpenScan);
        var volume = CollectorWire.ReadVolume(request);
        string data = request.ReadString();
        var start = CollectorWire.ReadPosition(request);
        var closed = CancelWhenClosedAsync(pipe, lifetime, forceExit: true);
        try
        {
            EnsureCurrentVolume(volume);
            var output = new MemoryScanWriter(pipe, lifetime.Token);
            var progress = new ThrottledProgress(output.Progress);
            long skipped = 0;
            NtfsEnumerator.ReadRows(volume.Root[0], output.Start, output.Add, lifetime.Token, progress, value => skipped = value,
                NativeJournal.CanonicalPathForMissingPath(data).TrimEnd('\\'));
            output.Finish();
            EnsureCurrentVolume(volume);
            var target = NativeJournal.Query(volume.Root[0]).Position;
            if (target.JournalId != start.JournalId || target.NextUsn < start.NextUsn)
                throw new JournalInvalidatedException("初扫期间USN日志已更换，需重新读取本卷。");
            await writer.WriteAsync(CollectorFrameKind.ScanEnd, body =>
            {
                CollectorWire.WritePosition(body, target);
                body.Write(skipped);
            }, lifetime.Token);
            return 0;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return 130; }
        catch (Exception error) when (IsCollectionError(error))
        {
            await CollectorWire.WriteErrorAsync(writer, error, VolumeJournalService.RequiresRebuild(error), lifetime.Token);
            return 1;
        }
        finally
        {
            await lifetime.CancelAsync();
            await closed;
        }
    }

    private static async Task CancelWhenClosedAsync(PipeStream pipe, CancellationTokenSource lifetime,
        bool forceExit = false, TaskCompletionSource? fence = null)
    {
        if (fence is not null)
        {
            try
            {
                await pipe.ReadExactlyAsync(new byte[1], lifetime.Token);
                fence.TrySetResult();
            }
            catch (IOException) { }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
        await MemoryCollectorPipe.WaitUntilClosedAsync(pipe, lifetime.Token);
        if (lifetime.IsCancellationRequested) return;
        if (forceExit)
            _ = Task.Run(async () => { await Task.Delay(TimeSpan.FromSeconds(8)); Environment.Exit(130); });
        await lifetime.CancelAsync();
    }

    private static void EnsureCurrentVolume(NtfsVolume request)
    {
        if (!string.Equals(VolumeDiscovery.IdentityAtRoot(request.Root), request.Id, StringComparison.OrdinalIgnoreCase))
            throw new IOException("磁盘卷已移除或盘符指向其他卷。");
    }

    internal static bool IsCollectionError(Exception error)
        => error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception or TimeoutException or ArgumentException;

    private sealed class ThrottledProgress(Action<string> report) : IProgress<string>
    {
        private long last;
        public void Report(string value)
        {
            long now = Stopwatch.GetTimestamp();
            if (now - last < Stopwatch.Frequency / 2) return;
            last = now;
            report(value);
        }
    }
}
