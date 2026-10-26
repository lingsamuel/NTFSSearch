using NtfsSearch.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace NtfsSearch.Tests;

public class ChangeLogTests
{
    [Fact]
    public void DurableTransactionsPreserveUtf16AndResumeAfterReopen()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(7, 100);
            using (var writer = new ChangeLogStore(root, "base.ntidx", initial, writable: true))
            {
                writer.Append(new(initial, new(7, 200), [new(99, false, [new(5, @"C:\", "name\ud800.txt")], 12, 50)]));
                writer.Append(new(new(7, 200), new(7, 300), [new(99, false, [])]));
            }
            using var reader = new ChangeLogStore(root, "base.ntidx", initial);
            Assert.Equal(2, reader.Checkpoint.Sequence); Assert.Equal(300, reader.Checkpoint.Position.NextUsn);
            var rows = reader.ReadAfter(0).ToArray();
            Assert.Equal("name\ud800.txt", rows[0].Transaction.Files[0].Links[0].Name);
            Assert.Single(reader.ReadAfter(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UncommittedTailDoesNotAdvanceAndIsTruncatedBeforeNextAppend()
    {
        string root = Temporary();
        try
        {
            var position = new JournalPosition(2, 10);
            string log;
            using (var writer = new ChangeLogStore(root, "base.ntidx", position, true))
            { writer.Append(new(position, new(2, 20), [])); log = writer.LogPath; }
            using (var tail = new FileStream(log, FileMode.Append)) tail.Write([10, 0, 0, 0, 1, 2]);
            using var recovered = new ChangeLogStore(root, "base.ntidx", position, true);
            Assert.Equal(1, recovered.Checkpoint.Sequence);
            recovered.Append(new(new(2, 20), new(2, 30), []));
            Assert.Equal(2, recovered.ReadAfter(0).Count());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CompactionKeepsCheckpointAnchorAndRejectsMissingHistory()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            for (int i = 0; i < 5; i++) writer.Append(new(new(1, i), new(1, i + 1), []));
            writer.Compact(3);
            Assert.Equal(new long[] { 4, 5 }, writer.ReadAfter(3).Select(c => c.Sequence));
            Assert.Throws<InvalidDataException>(() => writer.ReadAfter(2).ToArray());
            Assert.Equal(5, writer.Checkpoint.Sequence);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ConcurrentReadersDoNotMistakeAppendOrCompactionForCorruption()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            using var stop = new CancellationTokenSource();
            var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
            using var started = new CountdownEvent(8);
            var readers = Enumerable.Range(0, 8).Select(readerNumber => Task.Factory.StartNew(() =>
            {
                started.Signal();
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var reader = new ChangeLogStore(root, "base.ntidx", initial);
                        long sequence = reader.Checkpoint.Sequence;
                        // 新读者从当下已提交端点开始，不要求已被回收的旧历史。
                        _ = reader.ReadAfter(sequence).ToArray();
                    }
                    catch (InvalidDataException error) when (error.Message.Contains("增量历史已回收")) { }
                    catch (Exception error) { errors.Enqueue(error); }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            try
            {
                Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
                for (int i = 0; i < 200; i++)
                {
                    writer.Append(new(new(1, i), new(1, i + 1), []));
                    if (i % 5 == 4) writer.Compact(i - 2);
                }
            }
            finally { stop.Cancel(); await Task.WhenAll(readers); }
            Assert.Empty(errors);
            Assert.Equal(200, writer.Checkpoint.Sequence);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ContinuousTailReadPinsCommittedEndAcrossCompactionAndAppend()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            for (int i = 0; i < 3; i++) writer.Append(new(new(1, i), new(1, i + 1), []));
            using var reader = new ChangeLogStore(root, "base.ntidx", initial);
            Assert.Empty(reader.ReadAfter(3));
            writer.Compact(2);
            writer.Append(new(new(1, 3), new(1, 4), [])); writer.Append(new(new(1, 4), new(1, 5), []));
            using var tail = reader.ReadAfter(3).GetEnumerator();
            Assert.True(tail.MoveNext()); Assert.Equal(4, tail.Current.Sequence);
            writer.Compact(4); writer.Append(new(new(1, 5), new(1, 6), []));
            Assert.True(tail.MoveNext()); Assert.Equal(5, tail.Current.Sequence);
            Assert.False(tail.MoveNext());
            Assert.Equal(6, Assert.Single(reader.ReadAfter(5)).Sequence);
            Assert.Empty(reader.ReadAfter(6));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TruncatedCommittedFrameStillFailsRecovery()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            string path;
            using (var writer = new ChangeLogStore(root, "base.ntidx", initial, true))
            {
                writer.Append(new(initial, new(1, 1), [])); path = writer.LogPath;
            }
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write)) file.SetLength(file.Length - 3);
            Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", initial));
            Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", initial, true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void GenerationLeasePreventsCollectionUntilEveryReaderAndWriterCloses()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            string leasePath = Path.Combine(root, "base.ntidx.generation.lock");
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            using var reader = new ChangeLogStore(root, "base.ntidx", initial);
            Assert.Throws<IOException>(() => new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            writer.Dispose();
            Assert.Throws<IOException>(() => new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            reader.Dispose();
            using var collection = new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FailedOpenReleasesExternalGenerationLease()
    {
        string root = Temporary();
        try
        {
            string changes = Path.Combine(root, "base.ntidx.changes"); Directory.CreateDirectory(changes);
            File.WriteAllBytes(Path.Combine(changes, "changes.bin"), [1, 2]);
            var error = Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", new(1, 0)));
            Assert.IsType<EndOfStreamException>(error.InnerException);
            using var collection = new FileStream(Path.Combine(root, "base.ntidx.generation.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FailedSecondWriterDoesNotLeakGenerationLease()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            Assert.Throws<IOException>(() => new ChangeLogStore(root, "base.ntidx", initial, true));
            writer.Dispose();
            using var collection = new FileStream(Path.Combine(root, "base.ntidx.generation.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CompactPreservesLongTailOrderAndPayload()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using var writer = new ChangeLogStore(root, "base.ntidx", initial, true);
            for (int i = 0; i < 100; i++)
                writer.Append(new(new(1, i), new(1, i + 1), [new((ulong)i + 10, false, [new(5, @"C:\", "file-" + i)])]));
            writer.Compact(7);
            var tail = writer.ReadAfter(7).ToArray();
            Assert.Equal(Enumerable.Range(8, 93).Select(i => (long)i), tail.Select(c => c.Sequence));
            Assert.Equal(Enumerable.Range(7, 93).Select(i => "file-" + i), tail.Select(c => c.Transaction.Files[0].Links[0].Name));
            Assert.Equal(new JournalPosition(1, 100), writer.Checkpoint.Position);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidHeadJsonIsClassifiedAsCorruptionWithOriginalError()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using (var writer = new ChangeLogStore(root, "base.ntidx", initial, true)) { }
            using var reader = new ChangeLogStore(root, "base.ntidx", initial);
            File.WriteAllText(Path.Combine(root, "base.ntidx.changes", "head.json"), "{");
            var error = Assert.Throws<InvalidDataException>(() => reader.Checkpoint);
            Assert.IsAssignableFrom<JsonException>(error.InnerException);
            Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", initial));
            Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", initial, true));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ValidChecksumWithTruncatedTransactionBodyIsClassifiedAsCorruption()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            string path;
            using (var writer = new ChangeLogStore(root, "base.ntidx", initial, true))
            {
                writer.Append(new(initial, new(1, 1), [])); path = writer.LogPath;
            }
            byte[] bytes = File.ReadAllBytes(path);
            int frame = BinaryPrimitives.ReadInt32LittleEndian(bytes) + 36;
            int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(frame));
            var payload = bytes.AsSpan(frame + 4, length);
            BinaryPrimitives.WriteInt32LittleEndian(payload[32..], 1);
            SHA256.HashData(payload, bytes.AsSpan(frame + 4 + length, 32));
            File.WriteAllBytes(path, bytes);
            var error = Assert.Throws<InvalidDataException>(() => new ChangeLogStore(root, "base.ntidx", initial));
            Assert.IsType<EndOfStreamException>(error.InnerException);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void HeadSharingConflictRemainsAnIoError()
    {
        string root = Temporary();
        try
        {
            var initial = new JournalPosition(1, 0);
            using (var writer = new ChangeLogStore(root, "base.ntidx", initial, true)) { }
            using var reader = new ChangeLogStore(root, "base.ntidx", initial);
            using var locked = new FileStream(Path.Combine(root, "base.ntidx.changes", "head.json"),
                FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.ThrowsAny<IOException>(() => reader.Checkpoint);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Temporary()
    { string root = Path.Combine(Path.GetTempPath(), "ntfs-wal-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); return root; }
}
