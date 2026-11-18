using System.IO;
using System.ComponentModel;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal static class VolumeJournalService
{
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

    internal static IEnumerable<LiveTransaction> SplitResolved(LiveTransaction transaction, JournalChangeResolver resolver,
        char drive, CancellationToken token)
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
                yield return new(transaction.From, transaction.From, buffer.ToArray());
                buffer.Clear(); bytes = 0;
            }
            buffer.Add(file); bytes += estimated;
        }
        yield return new(transaction.From, transaction.To, buffer.ToArray());
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

    internal static JournalChangeResolver CreateResolver(string root, char drive, Func<ulong, IndexedFileState>? readState = null)
    {
        string canonical = NativeJournal.CanonicalPathForMissingPath(root);
        var resolver = new JournalChangeResolver(canonical, readState ?? (reference => NativeJournal.GetCurrentFileStateById(drive, reference)));
        if (canonical.StartsWith(drive + ":\\", StringComparison.OrdinalIgnoreCase) && Directory.Exists(root))
            resolver.MarkInternal(NativeJournal.ReferenceForPath(root));
        return resolver;
    }
}
