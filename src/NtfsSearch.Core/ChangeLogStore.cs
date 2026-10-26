using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace NtfsSearch.Core;

public sealed record ChangeLogCheckpoint(string LogId, long Sequence, JournalPosition Position);

// 事务数据和提交标记先落盘，再发布游标。读者只消费持久游标以内的完整事务。
public sealed class ChangeLogStore : IDisposable
{
    private const int MaximumFrame = 64 << 20;
    private const uint CommitTag = 0x54494d43;
    private readonly string baseId, headPath;
    private readonly JournalPosition initial;
    private readonly FileStream? ownership;
    private readonly FileStream generationLease;
    private Header header;
    private ChangeLogCheckpoint checkpoint;
    private long readOffset, readSequence;
    private long committedLength;
    private JournalPosition? readPosition;
    private string? readEpoch;
    private bool disposed;
    public string LogPath { get; }
    public string LogId => header.LogId;
    public long Length => new FileInfo(LogPath).Length;
    public ChangeLogCheckpoint Checkpoint => ownership is not null ? checkpoint : ReadHead() ?? checkpoint;

    public ChangeLogStore(string directory, string baseId, JournalPosition initial, bool writable = false)
    {
        if (string.IsNullOrEmpty(baseId) || baseId != Path.GetFileName(baseId)) throw new ArgumentException("无效的基线名称。", nameof(baseId));
        this.baseId = baseId; this.initial = initial;
        Directory.CreateDirectory(directory);
        // 锁放在代际目录外：回收者须取得同一文件的独占租约，读写者全生命周期共享持有。
        generationLease = new(Path.Combine(directory, baseId + ".generation.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        string root = Path.Combine(directory, baseId + ".changes");
        LogPath = Path.Combine(root, "changes.bin"); headPath = Path.Combine(root, "head.json");
        try
        {
            Directory.CreateDirectory(root);
            if (writable) ownership = new(Path.Combine(root, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (writable && !File.Exists(LogPath))
            {
                using var stream = new FileStream(LogPath, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                header = new(baseId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), 0, initial, 0);
                WriteHeader(stream, header); stream.Flush(true);
            }
            using var file = OpenRead(); header = ReadHeader(file);
            if (!writable)
            {
                using var snapshot = OpenCommittedRead(out var current, out var capturedHead);
                var verified = new ChangeLogCheckpoint(current.LogId, current.Anchor, current.Position);
                // 先捕获持久游标再打开日志，只验证这一不可变前缀；写者继续追加不影响本次恢复。
                while (verified.Sequence < capturedHead.Sequence)
                {
                    var change = ReadFrame(snapshot) ?? throw new InvalidDataException("已提交增量事务不完整。");
                    ValidateChain(verified, change);
                    verified = new(current.LogId, change.Sequence, change.Transaction.To);
                }
                if (verified.Position != capturedHead.Position) throw new InvalidDataException("持久USN与已提交事务不符。");
                header = current; checkpoint = capturedHead; committedLength = snapshot.Position;
                readOffset = snapshot.Position; readSequence = capturedHead.Sequence; readPosition = capturedHead.Position; readEpoch = current.Epoch;
                return;
            }
            var recovered = new ChangeLogCheckpoint(header.LogId, header.Anchor, header.Position);
            long valid = file.Position;
            while (ReadFrame(file) is { } change)
            {
                ValidateChain(recovered, change); recovered = new(header.LogId, change.Sequence, change.Transaction.To); valid = file.Position;
            }
            var durable = ReadHead();
            if (durable is not null && (durable.Sequence > recovered.Sequence
                || durable.Sequence == recovered.Sequence && durable.Position != recovered.Position))
                throw new InvalidDataException("已提交的增量日志已损坏，不能沿用其USN进度。");
            checkpoint = recovered;
            committedLength = valid;
            if (writable)
            {
                if (valid != file.Length)
                {
                    using var trim = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    trim.SetLength(valid); trim.Flush(true);
                }
                WriteHead();
            }
        }
        catch
        {
            try { ownership?.Dispose(); }
            finally { generationLease.Dispose(); }
            throw;
        }
    }

    public ChangeLogCheckpoint Append(LiveTransaction transaction, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownership is null) throw new InvalidOperationException("日志以只读方式打开。");
        token.ThrowIfCancellationRequested();
        if (transaction.From != checkpoint.Position || transaction.To.JournalId != transaction.From.JournalId
            || transaction.To.NextUsn < transaction.From.NextUsn) throw new InvalidDataException("增量日志事务水位不连续。");
        if (transaction.To == transaction.From && transaction.Files.Count == 0) return checkpoint;
        var change = new CommittedChange(checked(checkpoint.Sequence + 1), transaction);
        byte[] payload = Encode(change);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("单个增量事务过大，请拆分读取批次。");
        using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length != committedLength) stream.SetLength(committedLength);
        stream.Position = committedLength;
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(payload.Length); writer.Write(payload); writer.Write(SHA256.HashData(payload)); writer.Flush(); stream.Flush(true);
        token.ThrowIfCancellationRequested();
        writer.Write(CommitTag); writer.Flush(); stream.Flush(true);
        committedLength = stream.Position;
        checkpoint = new(header.LogId, change.Sequence, transaction.To); WriteHead(token);
        return checkpoint;
    }

    public IEnumerable<CommittedChange> ReadAfter(long sequence, int maximum = int.MaxValue, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(sequence); ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        using var stream = OpenCommittedRead(out var current, out var durable, token);
        if (sequence < current.Anchor) throw new InvalidDataException("增量历史已回收，需要匹配的实时检查点。");
        if (sequence > durable.Sequence) throw new InvalidDataException("实时检查点领先于已提交日志。");
        long previousSequence = current.Anchor;
        JournalPosition previousPosition = current.Position;
        if (readEpoch == current.Epoch && readSequence == sequence && readOffset >= stream.Position && readOffset <= stream.Length)
        {
            stream.Position = readOffset; previousSequence = sequence;
            // 连续尾读的From由上一帧已校验，后续仍逐帧检查。
            previousPosition = readPosition ?? throw new InvalidDataException("缺少尾读水位。");
        }
        int returned = 0;
        while (previousSequence < durable.Sequence && returned < maximum && ReadFrame(stream) is { } change)
        {
            token.ThrowIfCancellationRequested();
            if (change.Sequence != previousSequence + 1) throw new InvalidDataException("增量事务序号有缺口。");
            if (change.Transaction.From != previousPosition) throw new InvalidDataException("增量事务USN有缺口。");
            previousSequence = change.Sequence; previousPosition = change.Transaction.To;
            if (change.Sequence <= sequence) continue;
            readOffset = stream.Position; readSequence = change.Sequence; readPosition = change.Transaction.To; readEpoch = current.Epoch;
            returned++; yield return change;
        }
        if (previousSequence < durable.Sequence && returned < maximum) throw new InvalidDataException("已提交增量事务不完整。");
        if (previousSequence == durable.Sequence && previousPosition != durable.Position)
            throw new InvalidDataException("持久USN与已提交事务不符。");
    }

    public void Compact(long retainAfterSequence, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (ownership is null) throw new InvalidOperationException("只读日志不能回收。");
        if (retainAfterSequence <= header.Anchor) return;
        if (retainAfterSequence > checkpoint.Sequence) throw new ArgumentOutOfRangeException(nameof(retainAfterSequence));
        JournalPosition anchor = header.Position;
        // 第一遍只定位回收边界，不把未确认的长尾装进内存。
        foreach (var change in ReadAfter(header.Anchor, token: token))
        {
            anchor = change.Transaction.To;
            if (change.Sequence == retainAfterSequence) break;
        }
        string temporary = LogPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var next = new Header(baseId, header.LogId, Guid.NewGuid().ToString("N"), retainAfterSequence, anchor, 0);
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(file))
            {
                WriteHeader(file, next);
                // 第二遍逐帧转换并落盘；内存上限只取决于单帧大小。
                foreach (var change in ReadAfter(retainAfterSequence, token: token))
                { token.ThrowIfCancellationRequested(); byte[] payload = Encode(change); writer.Write(payload.Length); writer.Write(payload); writer.Write(SHA256.HashData(payload)); writer.Write(CommitTag); }
                writer.Flush(); file.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            AtomicFile.Publish(temporary, LogPath, LogPath + ".bak", token);
            header = next; committedLength = new FileInfo(LogPath).Length; readEpoch = null; WriteHead(token);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { ownership?.Dispose(); }
        finally { generationLease.Dispose(); }
    }
    private FileStream OpenRead(CancellationToken token = default)
    {
        FileStream? stream = null;
        AtomicFile.Retry(() => stream = new(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
            OperatingSystem.IsWindows(), token, allowMissing: true);
        return stream!;
    }
    private FileStream OpenCommittedRead(out Header current, out ChangeLogCheckpoint durable, CancellationToken token = default)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            // 必须先读head再打开WAL：反过来会把换代前的文件句柄与换代后的新尾混合比较。
            var captured = ownership is not null ? checkpoint : ReadHead();
            var stream = OpenRead(token);
            try
            {
                current = ReadHeader(stream);
                if (current.LogId != header.LogId) throw new InvalidDataException("增量日志已换代，需要重新加载检查点。");
                durable = captured ?? new(current.LogId, current.Anchor, current.Position);
                if (current.Anchor <= durable.Sequence) return stream;
                // 两次读取之间刚好发生回收：丢弃旧head重新取快照，尚未向调用者交付任何事务。
            }
            catch { stream.Dispose(); throw; }
            stream.Dispose();
        }
    }
    private void WriteHead(CancellationToken token = default) => AtomicJson.Write(headPath, checkpoint, keepBackup: true, token);
    private ChangeLogCheckpoint? ReadHead()
    {
        // 只有独占写者在首次建日志时允许缺少head；读者不能把替换窗口中的路径缺失当作游标回退。
        if (ownership is not null && !File.Exists(headPath)) return null;
        ChangeLogCheckpoint value;
        try
        {
            value = JsonSerializer.Deserialize<ChangeLogCheckpoint>(AtomicJson.ReadText(headPath))
                ?? throw new InvalidDataException("USN进度文件为空。");
        }
        catch (JsonException error) { throw new InvalidDataException("USN进度文件格式损坏。", error); }
        if (value.LogId != header.LogId || value.Sequence < header.Anchor || value.Position is null
            || value.Position.JournalId != initial.JournalId)
            throw new InvalidDataException("USN进度与日志身份不符。");
        return value;
    }
    private static void ValidateChain(ChangeLogCheckpoint previous, CommittedChange change)
    {
        if (change.Sequence != previous.Sequence + 1 || change.Transaction.From != previous.Position
            || change.Transaction.From.JournalId != change.Transaction.To.JournalId || change.Transaction.To.NextUsn < change.Transaction.From.NextUsn)
            throw new InvalidDataException("变化日志事务链损坏。");
    }

    private Header ReadHeader(Stream stream)
    {
        try { return ReadHeaderCore(stream); }
        catch (EndOfStreamException error) { throw new InvalidDataException("变化日志头不完整。", error); }
    }
    private Header ReadHeaderCore(Stream stream)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int length = reader.ReadInt32();
        if (length is < 64 or > 4096 || stream.Length - stream.Position < length + 32) throw new InvalidDataException("变化日志头长度无效。");
        byte[] data = reader.ReadBytes(length), digest = reader.ReadBytes(32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(data), digest)) throw new InvalidDataException("变化日志头校验失败。");
        using var body = new MemoryStream(data); using var input = new BinaryReader(body);
        if (!input.ReadBytes(8).AsSpan().SequenceEqual("NTWAL001"u8)) throw new InvalidDataException("未知变化日志格式。");
        string id = ReadText(input), log = ReadText(input), epoch = ReadText(input);
        long anchor = input.ReadInt64(); var position = new JournalPosition(input.ReadUInt64(), input.ReadInt64());
        if (id != baseId || !Guid.TryParseExact(log, "N", out _) || !Guid.TryParseExact(epoch, "N", out _)
            || anchor < 0 || position.JournalId != initial.JournalId || position.NextUsn < initial.NextUsn || body.Position != body.Length)
            throw new InvalidDataException("变化日志基线身份不符。");
        return new(id, log, epoch, anchor, position, stream.Position);
    }
    private static void WriteHeader(Stream stream, Header header)
    {
        using var data = new MemoryStream(); using var writer = new BinaryWriter(data, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write("NTWAL001"u8); WriteText(writer, header.BaseId); WriteText(writer, header.LogId); WriteText(writer, header.Epoch);
        writer.Write(header.Anchor); writer.Write(header.Position.JournalId); writer.Write(header.Position.NextUsn); writer.Flush();
        byte[] payload = data.ToArray(); using var output = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        output.Write(payload.Length); output.Write(payload); output.Write(SHA256.HashData(payload)); output.Flush();
    }
    private static CommittedChange? ReadFrame(Stream stream)
    {
        try { return ReadFrameCore(stream); }
        catch (EndOfStreamException error) { throw new InvalidDataException("变化事务内容不完整。", error); }
    }
    private static CommittedChange? ReadFrameCore(Stream stream)
    {
        if (stream.Length - stream.Position < 76) return null;
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        int length = reader.ReadInt32();
        if (length is < 36 or > MaximumFrame) throw new InvalidDataException("变化事务长度无效。");
        if (stream.Length - stream.Position < length + 36L) return null;
        byte[] payload = reader.ReadBytes(length), digest = reader.ReadBytes(32);
        if (reader.ReadUInt32() != CommitTag) return null;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), digest)) throw new InvalidDataException("变化事务校验失败。");
        return Decode(payload);
    }
    private static byte[] Encode(CommittedChange change)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
        var transaction = change.Transaction;
        writer.Write(change.Sequence); writer.Write(transaction.From.JournalId); writer.Write(transaction.From.NextUsn); writer.Write(transaction.To.NextUsn);
        writer.Write(transaction.Files.Count);
        foreach (var file in transaction.Files)
        {
            writer.Write(file.FileReference); writer.Write(file.IsDirectory);
            writer.Write(file.Size.HasValue); if (file.Size is long size) writer.Write(size);
            writer.Write(file.ModifiedUtcTicks.HasValue); if (file.ModifiedUtcTicks is long ticks) writer.Write(ticks);
            writer.Write(file.Links.Count);
            foreach (var link in file.Links) { writer.Write(link.ParentReference); WriteText(writer, link.ParentPath); WriteText(writer, link.Name); }
        }
        writer.Flush(); return stream.ToArray();
    }
    private static CommittedChange Decode(byte[] payload)
    {
        using var stream = new MemoryStream(payload); using var reader = new BinaryReader(stream);
        long sequence = reader.ReadInt64(); ulong id = reader.ReadUInt64(); long from = reader.ReadInt64(), to = reader.ReadInt64();
        int count = ReadCount(reader, 65536); var files = new IndexedFileState[count];
        for (int i = 0; i < count; i++)
        {
            ulong reference = reader.ReadUInt64(); bool directory = reader.ReadBoolean();
            long? size = reader.ReadBoolean() ? reader.ReadInt64() : null, ticks = reader.ReadBoolean() ? reader.ReadInt64() : null;
            int links = ReadCount(reader, 4096); var names = new IndexedLink[links];
            for (int n = 0; n < links; n++) names[n] = new(reader.ReadUInt64(), ReadText(reader), ReadText(reader));
            files[i] = new(reference, directory, names, size, ticks);
        }
        if (sequence < 1 || from < 0 || to < from || stream.Position != stream.Length) throw new InvalidDataException("变化事务内容无效。");
        return new(sequence, new(new(id, from), new(id, to), files));
    }
    private static void WriteText(BinaryWriter writer, string value)
    { writer.Write(value.Length); writer.Write(MemoryMarshal.AsBytes(value.AsSpan())); }
    private static string ReadText(BinaryReader reader)
    {
        int count = ReadCount(reader, 65535);
        if (reader.BaseStream.Length - reader.BaseStream.Position < count * 2L) throw new InvalidDataException("变化事务字符串越界。");
        char[] text = new char[count]; reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(text.AsSpan())); return new(text);
    }
    private static int ReadCount(BinaryReader reader, int maximum)
    { int count = reader.ReadInt32(); if (count < 0 || count > maximum) throw new InvalidDataException("变化事务数量越界。"); return count; }
    private sealed record Header(string BaseId, string LogId, string Epoch, long Anchor, JournalPosition Position, long Bytes);
}
