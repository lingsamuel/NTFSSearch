using System.Buffers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ZstdSharp;

namespace NtfsSearch.Core;

/// <summary>可查询的单卷索引视图及其已应用的 USN 水位。</summary>
public sealed record ExitSnapshotVolume(NtfsVolume Volume, JournalPosition Position, IReadOnlyList<SearchIndex> Layers)
{
    internal bool HasCreationMetadata { get; init; } = true;
}

public static class ExitSnapshotStore
{
    public const string FileName = "index.ntsnap";
    private static ReadOnlySpan<byte> Magic => "NTSEXIT4"u8;
    private static ReadOnlySpan<byte> LegacyMagic => "NTSEXIT3"u8;
    private const int CompressionLevel = 3;
    private const int DigestBytes = 32;
    private const int DirectoryEntryBytes = sizeof(long) * 2 + DigestBytes;
    private const int MinimumVolumeBytes = 3 + sizeof(int) * 2 + sizeof(long) * 3;
    private const int MinimumRowBytes = 6;
    private const ulong RecordNumberMask = (1UL << 48) - 1;
    private static readonly Comparer<ulong> ReferenceOrder = Comparer<ulong>.Create(SearchIndex.CompareReference);

    /// <summary>流式保存冻结视图，卷进度在对应数据全部写入临时文件时报告。</summary>
    public static void Save(string path, IReadOnlyList<ExitSnapshotVolume> volumes,
        CancellationToken token = default, Action<int, int>? progress = null)
    {
        RequireLittleEndian();
        token.ThrowIfCancellationRequested();
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 20))
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                // 卷目录置于文件开头，尾部损坏时仍能定位并恢复其余卷。
                long directoryEnd = Magic.Length + sizeof(int) + (long)volumes.Count * DirectoryEntryBytes;
                file.Position = directoryEnd + DigestBytes;
                var entries = new (long Length, long PayloadLength, byte[] Digest)[volumes.Count];
                for (int i = 0; i < volumes.Count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    long start = file.Position;
                    using var hash = SHA256.Create();
                    PayloadStream payloadStream;
                    using (var crypto = new CryptoStream(file, hash, CryptoStreamMode.Write, leaveOpen: true))
                    // 每卷结束一个独立压缩帧，哈希覆盖已经落到该卷块的压缩字节。
                    using (var compressed = new CompressionStream(crypto, level: CompressionLevel, leaveOpen: true))
                    {
                        payloadStream = new PayloadStream(compressed);
                        using var buffered = new BufferedStream(payloadStream, 64 * 1024);
                        using var payload = new BinaryWriter(buffered, Encoding.UTF8, leaveOpen: true);
                        WriteVolume(payload, volumes[i], token);
                    }
                    entries[i] = (file.Position - start, payloadStream.Position, hash.Hash!);
                    progress?.Invoke(i + 1, volumes.Count);
                }
                token.ThrowIfCancellationRequested();
                file.Position = 0;
                writer.Write(Magic);
                writer.Write(entries.Length);
                foreach (var (length, payloadLength, digest) in entries)
                {
                    writer.Write(length);
                    writer.Write(payloadLength);
                    writer.Write(digest);
                }
                file.Position = 0;
                Span<byte> directoryDigest = stackalloc byte[DigestBytes];
                SnapshotStore.CalculateChecksum(file, directoryEnd, directoryDigest, token);
                writer.Write(directoryDigest);
                file.Flush(flushToDisk: true);
            }
            AtomicFile.Publish(temporary, path, token: token);
        }
        catch (ZstdException error) { throw new IOException("退出快照压缩失败。", error); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>恢复快照中的可用卷，并在内存中重建各列排序。</summary>
    public static IReadOnlyList<ExitSnapshotVolume> Load(string path, CancellationToken token = default)
        => LoadVolumes(path, token).ToArray();

    /// <summary>逐卷返回完整且已重新排序的索引。并行恢复时按完成顺序返回。</summary>
    public static IEnumerable<ExitSnapshotVolume> LoadVolumes(string path, CancellationToken token = default, int maxConcurrency = 1)
        => LoadVolumes(path, token, maxConcurrency, null);

    // 回调在实际读取卷的线程上执行，卷身份经过快照校验后才报告。
    internal static IEnumerable<ExitSnapshotVolume> LoadVolumes(string path, CancellationToken token, int maxConcurrency,
        Action<NtfsVolume, bool, int>? progress, bool requireCreationMetadata = false)
    {
        RequireLittleEndian();
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 20);
        var entries = ReadDirectory(file, token, out bool hasCreationMetadata);
        // 旧快照以原本逐卷采集流程补齐创建时间，避免先为缺少该列的行重建排序。
        if (requireCreationMetadata && !hasCreationMetadata) yield break;
        long length = file.Length;
        if (maxConcurrency > 1 && entries.Length > 1)
        {
            foreach (var volume in LoadConcurrently(file.SafeFileHandle, entries.TakeWhile(entry => entry.End <= length), maxConcurrency, token, progress, hasCreationMetadata))
                yield return volume;
            yield break;
        }
        foreach (var (start, end, payloadLength, digest) in entries)
        {
            token.ThrowIfCancellationRequested();
            if (end > length) break;
            if (LoadVolume(file.SafeFileHandle, start, end, payloadLength, digest, token, progress, hasCreationMetadata) is { } volume)
                yield return volume;
        }
    }

    private static IEnumerable<ExitSnapshotVolume> LoadConcurrently(SafeFileHandle file,
        IEnumerable<(long Start, long End, long PayloadLength, byte[] Digest)> entries, int maxConcurrency, CancellationToken token,
        Action<NtfsVolume, bool, int>? progress, bool hasCreationMetadata)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var remaining = entries.GetEnumerator();
        var pending = new List<Task<ExitSnapshotVolume?>>();
        try
        {
            while (pending.Count < maxConcurrency && StartNext()) { }
            while (pending.Count > 0)
            {
                token.ThrowIfCancellationRequested();
                var next = Task.WhenAny(pending).GetAwaiter().GetResult();
                pending.Remove(next);
                var volume = next.GetAwaiter().GetResult();
                if (volume is not null) yield return volume;
                StartNext();
            }
        }
        finally
        {
            lifetime.Cancel();
            // 即使调用方停止枚举，也要等后台读取结束再释放指向原快照的文件句柄。
            try { Task.WhenAll(pending).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }

        bool StartNext()
        {
            if (!remaining.MoveNext()) return false;
            var (start, end, payloadLength, digest) = remaining.Current;
            pending.Add(Task.Run(() => LoadVolume(file, start, end, payloadLength, digest, lifetime.Token, progress, hasCreationMetadata), lifetime.Token));
            return true;
        }
    }

    private static ExitSnapshotVolume? LoadVolume(SafeFileHandle file, long start, long end, long payloadLength, byte[] digest,
        CancellationToken token,
        Action<NtfsVolume, bool, int>? progress, bool hasCreationMetadata)
    {
        try
        {
            // 串行读取也要限制卷块边界，解码器可以继续预读当前帧之后的字节。
            using var stored = new BufferedStream(new VolumeStream(file, start, end - start), 1 << 20);
            if (!ChecksumMatches(stored, end - start, digest, token)) return null;
            stored.Position = 0;
            using var decompressed = new DecompressionStream(stored, leaveOpen: true);
            using var buffered = new BufferedStream(decompressed, 128 * 1024);
            using var payload = new PayloadStream(buffered);
            return ReadVolume(payload, payloadLength, token, progress, hasCreationMetadata);
        }
        catch (Exception error) when (error is InvalidDataException or EndOfStreamException or ArgumentException or FormatException or ZstdException)
        {
            // 卷目录可信，其余卷按各自的偏移和校验值继续恢复。
            return null;
        }
    }

    private static (long Start, long End, long PayloadLength, byte[] Digest)[] ReadDirectory(FileStream file, CancellationToken token, out bool hasCreationMetadata)
    {
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        int prefixBytes = Magic.Length + sizeof(int);
        ReadOnlySpan<byte> magic = reader.ReadBytes(Magic.Length);
        hasCreationMetadata = magic.SequenceEqual(Magic);
        if (file.Length < prefixBytes + DigestBytes || !hasCreationMetadata && !magic.SequenceEqual(LegacyMagic))
            throw new InvalidDataException("退出快照标头无效。");
        int count = reader.ReadInt32();
        long directoryEnd = prefixBytes + (long)count * DirectoryEntryBytes;
        long start = directoryEnd + DigestBytes;
        if (count < 0 || start > file.Length) throw new InvalidDataException("退出快照卷目录不完整。");
        file.Position = directoryEnd;
        byte[] expected = reader.ReadBytes(DigestBytes);
        file.Position = 0;
        if (!ChecksumMatches(file, directoryEnd, expected, token)) throw new InvalidDataException("退出快照卷目录校验失败。");
        file.Position = prefixBytes;
        var entries = new (long Start, long End, long PayloadLength, byte[] Digest)[count];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            long length = reader.ReadInt64();
            long payloadLength = reader.ReadInt64();
            if (length <= 0 || length > long.MaxValue - start || payloadLength < MinimumVolumeBytes)
                throw new InvalidDataException("退出快照卷数据长度无效。");
            long end = start + length;
            entries[i] = (start, end, payloadLength, reader.ReadBytes(DigestBytes));
            start = end;
        }
        return entries;
    }

    private static void WriteVolume(BinaryWriter writer, ExitSnapshotVolume snapshot, CancellationToken token)
    {
        var volume = snapshot.Volume;
        Span<byte> ascii = stackalloc byte[1024];
        WriteText(writer, volume.Id, ascii);
        WriteText(writer, volume.Root, ascii);
        WriteText(writer, volume.Label, ascii);
        writer.Write(volume.TotalBytes);
        writer.Write(volume.PhysicalDisks?.Length ?? -1);
        if (volume.PhysicalDisks is { } disks)
            foreach (string disk in disks) WriteText(writer, disk, ascii);
        writer.Write(snapshot.Position.JournalId);
        writer.Write(snapshot.Position.NextUsn);

        foreach (var layer in snapshot.Layers)
            if (!layer.HasIdentity) throw new ArgumentException("退出快照要求完整的 NTFS 文件身份。", nameof(snapshot));
        writer.Write(snapshot.Layers.Sum(layer => layer.VisibleCount));

        // 只为实际有可见子项的目录分配编号。各层同一父目录共享定义，行仅引用编号。
        var directories = new Dictionary<(ulong Reference, string Path), int>();
        var directoryIds = snapshot.Layers.Select(layer => new int[layer.Directories.Length]).ToArray();
        Span<char> scratch = stackalloc char[255];
        ulong recordNumber = 0;
        foreach (var (layerId, row) in EnumerateRows(snapshot.Layers, token))
        {
            var layer = snapshot.Layers[layerId];
            ref readonly var record = ref layer.Records[row];
            ref int cached = ref directoryIds[layerId][record.Directory];
            if (cached == 0)
            {
                var directory = (layer.DirectoryReferences![record.Directory], layer.Directories[record.Directory]);
                int id = directories.Count;
                if (directories.TryAdd(directory, id))
                {
                    writer.Write7BitEncodedInt(id);
                    writer.Write(directory.Item1);
                    WriteText(writer, directory.Item2, ascii);
                }
                else
                {
                    id = directories[directory];
                    writer.Write7BitEncodedInt(id);
                }
                cached = id + 1;
            }
            else writer.Write7BitEncodedInt(cached - 1);

            // MFT 槽号已按升序归并，通常只需一个字节记录增量，高位代际单独存储。
            ulong reference = layer.FileReferences![row];
            ulong nextRecordNumber = reference & RecordNumberMask;
            writer.Write7BitEncodedInt64((long)(nextRecordNumber - recordNumber));
            writer.Write7BitEncodedInt((int)(reference >> 48));
            recordNumber = nextRecordNumber;

            long? created = layer.CreatedUtcTicks(row);
            byte flags = (byte)((record.IsDirectory ? 1 : 0) | (record.HasModified ? 2 : 0) | (record.HasSize ? 4 : 0)
                | (created.HasValue ? 8 : 0));
            writer.Write(flags);
            if (record.HasSize) writer.Write7BitEncodedInt64(layer.Size(row));
            if (record.HasModified) writer.Write(record.Modified);
            if (created is long ticks) writer.Write(ticks);
            WriteText(writer, layer.Name(row, scratch), ascii);
        }
    }

    private static IEnumerable<(int Layer, int Row)> EnumerateRows(IReadOnlyList<SearchIndex> layers, CancellationToken token)
    {
        if (layers.Count == 1)
        {
            var layer = layers[0];
            for (int row = NextVisible(layer, 0, token); row < layer.Count; row = NextVisible(layer, row + 1, token))
                yield return (0, row);
            yield break;
        }

        // 归并各层已有的文件身份顺序，恢复时可直接建立查询排序，无需再重排全部文件身份。
        var queue = new PriorityQueue<(int Layer, int Row), ulong>(layers.Count, ReferenceOrder);
        for (int i = 0; i < layers.Count; i++) Enqueue(i, 0);
        while (queue.TryDequeue(out var next, out _))
        {
            yield return next;
            Enqueue(next.Layer, next.Row + 1);
        }

        void Enqueue(int layerId, int start)
        {
            var layer = layers[layerId];
            int row = NextVisible(layer, start, token);
            if (row < layer.Count) queue.Enqueue((layerId, row), layer.FileReferences![row]);
        }
    }

    private static int NextVisible(SearchIndex layer, int row, CancellationToken token)
    {
        for (; row < layer.Count; row++)
        {
            if ((row & 16383) == 0) token.ThrowIfCancellationRequested();
            if (layer.IsVisible(row)) break;
        }
        return row;
    }

    private static ExitSnapshotVolume ReadVolume(Stream file, long end, CancellationToken token,
        Action<NtfsVolume, bool, int>? progress, bool hasCreationMetadata)
    {
        using var reader = new BinaryReader(file, Encoding.UTF8, leaveOpen: true);
        string id = ReadString(reader, end);
        string root = ReadString(reader, end);
        string label = ReadString(reader, end);
        long totalBytes = reader.ReadInt64();
        int diskCount = reader.ReadInt32();
        if (diskCount < -1 || diskCount > end - file.Position)
            throw new InvalidDataException("退出快照卷的物理磁盘数量无效。");
        string[]? disks = diskCount < 0 ? null : new string[diskCount];
        if (disks is not null)
            for (int i = 0; i < disks.Length; i++) disks[i] = ReadString(reader, end);
        var position = new JournalPosition(reader.ReadUInt64(), reader.ReadInt64());
        int rowCount = reader.ReadInt32();
        if (rowCount < 0 || rowCount > (end - file.Position) / MinimumRowBytes)
            throw new InvalidDataException("退出快照记录数无效。");
        var volume = new NtfsVolume(id, root, totalBytes, label, disks);
        progress?.Invoke(volume, false, rowCount);
        var builder = new IndexBuilder(rowCount, fixedSize: true);
        Span<char> scratch = stackalloc char[255];
        Span<byte> bytes = stackalloc byte[1024];
        char[]? longNameBuffer = null;
        ulong recordNumber = 0;
        for (int row = 0; row < rowCount; row++)
        {
            if ((row & 16383) == 0) token.ThrowIfCancellationRequested();
            if (end - file.Position < MinimumRowBytes) throw new InvalidDataException("退出快照文件记录不完整。");
            int directoryId = reader.Read7BitEncodedInt();
            if (directoryId < 0 || directoryId > builder.DirectoryCount) throw new InvalidDataException("退出快照父目录编号无效。");
            if (directoryId == builder.DirectoryCount)
            {
                ulong parentReference = reader.ReadUInt64();
                builder.AddDirectory(ReadString(reader, end), parentReference);
            }
            ulong delta = (ulong)reader.Read7BitEncodedInt64();
            int sequence = reader.Read7BitEncodedInt();
            if (delta > RecordNumberMask - recordNumber || (uint)sequence > ushort.MaxValue)
                throw new InvalidDataException("退出快照文件身份无效。");
            recordNumber += delta;
            ulong reference = ((ulong)sequence << 48) | recordNumber;

            byte flags = reader.ReadByte();
            if (flags > (hasCreationMetadata ? 15 : 7)) throw new InvalidDataException("退出快照文件标志无效。");
            long? size = (flags & 4) != 0 ? reader.Read7BitEncodedInt64() : null;
            long? modified = (flags & 2) != 0 ? reader.ReadInt64() : null;
            long? created = (flags & 8) != 0 ? reader.ReadInt64() : null;
            var (chars, utf16) = ReadTextHeader(reader, end);
            if (size < 0 || chars is 0 or > ushort.MaxValue)
                throw new InvalidDataException("退出快照文件记录无效。");
            scoped Span<char> name = chars <= scratch.Length ? scratch[..chars] : (longNameBuffer ??= new char[ushort.MaxValue]).AsSpan(0, chars);
            ReadText(file, name, utf16, bytes);
            builder.AddRecord(directoryId, name, size, modified, (flags & 1) != 0, reference, created);
        }
        if (file.Position != end || file.ReadByte() != -1) throw new InvalidDataException("退出快照卷解压长度不符。");
        progress?.Invoke(volume, true, rowCount);
        return new(volume, position, [builder.Build(token)]) { HasCreationMetadata = hasCreationMetadata };
    }

    private static void WriteText(BinaryWriter writer, ReadOnlySpan<char> text, Span<byte> buffer)
    {
        if (text.Length <= buffer.Length && Ascii.FromUtf16(text, buffer, out int written) == OperationStatus.Done)
        {
            writer.Write7BitEncodedInt(text.Length << 1);
            writer.Write(buffer[..written]);
        }
        else
        {
            // Unicode 路径和未配对的代理项以原始代码单元保存。超长文本也走此路径，避免临时分配。
            writer.Write7BitEncodedInt((text.Length << 1) | 1);
            writer.Write(MemoryMarshal.AsBytes(text));
        }
    }

    private static (int Length, bool Utf16) ReadTextHeader(BinaryReader reader, long end)
    {
        uint code = (uint)reader.Read7BitEncodedInt();
        int length = (int)(code >> 1);
        bool utf16 = (code & 1) != 0;
        if ((long)length * (utf16 ? sizeof(char) : 1) > end - reader.BaseStream.Position)
            throw new InvalidDataException("退出快照文本不完整。");
        return (length, utf16);
    }

    private static string ReadString(BinaryReader reader, long end)
    {
        var (length, utf16) = ReadTextHeader(reader, end);
        return string.Create(length, (reader.BaseStream, utf16), static (characters, source) =>
            ReadText(source.BaseStream, characters, source.utf16, stackalloc byte[1024]));
    }

    private static void ReadText(Stream stream, Span<char> characters, bool utf16, Span<byte> buffer)
    {
        if (utf16)
        {
            SnapshotStore.ReadUtf16(stream, characters);
            return;
        }
        while (!characters.IsEmpty)
        {
            int length = Math.Min(characters.Length, buffer.Length);
            stream.ReadExactly(buffer[..length]);
            if (Ascii.ToUtf16(buffer[..length], characters, out _) != OperationStatus.Done)
                throw new InvalidDataException("退出快照 ASCII 文本无效。");
            characters = characters[length..];
        }
    }

    private static bool ChecksumMatches(Stream stream, long end, ReadOnlySpan<byte> expected, CancellationToken token)
    {
        Span<byte> actual = stackalloc byte[DigestBytes];
        SnapshotStore.CalculateChecksum(stream, end, actual, token);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static void RequireLittleEndian()
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("退出快照要求小端平台。");
    }

    // 复用原记录编码中的长度检查；统计的是解压后实际消费的字节，底层预读不计入。
    private sealed class PayloadStream(Stream stream) : Stream
    {
        private long position;
        public override bool CanRead => stream.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => stream.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => position; set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            int read = stream.Read(buffer);
            position += read;
            return read;
        }
        public override int ReadByte()
        {
            int value = stream.ReadByte();
            if (value >= 0) position++;
            return value;
        }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            stream.Write(buffer);
            position += buffer.Length;
        }
    }

    // 每个读取任务维护独立偏移，所有任务共享同一个已打开的快照句柄。
    private sealed class VolumeStream(SafeFileHandle file, long start, long length) : Stream
    {
        private long position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position
        {
            get => position;
            set
            {
                ArgumentOutOfRangeException.ThrowIfNegative(value);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, length);
                position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int count = (int)Math.Min(buffer.Length, length - position);
            if (count == 0) return 0;
            int read = RandomAccess.Read(file, buffer[..count], start + position);
            position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => checked(position + offset),
                SeekOrigin.End => checked(length + offset),
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            return position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
