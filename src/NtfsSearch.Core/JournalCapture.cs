using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NtfsSearch.Core;

/// <summary>扫描期间将日志持续排到磁盘，避免长扫描依赖卷日志保留窗口。</summary>
public sealed class JournalCapture : IDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop;
    private readonly Task pump;
    private readonly FileStream spool;
    private readonly BinaryWriter writer;
    private readonly BinaryReader reader;
    private readonly SafeFileHandle volume;
    private readonly HashSet<ulong> ownReferences = [];
    private readonly byte[] journalBuffer = new byte[1 << 20];
    private readonly string path;
    private long readOffset;
    private long writeOffset;
    private JournalPosition position;
    private JournalPosition readPosition;
    private Exception? failure;
    private bool disposed;
    public JournalPosition Start { get; }
    public JournalPosition Position { get { lock (gate) return position; } }

    internal JournalCapture(char drive, string workDirectory, CancellationToken token, JournalPosition? from = null)
    {
        Directory.CreateDirectory(workDirectory);
        path = Path.Combine(workDirectory, $"capture-{char.ToUpperInvariant(drive)}-{Guid.NewGuid():N}.tmp");
        volume = NativeJournal.Open(drive);
        try
        {
            spool = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 64 << 10);
            writer = new(spool, System.Text.Encoding.Unicode, leaveOpen: true);
            reader = new(spool, System.Text.Encoding.Unicode, leaveOpen: true);
            using var root = NativeFileState.OpenPath($"{char.ToUpperInvariant(drive)}:\\");
            if (root.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            string volumeIdentity = NativeFileState.VolumeIdentity(root);
            if (string.Equals(NativeFileState.VolumeIdentity(spool.SafeFileHandle), volumeIdentity, StringComparison.OrdinalIgnoreCase))
                ownReferences.Add(NativeFileState.Reference(spool.SafeFileHandle));
            using (var directory = NativeFileState.OpenPath(Path.GetFullPath(workDirectory)))
                if (!directory.IsInvalid && string.Equals(NativeFileState.VolumeIdentity(directory), volumeIdentity, StringComparison.OrdinalIgnoreCase))
                    ownReferences.Add(NativeFileState.Reference(directory));
            var journal = NativeJournal.Query(drive, createIfMissing: true);
            if (from is not null) journal.Validate(from);
            readPosition = position = Start = from ?? journal.Position;
            stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            pump = Task.Run(Pump);
        }
        catch
        {
            reader?.Dispose(); writer?.Dispose(); spool?.Dispose(); stop?.Dispose(); volume.Dispose();
            // 初始化错误优先；清理失败不能掩盖失效日志等可诊断原因。
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    private async Task Pump()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var batch = NativeJournal.Read(volume, Position, outputBuffer: journalBuffer);
                if (batch.To == batch.From) { await Task.Delay(50, stop.Token).ConfigureAwait(false); continue; }
                lock (gate)
                {
                    var records = batch.Records.Where(record => !ownReferences.Contains(record.FileReference)).ToArray();
                    if (records.Length == 0)
                    {
                        // 自身spool写入的事件只推进内存游标，否则写入空帧会永远产生新事件。
                        position = batch.To;
                        continue;
                    }
                    spool.Position = writeOffset;
                    writer.Write(batch.From.NextUsn); writer.Write(batch.To.NextUsn);
                    writer.Write(records.Length);
                    foreach (var record in records)
                    {
                        writer.Write(record.FileReference); writer.Write(record.ParentReference); writer.Write(record.Usn);
                        writer.Write(record.Reason); writer.Write(record.Attributes); writer.Write(record.Name.Length);
                        writer.Write(MemoryMarshal.AsBytes(record.Name.AsSpan()));
                    }
                    writer.Flush(); writeOffset = spool.Position; position = batch.To;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error) { lock (gate) failure = error; }
    }

    /// <summary>每次读取一个有界捕获批次。null表示当前已排空，捕获仍可继续。</summary>
    public JournalBatch? ReadCaptured()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (failure is not null) throw new IOException("扫描期间USN捕获失败，必须废弃本次基线。", failure);
            if (readOffset == writeOffset)
            {
                if (readPosition == position) return null;
                var skipped = new JournalBatch(readPosition, position, []);
                readPosition = position;
                return skipped;
            }
            spool.Position = readOffset;
            _ = reader.ReadInt64(); long to = reader.ReadInt64(); int count = reader.ReadInt32();
            var records = new JournalRecord[count];
            for (int i = 0; i < count; i++)
            {
                ulong frn = reader.ReadUInt64(), parent = reader.ReadUInt64(); long usn = reader.ReadInt64();
                uint reason = reader.ReadUInt32(), attributes = reader.ReadUInt32(); int characters = reader.ReadInt32();
                byte[] name = reader.ReadBytes(checked(characters * 2));
                if (name.Length != characters * 2) throw new InvalidDataException("捕获文件已截断。");
                records[i] = new(frn, parent, usn, reason, attributes, MemoryMarshal.Cast<byte, char>(name).ToString());
            }
            readOffset = spool.Position;
            if (readOffset == writeOffset)
            {
                // 消费与生产共用锁；排空后回收空间时不会截断尚未消费的帧。
                spool.SetLength(0); spool.Position = 0; readOffset = writeOffset = 0;
            }
            var batch = new JournalBatch(readPosition, new(Start.JournalId, to), records);
            readPosition = batch.To;
            return batch;
        }
    }

    public void Stop()
    {
        stop.Cancel(); pump.GetAwaiter().GetResult();
        lock (gate) if (failure is not null) throw new IOException("扫描期间USN捕获失败。", failure);
    }
    public void Dispose()
    {
        if (disposed) return;
        try { Stop(); }
        finally
        {
            lock (gate) { disposed = true; writer.Dispose(); reader.Dispose(); spool.Dispose(); }
            volume.Dispose(); stop.Dispose(); File.Delete(path);
        }
    }
}
