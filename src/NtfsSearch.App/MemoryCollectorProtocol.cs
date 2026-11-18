using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using NtfsSearch.Core;

namespace NtfsSearch.App;

internal enum CollectorFrameKind : byte
{
    OpenVolume = 1,
    OpenScan,
    Error,
    Progress,
    WorkerStarted,
    ScanStart,
    ScanRows,
    ScanEnd,
    JournalStart,
    JournalFence,
    Changes
}

internal readonly record struct CollectorFrame(CollectorFrameKind Kind, byte[] Payload)
{
    internal BinaryReader Open() => new(new MemoryStream(Payload, writable: false));

    internal BinaryReader Open(CollectorFrameKind kind)
    {
        if (Kind != kind) throw new InvalidDataException($"采集消息不连续，期望 {kind}，收到 {Kind}。");
        return Open();
    }
}

internal sealed class CollectorFrameReader(Stream stream)
{
    private readonly byte[] header = new byte[5];

    internal async ValueTask<CollectorFrame> ReadAsync(CancellationToken token = default)
    {
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(1));
        if (length is < 0 or > ChangeLogStore.MaximumFrame) throw new InvalidDataException("采集消息长度无效。");
        var body = GC.AllocateUninitializedArray<byte>(length);
        await stream.ReadExactlyAsync(body, token);
        return new((CollectorFrameKind)header[0], body);
    }
}

internal sealed class CollectorFrameWriter(Stream stream)
{
    private readonly byte[] header = new byte[5];

    internal async Task WriteAsync(CollectorFrameKind kind, ReadOnlyMemory<byte> body, CancellationToken token)
    {
        if (body.Length > ChangeLogStore.MaximumFrame) throw new InvalidDataException("单次采集消息超过长度上限。");
        header[0] = (byte)kind;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), body.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(body, token);
    }

    internal async Task WriteAsync(CollectorFrameKind kind, Action<BinaryWriter> write, CancellationToken token)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer);
        write(writer);
        await WriteAsync(kind, buffer.GetBuffer().AsMemory(0, (int)buffer.Length), token);
    }
}

internal sealed record MemoryCollectorRequest(NtfsVolume Volume, string DataDirectory, string JournalPipe,
    string? ScanPipe, JournalPosition? Resume, int WorkerProcessors)
{
    internal static MemoryCollectorRequest Read(CollectorFrame frame)
    {
        using var reader = frame.Open(CollectorFrameKind.OpenVolume);
        var volume = CollectorWire.ReadVolume(reader);
        string directory = reader.ReadString();
        string journal = reader.ReadString();
        return reader.ReadBoolean()
            ? new(volume, directory, journal, reader.ReadString(), null, reader.ReadInt32())
            : new(volume, directory, journal, null, CollectorWire.ReadPosition(reader), 0);
    }

    internal void Write(BinaryWriter writer)
    {
        CollectorWire.WriteVolume(writer, Volume);
        writer.Write(DataDirectory);
        writer.Write(JournalPipe);
        writer.Write(ScanPipe is not null);
        if (ScanPipe is not null)
        {
            writer.Write(ScanPipe);
            writer.Write(WorkerProcessors);
        }
        else CollectorWire.WritePosition(writer, Resume!);
    }
}

internal static class CollectorWire
{
    internal static NtfsVolume ReadVolume(BinaryReader reader)
        => new(reader.ReadString(), reader.ReadString(), 0, "");

    internal static void WriteVolume(BinaryWriter writer, NtfsVolume volume)
    {
        writer.Write(volume.Id);
        writer.Write(volume.Root);
    }

    internal static JournalPosition ReadPosition(BinaryReader reader)
    {
        var position = new JournalPosition(reader.ReadUInt64(), reader.ReadInt64());
        if (position.NextUsn < 0) throw new InvalidDataException("采集消息中的日志游标无效。");
        return position;
    }

    internal static void WritePosition(BinaryWriter writer, JournalPosition position)
    {
        writer.Write(position.JournalId);
        writer.Write(position.NextUsn);
    }

    internal static void WriteName(BinaryWriter writer, ReadOnlySpan<char> value)
    {
        writer.Write(value.Length);
        // NTFS允许未配对的UTF-16代理项，保留字节可避免编码回退更改文件名。
        writer.Write(MemoryMarshal.AsBytes(value));
    }

    internal static ReadOnlySpan<char> ReadName(BinaryReader reader, byte[] payload)
    {
        int characters = reader.ReadInt32();
        int offset = (int)reader.BaseStream.Position;
        if (characters < 1 || characters > (payload.Length - offset) / 2)
            throw new InvalidDataException("采集消息中的名称长度无效。");
        int bytes = characters * 2;
        reader.BaseStream.Position += bytes;
        return MemoryMarshal.Cast<byte, char>(payload.AsSpan(offset, bytes));
    }

    internal static Exception ReadError(CollectorFrame frame)
    {
        using var reader = frame.Open(CollectorFrameKind.Error);
        bool gap = reader.ReadBoolean();
        string message = reader.ReadString();
        return gap ? new JournalInvalidatedException(message) : new IOException(message);
    }

    internal static Task WriteErrorAsync(CollectorFrameWriter writer, Exception error, bool gap, CancellationToken token)
        => writer.WriteAsync(CollectorFrameKind.Error, body =>
        {
            body.Write(gap);
            body.Write(error.Message);
        }, token);
}
