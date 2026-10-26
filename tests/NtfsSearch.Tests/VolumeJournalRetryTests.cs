using System.ComponentModel;
using NtfsSearch.App;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class VolumeJournalRetryTests
{
    [Fact]
    public void FileStateSharingFailureRetainsBatchAndRetriesWithoutAdvancingCursor()
    {
        var durable = new JournalPosition(1, 0);
        var batch = new JournalBatch(durable, new(1, 1), [new(10, 5, 0, 0x100, 0, "created")]);
        int attempts = 0; var delays = new List<TimeSpan>();
        var resolver = new JournalChangeResolver(Path.GetTempPath(), reference => VolumeJournalService.ReadCurrentWithRetry(() =>
        {
            Assert.Equal(new JournalPosition(1, 0), durable);
            if (++attempts < 4) throw new Win32Exception(32);
            return new IndexedFileState(reference, false, [new(5, @"C:\", "created")]);
        }, default, delay: (duration, _) => delays.Add(duration)));
        var transaction = resolver.Resolve(batch, durable);
        Assert.Equal(4, attempts);
        Assert.Equal(3, delays.Count);
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2)));
        Assert.Equal(batch.To, transaction.To);
        Assert.Single(transaction.Files);
    }

    [Fact]
    public void RetryCanBeCancelledAndNeverReturnsAnEmptyReplacementState()
    {
        using var stop = new CancellationTokenSource(); int attempts = 0;
        Assert.Throws<OperationCanceledException>(() => VolumeJournalService.ReadCurrentWithRetry<int>(() =>
        {
            attempts++; throw new IOException("正在改名");
        }, stop.Token, delay: (_, _) => stop.Cancel()));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public void CorruptOrInvalidatedReadIsNotTreatedAsTransientFileSharing()
    {
        int attempts = 0;
        Assert.Throws<InvalidDataException>(() => VolumeJournalService.ReadCurrentWithRetry<int>(() =>
        { attempts++; throw new InvalidDataException("身份数据损坏"); }, default));
        Assert.Equal(1, attempts);
        Assert.Throws<JournalInvalidatedException>(() => VolumeJournalService.ReadCurrentWithRetry<int>(
            () => throw new JournalInvalidatedException("日志截断"), default));
    }

    [Fact]
    public void SubtreeReadRetryReplaysMultipartButWalFailureIsNotRetried()
    {
        int attempts = 0;
        VolumeJournalService.PersistWithRetry(() =>
        {
            if (++attempts == 1) throw new VolumeJournalService.CurrentStateReadException(new Win32Exception(32));
        }, default, delay: (_, _) => { });
        Assert.Equal(2, attempts);
        attempts = 0;
        Assert.Throws<IOException>(() => VolumeJournalService.PersistWithRetry(() =>
        { attempts++; throw new IOException("WAL写入失败"); }, default, delay: (_, _) => { }));
        Assert.Equal(1, attempts);
    }
    [Fact]
    public void OnlyJournalInvalidationOrCorruptDataRequestsRebuild()
    {
        Assert.False(VolumeJournalService.RequiresRebuild(new Win32Exception(32)));
        Assert.False(VolumeJournalService.RequiresRebuild(new IOException("缓存暂时不可写")));
        Assert.True(VolumeJournalService.RequiresRebuild(new InvalidDataException("WAL校验失败")));
        Assert.True(VolumeJournalService.RequiresRebuild(new IOException("捕获失败", new JournalInvalidatedException("USN断档"))));
        Assert.True(VolumeJournalService.RequiresRebuild(new Win32Exception(1181)));
    }

    [Fact]
    public void CancellationFlushesOnlySuccessfullyResolvedSafeCursor()
    {
        string directory = Path.Combine(Path.GetTempPath(), "retry-flush-" + Guid.NewGuid().ToString("N"));
        try
        {
            var initial = new JournalPosition(1, 0);
            using var store = new ChangeLogStore(directory, "base.ntidx", initial, true);
            // 中间段已落盘但最终batch还未完成，取消时只能刷新此前已解析的空区间。
            store.Append(new(initial, initial, [new(10, false, [new(5, @"C:\", "first-part")])]));
            VolumeJournalService.FlushSafePosition(store, new(1, 5));
            Assert.Equal(new JournalPosition(1, 5), store.Checkpoint.Position);
            Assert.Equal(2, store.Checkpoint.Sequence);
            VolumeJournalService.FlushSafePosition(store, initial);
            Assert.Equal(new JournalPosition(1, 5), store.Checkpoint.Position);
            Assert.Equal(2, store.Checkpoint.Sequence);
            Assert.Equal(new long[] { 1, 2 }, store.ReadAfter(0).Select(c => c.Sequence));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void SubtreeRetryKeepsMultipartCursorUntilFinalPartAndReplaysIdempotently()
    {
        string directory = Path.Combine(Path.GetTempPath(), "retry-parts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var initial = new JournalPosition(1, 0); int attempts = 0;
            using var store = new ChangeLogStore(directory, "base.ntidx", initial, true);
            var file = new IndexedFileState(10, false, [new(5, @"C:\", "restored")]);
            VolumeJournalService.PersistWithRetry(() =>
            {
                Assert.Equal(initial, store.Checkpoint.Position);
                store.Append(new(initial, initial, [file]));
                if (++attempts == 1) throw new VolumeJournalService.CurrentStateReadException(new IOException("补采暂时不可读"));
                store.Append(new(initial, new(1, 1), []));
            }, default, delay: (_, _) => { });
            var live = new LiveVolumeIndex(new IndexBuilder().Build(), initial);
            foreach (var change in store.ReadAfter(0)) live.Apply(change);
            Assert.Equal(2, attempts);
            Assert.Equal(new JournalPosition(1, 1), live.Position);
            Assert.Equal(3, live.AppliedSequence);
            Assert.Equal(1, live.Layers.Sum(layer => layer.VisibleCount));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

}
