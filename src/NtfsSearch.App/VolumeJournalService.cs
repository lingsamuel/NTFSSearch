using System.IO;
using System.ComponentModel;
using System.Text.Json;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class VolumeJournalService
{
    internal static async Task WatchAsync(string root, NtfsVolume volume, VolumeManifest manifest, CancellationToken token)
    {
        if (manifest.Journal is not { } initial) return;
        string progress = Path.Combine(root, VolumeCache.Key(volume) + ".progress.json");
        void Report(string message, Exception? error = null)
        {
            try { AtomicJson.Write(progress, new CollectorProgress(message, DateTime.UtcNow) { Details = error?.ToString() }); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var store = await OpenWriterAsync(root, manifest, initial, token);
                NativeJournal.Query(volume.Root[0]).Validate(store.Checkpoint.Position);
                using var capture = NativeJournal.Capture(volume.Root[0], Path.Combine(root, "Capture"), token, store.Checkpoint.Position);
                DateTime lastPersist = DateTime.UtcNow, lastStatus = DateTime.MinValue;
                bool retrying = false;
                void Waiting(Exception error)
                {
                    retrying = true;
                    if (DateTime.UtcNow - lastStatus < TimeSpan.FromSeconds(2)) return;
                    Report("变化对象暂时不可读取，保留日志进度并重试：" + error.Message, error); lastStatus = DateTime.UtcNow;
                }
                var resolver = CreateResolver(root, volume.Root[0], reference => ReadCurrentWithRetry(
                    () => NativeJournal.GetCurrentFileStateById(volume.Root[0], reference), token, Waiting));
                Report("实时监控中，已提交变化 " + store.Checkpoint.Sequence);
                JournalPosition safe = store.Checkpoint.Position;
                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        // 当前batch在解析与完整补采成功前始终由本次调用持有，不消费下一个batch。
                        var batch = capture.ReadCaptured();
                        if (batch is null) { await Task.Delay(100, token); continue; }
                        var transaction = resolver.Resolve(batch, store.Checkpoint.Position, token);
                        if (transaction.Files.Count > 0 || DateTime.UtcNow - lastPersist > TimeSpan.FromMinutes(5))
                        {
                            PersistWithRetry(() => PersistResolved(store, transaction, resolver, volume.Root[0], token), token, Waiting);
                            lastPersist = DateTime.UtcNow;
                            if (retrying || DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(2))
                            {
                                Report("实时监控中，已提交变化 " + store.Checkpoint.Sequence);
                                lastStatus = DateTime.UtcNow; retrying = false;
                            }
                            CompactAcknowledged(store);
                        }
                        // 只有解析及全部multipart完成后，才可将这一批作为安全的空推进水位。
                        safe = batch.To;
                    }
                }
                finally { FlushSafePosition(store, safe); }
                return;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or Win32Exception)
            {
                if (token.IsCancellationRequested || new VolumeCache(root).CurrentManifest(volume)?.SnapshotFile != manifest.SnapshotFile) return;
                if (RequiresRebuild(error))
                {
                    Report("增量日志失效，保留已有结果并重建本卷：" + error.Message, error);
                    try { AtomicJson.Write(Path.Combine(root, VolumeCache.Key(volume) + ".rebuild.json"), new { Id = Guid.NewGuid().ToString("N"), Reason = error.ToString(), RequestedUtc = DateTime.UtcNow }); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    return;
                }
                // 缓存IO、卷暂时不可读等错误先重开同代日志；从已提交水位恢复，不以重扫替代重试。
                Report("实时监控暂时不可用，保留已有结果并重试：" + error.Message, error);
                try { await Task.Delay(1000, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            }
        }
    }

    internal static void FlushSafePosition(ChangeLogStore store, JournalPosition safe)
    {
        var current = store.Checkpoint.Position;
        if (safe.JournalId == current.JournalId && safe.NextUsn > current.NextUsn)
            store.Append(new(current, safe, []));
    }

    internal static bool RequiresRebuild(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is InvalidDataException or JournalInvalidatedException
                || current is Win32Exception native && native.NativeErrorCode is 1178 or 1179 or 1181) return true;
        return false;
    }

    internal static T ReadCurrentWithRetry<T>(Func<T> read, CancellationToken token,
        Action<Exception>? waiting = null, Action<TimeSpan, CancellationToken>? delay = null)
        => Retry(read, IsRetryableFileRead, token, waiting, delay);

    internal static void PersistWithRetry(Action persist, CancellationToken token,
        Action<Exception>? waiting = null, Action<TimeSpan, CancellationToken>? delay = null)
        => Retry(() => { persist(); return true; }, error => error is CurrentStateReadException, token, waiting, delay);

    private static T Retry<T>(Func<T> action, Func<Exception, bool> retryable, CancellationToken token,
        Action<Exception>? waiting, Action<TimeSpan, CancellationToken>? delay)
    {
        int milliseconds = 100;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return action(); }
            catch (Exception error) when (retryable(error))
            {
                waiting?.Invoke(error);
                var duration = TimeSpan.FromMilliseconds(milliseconds);
                if (delay is not null) delay(duration, token);
                else if (token.WaitHandle.WaitOne(duration)) token.ThrowIfCancellationRequested();
                milliseconds = Math.Min(milliseconds * 2, 2000);
            }
        }
    }

    private static bool IsRetryableFileRead(Exception error) => !RequiresRebuild(error)
        && error is IOException or UnauthorizedAccessException or Win32Exception;

    internal sealed class CurrentStateReadException(Exception inner) : IOException("子树当前状态暂时不可读取。", inner);

    internal static void PersistResolved(ChangeLogStore store, LiveTransaction transaction, JournalChangeResolver resolver, char drive, CancellationToken token)
    {
        IEnumerable<IndexedFileState> Files()
        {
            foreach (var file in transaction.Files) yield return file;
            foreach (ulong reference in resolver.ReenteredDirectories)
                foreach (var file in ReadSubtree(drive, reference, token)) yield return resolver.FilterState(file);
        }
        var buffer = new List<IndexedFileState>();
        long bytes = 0;
        foreach (var file in Files())
        {
            token.ThrowIfCancellationRequested();
            long estimated = 64 + file.Links.Sum(link => 16L + (link.Name.Length + link.ParentPath.Length) * 2L);
            if (buffer.Count > 0 && (buffer.Count >= 512 || bytes + estimated > 4L << 20))
            {
                var position = store.Checkpoint.Position;
                store.Append(new(position, position, buffer.ToArray()), token); buffer.Clear(); bytes = 0;
            }
            buffer.Add(file); bytes += estimated;
        }
        store.Append(new(store.Checkpoint.Position, transaction.To, buffer.ToArray()), token);
    }

    private static IEnumerable<IndexedFileState> ReadSubtree(char drive, ulong reference, CancellationToken token)
    {
        using var cursor = NativeJournal.EnumerateSubtree(drive, reference, token).GetEnumerator();
        while (true)
        {
            bool more;
            try { more = cursor.MoveNext(); }
            catch (Exception error) when (IsRetryableFileRead(error)) { throw new CurrentStateReadException(error); }
            if (!more) yield break;
            yield return cursor.Current;
        }
    }

    private static async Task<ChangeLogStore> OpenWriterAsync(string root, VolumeManifest manifest, JournalPosition initial, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new(root, manifest.SnapshotFile, initial, writable: true); }
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33) { await Task.Delay(100, token); }
        }
    }

    internal static JournalChangeResolver CreateResolver(string root, char drive, Func<ulong, IndexedFileState>? readState = null)
    {
        string canonical = NativeJournal.CanonicalPathForPath(root) ?? root;
        var resolver = new JournalChangeResolver(canonical, readState ?? (reference => NativeJournal.GetCurrentFileStateById(drive, reference)));
        if (canonical.StartsWith(drive + ":\\", StringComparison.OrdinalIgnoreCase))
            resolver.MarkInternal(NativeJournal.ReferenceForPath(root));
        return resolver;
    }

    private static void CompactAcknowledged(ChangeLogStore store)
    {
        if (store.Length < 64L << 20) return;
        string path = Path.Combine(Path.GetDirectoryName(store.LogPath)!, "consumer.json");
        if (!File.Exists(path)) return;
        try
        {
            var consumer = JsonSerializer.Deserialize<ConsumerPosition>(AtomicJson.ReadText(path));
            if (consumer is not null && consumer.LogId == store.LogId && consumer.KeepAfterSequence > 0)
                store.Compact(consumer.KeepAfterSequence);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { }
    }
}
