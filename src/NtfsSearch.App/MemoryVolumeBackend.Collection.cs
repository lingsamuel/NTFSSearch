using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Threading.Channels;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal sealed partial class MemoryVolumeBackend
{
    private async Task ScanAsync(VolumeWork work, CancellationToken token)
    {
        int generation = int.Parse(work.Revision, CultureInfo.InvariantCulture);
        VolumeState volume;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (gate)
        {
            if (!volumes.TryGetValue(VolumeCache.Key(work.Volume), out var current) || current.Revision != generation) return;
            volume = current;
            volume.ScanStop = lifetime;
        }
        CancellationToken scanToken = lifetime.Token;
        lifetime.CancelAfter(TimeSpan.FromMinutes(30));

        try
        {
            var connection = Volatile.Read(ref collector) ?? throw new IOException("采集进程的控制连接已关闭。");
            Report(volume, generation, new("backend.index.preparingScan"));
            string journalName = MemoryCollectorPipe.NewName(), scanName = MemoryCollectorPipe.NewName();
            using var journal = MemoryCollectorPipe.CreateServer(journalName, acrossElevation: true);
            using var scan = MemoryCollectorPipe.CreateServer(scanName, acrossElevation: true);
            await connection.RequestAsync(new(volume.Volume, dataDirectory, journalName, scanName, null,
                policy.WorkerProcessors, metadata), scanToken);
            await Task.WhenAll(MemoryCollectorPipe.AcceptAsync(journal, connection.ProcessId, scanToken),
                MemoryCollectorPipe.AcceptAsync(scan, connection.ProcessId, scanToken));

            // 扫描和建排序期间只保留少量USN帧。队列满后暂停读管道，采集器随后从NTFS继续读取。
            var buffered = Channel.CreateBounded<CollectorFrame>(new BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
            using var streams = CancellationTokenSource.CreateLinkedTokenSource(scanToken);
            Task intake = BufferJournalAsync(journal, buffered.Writer, streams.Token);
            var baseline = ReadScanAsync(scan, volume, generation, streams.Token);
            try
            {
                if (await Task.WhenAny(baseline, intake) == intake) await intake;
                var (index, target, skipped) = await baseline;
                var (start, _) = ReadStart(await ReadBufferedAsync(buffered.Reader, scanToken));
                if (target.JournalId != start.JournalId || target.NextUsn < start.NextUsn)
                    throw new JournalInvalidatedException("扫描期间USN日志已更换。");

                var live = new LiveVolumeIndex(index, start, scanToken);
                Report(volume, generation, new("backend.index.replayingChanges"));
                await journal.WriteAsync(new byte[] { 1 }, scanToken);
                JournalPosition? fence = null;
                while (fence is null || live.Position!.NextUsn < fence.NextUsn)
                {
                    var frame = await ReadBufferedAsync(buffered.Reader, scanToken);
                    if (frame.Kind == CollectorFrameKind.JournalFence)
                    {
                        using var body = frame.Open();
                        fence = CollectorWire.ReadPosition(body);
                        if (fence.JournalId != target.JournalId || fence.NextUsn < target.NextUsn)
                            throw new JournalInvalidatedException("回放期间USN日志已更换。");
                    }
                    else ApplyFrame(live, frame, scanToken);
                }
                if (intake.IsFaulted) await intake;
                live.CompactIfNeeded(scanToken);
                scanToken.ThrowIfCancellationRequested();
                long count = live.Layers.Sum(layer => (long)layer.VisibleCount);
                Publish(volume, generation, live, changed: true, scanned: true,
                    status: skipped > 0 ? new("backend.index.scanFinishedWithSkipped", ("count", count), ("skipped", skipped))
                        : new("backend.index.scanFinished", ("count", count)));
            }
            finally
            {
                await streams.CancelAsync();
                try { await Task.WhenAll(baseline, intake); }
                catch (Exception error) when (error is OperationCanceledException || MemoryCollector.IsCollectionError(error)) { }
            }
        }
        catch (OperationCanceledException) when (scanToken.IsCancellationRequested)
        {
            if (!token.IsCancellationRequested)
                ScanFailed(volume, generation, new("backend.index.scanTimeout"));
        }
        catch (Exception error) when (MemoryCollector.IsCollectionError(error))
        {
            ScanFailed(volume, generation, new("backend.index.scanFailed", ("detail", error.Message)));
        }
        finally
        {
            lock (gate)
            {
                volume.ScanPid = null;
                volume.ScanStop = null;
            }
            StatusChanged?.Invoke();
        }
    }

    private async Task<(SearchIndex Index, JournalPosition Target, long Skipped)> ReadScanAsync(PipeStream scan,
        VolumeState volume, int generation, CancellationToken token)
    {
        var frames = new CollectorFrameReader(scan);
        MemoryScanReader? records = null;
        while (true)
        {
            var frame = await frames.ReadAsync(token);
            switch (frame.Kind)
            {
                case CollectorFrameKind.Error:
                    throw CollectorWire.ReadError(frame);
                case CollectorFrameKind.Progress:
                    using (var body = frame.Open()) Report(volume, generation, ScanStageText((NtfsScanStage)body.ReadByte(), body.ReadInt64()));
                    break;
                case CollectorFrameKind.WorkerStarted:
                    using (var body = frame.Open()) ScanStarted(volume, generation, body.ReadInt32());
                    break;
                case CollectorFrameKind.ScanStart when records is null:
                    using (var body = frame.Open()) records = new(body.ReadInt32(), metadata, precomputeSort: false);
                    break;
                case CollectorFrameKind.ScanRows when records is not null:
                    records.ReadRows(frame, token);
                    break;
                case CollectorFrameKind.ScanEnd when records is not null:
                    using (var body = frame.Open())
                    {
                        var target = CollectorWire.ReadPosition(body);
                        var skipped = body.ReadInt64();
                        return (records.Build(token), target, skipped);
                    }
                default:
                    throw new InvalidDataException("扫描消息顺序无效。");
            }
        }
    }

    private static DesktopText ScanStageText(NtfsScanStage stage, long count) => stage switch
    {
        NtfsScanStage.ReadingDirectories => new("backend.index.readingDirectories"),
        NtfsScanStage.RecordsRead => new("backend.index.recordsRead", ("count", count)),
        NtfsScanStage.IndexingNames => new("backend.index.indexingNames"),
        NtfsScanStage.ReadingMetadata => new("backend.index.readingMetadata"),
        NtfsScanStage.RecordsIndexed => new("backend.index.recordsIndexed", ("count", count)),
        _ => throw new InvalidDataException("扫描进度阶段无效。")
    };

    private async Task WatchAsync(MemoryCollectorConnection connection, VolumeState volume, int generation,
        ExitSnapshotVolume initial, DirectoryGraph? graph, CancellationTokenSource lifetime)
    {
        CancellationToken token = lifetime.Token;
        try
        {
            Report(volume, generation, new("backend.index.checkingJournal"));
            string name = MemoryCollectorPipe.NewName();
            using var pipe = MemoryCollectorPipe.CreateServer(name, acrossElevation: true);
            await connection.RequestAsync(new(volume.Volume, dataDirectory, name, null, initial.Position, 0, metadata), token);
            await MemoryCollectorPipe.AcceptAsync(pipe, connection.ProcessId, token);
            var frames = new CollectorFrameReader(pipe);
            var (start, target) = ReadStart(await frames.ReadAsync(token));
            if (start != initial.Position) throw new InvalidDataException("USN续读未从已应用的水位开始。");
            var live = graph is not null ? new LiveVolumeIndex(initial.Layers.ToArray(), graph, 0, start)
                : new LiveVolumeIndex(initial.Layers.Single(), start, token);
            bool ready = start == target;
            if (ready) Publish(volume, generation, live, changed: false, monitoring: true, status: LiveStatus(volume, live));
            else Report(volume, generation, new("backend.index.catchingUpJournal"));
            bool changed = false;
            HashSet<ulong>? fileParents = null;
            SpaceActivityObservation? batchObservation = null;
            while (true)
            {
                var frame = await frames.ReadAsync(token);
                var observation = AnalysisObservation;
                if (!ready || observation?.VolumeKey != volume.Key) observation = null;
                if (!ReferenceEquals(observation, batchObservation))
                {
                    batchObservation = observation;
                    fileParents = observation is null ? null : [];
                }
                var (final, applied) = ApplyFrame(live, frame, token, fileParents);
                changed |= applied;
                if (!final) continue;
                changed |= live.CompactIfNeeded(token);
                bool caughtUp = live.Position!.NextUsn >= target.NextUsn;
                DesktopText? status = caughtUp && !ready ? LiveStatus(volume, live) : null;
                // 一个原始USN批次可拆成多帧。跨帧补采完整后才同时发布视图和水位。
                Publish(volume, generation, live, changed, monitoring: caughtUp, status: status,
                    observation: batchObservation, fileParents: fileParents);
                ready |= caughtUp;
                changed = false;
                fileParents?.Clear();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (MemoryCollector.IsCollectionError(error))
        {
            bool gap = VolumeJournalService.RequiresRebuild(error);
            lock (gate)
            {
                if (!IsCurrent(volume, generation)) return;
                volume.Monitoring = false;
                volume.RetryAt = DateTime.UtcNow.AddSeconds(1);
                if (gap)
                {
                    volume.NeedsScan = true;
                    volume.Confirmed = false;
                    volume.Revision = ++revision;
                }
                volume.Status = new(volume.Volume.Root, new(gap ? "backend.index.journalLost" : "backend.index.monitorFailed",
                    ("detail", error.Message))) { HasError = true };
            }
            StatusChanged?.Invoke();
        }
        finally
        {
            lock (gate)
            {
                volume.WatchStop = null;
                volume.Monitoring = false;
                lifetime.Dispose();
            }
        }
    }

    private static async Task BufferJournalAsync(PipeStream pipe, ChannelWriter<CollectorFrame> output, CancellationToken token)
    {
        var reader = new CollectorFrameReader(pipe);
        try
        {
            while (true)
            {
                var next = await reader.ReadAsync(token);
                if (next.Kind == CollectorFrameKind.Error) throw CollectorWire.ReadError(next);
                await output.WriteAsync(next, token);
            }
        }
        catch (Exception error)
        {
            output.TryComplete(error);
            throw;
        }
    }

    private static async Task<CollectorFrame> ReadBufferedAsync(ChannelReader<CollectorFrame> reader, CancellationToken token)
    {
        if (await reader.WaitToReadAsync(token) && reader.TryRead(out var next)) return next;
        throw new EndOfStreamException("USN采集连接提前关闭。");
    }

    private static (JournalPosition Start, JournalPosition Target) ReadStart(CollectorFrame frame)
    {
        if (frame.Kind == CollectorFrameKind.Error) throw CollectorWire.ReadError(frame);
        using var body = frame.Open(CollectorFrameKind.JournalStart);
        var start = CollectorWire.ReadPosition(body);
        var target = CollectorWire.ReadPosition(body);
        if (start.JournalId != target.JournalId || start.NextUsn > target.NextUsn)
            throw new InvalidDataException("USN起始位置和目标水位不一致。");
        return (start, target);
    }

    private static (bool Final, bool Changed) ApplyFrame(LiveVolumeIndex live, CollectorFrame frame, CancellationToken token,
        HashSet<ulong>? fileParents = null)
    {
        if (frame.Kind == CollectorFrameKind.Error) throw CollectorWire.ReadError(frame);
        if (frame.Kind != CollectorFrameKind.Changes) throw new InvalidDataException("缺少USN事务。");
        var change = ChangeLogStore.Decode(frame.Payload);
        var transaction = change.Transaction;
        if (change.Sequence != live.AppliedSequence + 1 || transaction.From != live.Position)
            throw new InvalidDataException("USN事务序号或水位不连续。");
        if (fileParents is not null) live.CollectFileParents(transaction, fileParents);
        return (transaction.To != transaction.From, live.Apply(change, token));
    }

    private void Publish(VolumeState volume, int generation, LiveVolumeIndex live, bool changed,
        bool scanned = false, bool monitoring = false, DesktopText? status = null,
        SpaceActivityObservation? observation = null, HashSet<ulong>? fileParents = null)
    {
        lock (gate)
        {
            if (!IsCurrent(volume, generation)) return;
            var layers = live.Layers;
            volume.View = new(volume.Volume, live.Position!, layers);
            volume.Graph = live.DirectoryGraph;
            if (scanned || monitoring) volume.Confirmed = true;
            if (scanned) volume.NeedsScan = volume.Refresh = volume.RestoredFromSnapshot = false;
            volume.Monitoring = monitoring;
            if (status is not null) volume.Status = new(volume.Volume.Root, status);
            if (changed) catalog.PublishLayers(volume.Key, layers);
        }
        if (observation is not null && fileParents is { Count: > 0 }) FileActivity?.Invoke(observation, fileParents);
        if (status is not null) StatusChanged?.Invoke();
    }

    private static DesktopText LiveStatus(VolumeState volume, LiveVolumeIndex live)
        => new(volume.RestoredFromSnapshot ? "backend.index.monitoringRestored" : "backend.index.monitoringScanned",
            ("count", live.Layers.Sum(layer => (long)layer.VisibleCount)));

    private void Report(VolumeState volume, int generation, DesktopText message)
    {
        lock (gate)
        {
            if (!IsCurrent(volume, generation)) return;
            volume.Status = new(volume.Volume.Root, message);
        }
        Progress?.Invoke(VolumeProgress(volume, message), false);
        StatusChanged?.Invoke();
    }

    private void ScanStarted(VolumeState volume, int generation, int process)
    {
        lock (gate)
        {
            if (!IsCurrent(volume, generation)) return;
            volume.ScanPid = process;
            scanned.Add(volume.Volume.Root);
            peakScans = Math.Max(peakScans, volumes.Values.Count(v => v.ScanPid.HasValue));
        }
        StatusChanged?.Invoke();
    }

    private void ScanFailed(VolumeState volume, int generation, DesktopText message)
    {
        lock (gate)
        {
            if (!IsCurrent(volume, generation)) return;
            volume.Status = new(volume.Volume.Root, message) { HasError = true };
            volume.Revision = ++revision;
            volume.RetryAt = DateTime.UtcNow.AddSeconds(2);
        }
        Progress?.Invoke(VolumeProgress(volume, message), true);
        StatusChanged?.Invoke();
    }

    private static DesktopText VolumeProgress(VolumeState volume, DesktopText message)
        => new("backend.index.volumeProgress", ("root", volume.Volume.Root), ("message", message));
}
