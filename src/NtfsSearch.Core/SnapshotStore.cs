using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

public static class SnapshotStore
{
    private static ReadOnlySpan<byte> CompactMagic => "NTSIDX04"u8;
    private static ReadOnlySpan<byte> CreationMagic => "NTSIDX05"u8;

    private static bool IsCompactFormat(ReadOnlySpan<byte> magic)
        => magic.SequenceEqual(CompactMagic) || magic.SequenceEqual(CreationMagic);

    // 调用方已加载并验证正文后，用头部探测决定是否需要进行同代编码迁移。
    internal static bool IsCurrentFormat(string path, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8);
        Span<byte> header = stackalloc byte[8];
        int read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
        cancellationToken.ThrowIfCancellationRequested();
        return read == header.Length && IsCompactFormat(header);
    }

    public static void Save(SearchIndex index, string path, CancellationToken cancellationToken = default)
    {
        SaveChecked(path, (writer, crypto) =>
        {
            writer.Write(index.CreationTimes is null ? CompactMagic : CreationMagic);
            writer.Write(index.Count); writer.Write(index.Directories.Length);
            writer.Write(index.Names.Chunks.Count); writer.Write(index.Names.LastUsed);
            writer.Write(index.Names.Count);
            writer.Write(index.BigSizes.Length);
            writer.Write(index.Names.AsciiChunks.Count); writer.Write(index.Names.AsciiLastUsed);
            foreach (string directory in index.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WriteUtf16(writer, directory);
            }
            WriteArray<ulong>(crypto, index.Names.Descriptors, cancellationToken);
            foreach (char[] chunk in index.Names.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(chunk.Length);
                crypto.Write(MemoryMarshal.AsBytes(chunk.AsSpan()));
            }
            foreach (byte[] chunk in index.Names.AsciiChunks)
            {
                cancellationToken.ThrowIfCancellationRequested(); writer.Write(chunk.Length); crypto.Write(chunk);
            }
            WriteArray<long>(crypto, index.BigSizes, cancellationToken);
            for (int start = 0; start < index.Count; start += 16_384)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crypto.Write(MemoryMarshal.AsBytes(index.Records.AsSpan(start, Math.Min(16_384, index.Count - start))));
            }
            var savedOrders = new SortPermutation[4];
            var orderBlock = new int[Math.Min(65536, index.Count)];
            for (int field = 0; field < 4; field++)
            {
                var order = index.SortOrder((SortField)field);
                savedOrders[field] = order;
                int alias = -1;
                // 修正视图的有效排列由两路归并产生，不能仅以基线共享引用推断alias。
                if (!index.HasOrderCorrections)
                    for (int previous = 0; previous < field; previous++)
                        if (ReferenceEquals(order, savedOrders[previous])) { alias = previous; break; }
                writer.Write(alias); writer.Write(index.KnownCount((SortField)field));
                if (alias >= 0) continue;
                using var ids = index.EnumerateSortOrder((SortField)field, cancellationToken).GetEnumerator();
                for (int start = 0; start < index.Count; start += orderBlock.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int length = Math.Min(orderBlock.Length, index.Count - start);
                    for (int i = 0; i < length; i++)
                    {
                        if (!ids.MoveNext()) throw new InvalidOperationException("排序排列提前结束。");
                        orderBlock[i] = ids.Current;
                    }
                    crypto.Write(MemoryMarshal.AsBytes(orderBlock.AsSpan(0, length)));
                }
            }
            writer.Write(index.HasIdentity);
            if (index.HasIdentity)
            {
                WriteArray<ulong>(crypto, index.FileReferences!, cancellationToken);
                WriteArray<ulong>(crypto, index.DirectoryReferences!, cancellationToken);
            }
            // 改过目录视图的另存文件不再是原物理基线，不能沿用IdentitySource的旧指纹。
            byte[]? legacyFingerprint = index.CheckpointFingerprint;
            writer.Write(legacyFingerprint is not null);
            if (legacyFingerprint is not null) writer.Write(legacyFingerprint);
            if (index.CreationTimes is { } times)
            {
                WriteArray<long>(crypto, times, cancellationToken);
                WriteArray<ulong>(crypto, index.KnownCreationTimes!, cancellationToken);
            }
        }, cancellationToken);
    }

    internal static void SaveChecked(string path, Action<BinaryWriter, Stream> write, CancellationToken cancellationToken)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("快照要求小端平台。");
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20))
            using (var hash = SHA256.Create())
            {
                using (var crypto = new CryptoStream(stream, hash, CryptoStreamMode.Write, leaveOpen: true))
                using (var writer = new BinaryWriter(crypto, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    write(writer, crypto);
                    writer.Flush(); crypto.FlushFinalBlock();
                }
                stream.Write(hash.Hash!);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.Publish(temporary, path, path + ".bak", cancellationToken);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static SearchIndex Load(string path, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("快照要求小端平台。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        long payload = ValidatePayload(stream, cancellationToken);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var magic = reader.ReadBytes(8);
        bool hasCreation = magic.AsSpan().SequenceEqual(CreationMagic);
        bool compact = hasCreation || magic.AsSpan().SequenceEqual(CompactMagic);
        bool hasIdentityField = compact || magic.AsSpan().SequenceEqual("NTSIDX03"u8);
        bool hasOrders = hasIdentityField || magic.AsSpan().SequenceEqual("NTSIDX02"u8);
        if (!hasOrders && !magic.AsSpan().SequenceEqual("NTSIDX01"u8)) throw new InvalidDataException("未知快照格式。");
        int recordSize = compact ? 20 : 32;
        int count = ReadCount(reader, payload, recordSize), directoryCount = ReadCount(reader, payload, 4);
        int chunks = ReadCount(reader, payload, 4), lastUsed = ReadCount(reader, NameArena.ChunkChars, 1);
        int nameCount = compact ? ReadCount(reader, payload - stream.Position, sizeof(ulong)) : 0;
        int bigSizeCount = compact ? ReadCount(reader, payload - stream.Position, sizeof(long)) : 0;
        int asciiChunks = compact ? ReadCount(reader, payload - stream.Position, 4) : 0;
        int asciiLastUsed = compact ? ReadCount(reader, NameArena.ChunkChars, 1) : 0;
        var directories = new string[directoryCount];
        for (int i = 0; i < directoryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chars = ReadCount(reader, payload - stream.Position, 2);
            if (chars > 32768) throw new InvalidDataException("快照目录路径过长。");
            if (chars == 0) throw new InvalidDataException("快照目录格式错误。");
            directories[i] = string.Create(chars, stream,
                static (span, source) => ReadUtf16(source, span));
            if (directories[i][^1] != '\\') throw new InvalidDataException("快照目录格式错误。");
        }
        var arena = new NameArena { LastUsed = lastUsed, AsciiLastUsed = asciiLastUsed };
        if (compact) { arena.Descriptors = ReadArray<ulong>(stream, nameCount, payload, cancellationToken); arena.Count = nameCount; }
        var legacyChunks = new List<(long Offset, int Length)>(compact ? 0 : chunks);
        for (int i = 0; i < chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chars = ReadCount(reader, Math.Min(NameArena.ChunkChars, (payload - stream.Position) / 2), 1);
            if (i < chunks - 1 && chars != NameArena.ChunkChars || i == chunks - 1 && chars != lastUsed)
                throw new InvalidDataException("快照名称块长度错误。");
            if (compact)
            {
                var chunk = IndexArrays.Persistent<char>(chars); stream.ReadExactly(MemoryMarshal.AsBytes(chunk.AsSpan())); arena.Chunks.Add(chunk);
            }
            else { legacyChunks.Add((stream.Position, chars)); stream.Position += (long)chars * sizeof(char); }
        }
        if (chunks == 0 && lastUsed != 0) throw new InvalidDataException("快照名称块长度错误。");
        for (int i = 0; i < asciiChunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int bytes = ReadCount(reader, Math.Min(NameArena.ChunkChars, payload - stream.Position), 1);
            if (i < asciiChunks - 1 && bytes != NameArena.ChunkChars || i == asciiChunks - 1 && bytes != asciiLastUsed)
                throw new InvalidDataException("快照ASCII名称块长度错误。");
            var chunk = IndexArrays.Persistent<byte>(bytes); stream.ReadExactly(chunk); arena.AsciiChunks.Add(chunk);
        }
        if (asciiChunks == 0 && asciiLastUsed != 0) throw new InvalidDataException("快照ASCII名称块长度错误。");
        if (compact) arena.ValidateReferences(cancellationToken);
        long[] bigSizes = compact ? ReadArray<long>(stream, bigSizeCount, payload, cancellationToken) : [];
        foreach (long size in bigSizes)
            if (size <= int.MaxValue) throw new InvalidDataException("快照大小旁表包含无效值。");
        if (count > (payload - stream.Position) / recordSize) throw new InvalidDataException("快照记录数越界。");
        long recordsPosition = stream.Position;
        var records = IndexArrays.Persistent<IndexRecord>(count);
        using var legacyNames = compact ? null : new MappedSnapshotNames(stream, legacyChunks);
        if (compact)
        {
            for (int start = 0; start < count; start += 16_384)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.ReadExactly(MemoryMarshal.AsBytes(records.AsSpan(start, Math.Min(16_384, count - start))));
            }
        }
        else
        {
            // 旧名称只映射只读视图；每批转换直接写新行，避免同时保留两套全名称和全记录。
            arena.LastUsed = 0;
            var sizeTable = new SizeTable();
            var batch = new byte[32 * 16_384];
            for (int start = 0; start < count; start += 16_384)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int rows = Math.Min(16_384, count - start);
                stream.ReadExactly(batch.AsSpan(0, rows * 32));
                for (int i = 0; i < rows; i++)
                {
                    var raw = batch.AsSpan(i * 32, 32);
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(raw);
                    long size = BinaryPrimitives.ReadInt64LittleEndian(raw[8..]);
                    long modified = BinaryPrimitives.ReadInt64LittleEndian(raw[16..]);
                    int directory = BinaryPrimitives.ReadInt32LittleEndian(raw[24..]);
                    ushort length = BinaryPrimitives.ReadUInt16LittleEndian(raw[28..]);
                    ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(raw[30..]);
                    if (directory < 0 || directory >= directoryCount || (flags & ~7) != 0 || (flags & 2) != 0 && size < 0)
                        throw new InvalidDataException("快照记录引用越界。");
                    records[start + i] = new(arena.Add(legacyNames!.Get(offset, length), (flags & 1) != 0), sizeTable.Add((flags & 3) == 2 ? size : -1),
                        modified, directory, (flags & 1) != 0, (flags & 4) != 0);
                }
            }
            arena.Seal();
            bigSizes = sizeTable.Freeze();
        }
        foreach (ref readonly var r in records.AsSpan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (r.Directory >= directoryCount || r.NameId >= arena.Count || r.IsDirectory && (r.HasSize || arena.IsAscii(r.NameId))
                || r.HasSize && r.SizeCode > int.MaxValue && (r.SizeCode & int.MaxValue) >= bigSizes.Length)
                throw new InvalidDataException("快照记录引用越界。");
        }
        SortPermutation[]? orders = null;
        int[]? known = null;
        if (hasOrders)
        {
            orders = new SortPermutation[4]; known = new int[4];
            var seen = new byte[(int)(((long)count + 7) / 8)];
            for (int field = 0; field < 4; field++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int alias = reader.ReadInt32(), knownCount = reader.ReadInt32();
                if (alias < -1 || alias >= field || knownCount < 0 || knownCount > count
                    || field < 2 && knownCount != count) throw new InvalidDataException("排序缓存头无效。");
                known[field] = knownCount;
                if (alias >= 0) orders[field] = orders[alias];
                else
                {
                    if (count > (payload - stream.Position) / sizeof(int)) throw new InvalidDataException("排序缓存越界。");
                    orders[field] = SortPermutation.Read(stream, count, seen, cancellationToken);
                }
                for (int i = 0; i < count; i++)
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (SearchIndex.IsKnown(records[orders[field][i]], (SortField)field) != (i < knownCount))
                        throw new InvalidDataException("排序缓存的已知元数据边界不符。");
                }
            }
        }
        ulong[]? references = null, parents = null;
        if (hasIdentityField && reader.ReadBoolean())
        {
            references = ReadArray<ulong>(stream, count, payload, cancellationToken);
            parents = ReadArray<ulong>(stream, directoryCount, payload, cancellationToken);
            for (int i = 1; i < count; i++)
                if (SearchIndex.CompareReference(references[i - 1], references[i]) > 0)
                    throw new InvalidDataException("文件身份分组顺序无效。");
        }
        byte[]? legacyFingerprint = null;
        if (compact && reader.ReadBoolean())
        {
            legacyFingerprint = reader.ReadBytes(32);
            if (legacyFingerprint.Length != 32) throw new InvalidDataException("快照来源指纹不完整。");
        }
        long[]? creationTimes = null;
        ulong[]? knownCreationTimes = null;
        if (hasCreation)
        {
            creationTimes = ReadArray<long>(stream, count, payload, cancellationToken);
            knownCreationTimes = ReadArray<ulong>(stream, (int)(((long)count + 63) / 64), payload, cancellationToken);
            if (count % 64 != 0 && (knownCreationTimes[^1] >> (count % 64)) != 0)
                throw new InvalidDataException("快照创建时间位图越界。");
        }
        if (stream.Position != payload) throw new InvalidDataException("快照包含多余数据。");
        var index = new SearchIndex(arena, records, directories, cancellationToken, progress, orders, known, references, parents,
            bigSizes: bigSizes, creationTimes: creationTimes, knownCreationTimes: knownCreationTimes);
        index.CheckpointFingerprint = legacyFingerprint;
        if (!compact && index.HasIdentity)
            index.CheckpointFingerprint = IndexFingerprint.LegacyFromSerialized(index, stream, recordsPosition, legacyChunks, cancellationToken);
        if (hasOrders)
        {
            for (int field = 0; field < 4; field++)
            {
                // 原生基本索引的未知大小/时间与路径排序等价，不重复验证同一排列。
                if (field >= 2 && known![field] == 0 && ReferenceEquals(orders![field], orders[1])) continue;
                for (int i = 1; i < count; i++)
                {
                    if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (index.Compare(orders![field][i - 1], orders[field][i], (SortField)field) > 0)
                        throw new InvalidDataException("缓存排序顺序无效，将自动回退或重建。");
                }
            }
        }
        if (hasOrders) progress?.Report("缓存排序校验完成。");
        return index;
    }

    // 迁移验证复用流式校验，不加载记录或排序数组。
    public static void ValidateChecksum(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        ValidatePayload(stream, cancellationToken);
        stream.Position = 0;
        Span<byte> magic = stackalloc byte[8]; stream.ReadExactly(magic);
        if (!IsCompactFormat(magic) && !magic.SequenceEqual("NTSIDX03"u8) && !magic.SequenceEqual("NTSIDX02"u8) && !magic.SequenceEqual("NTSIDX01"u8))
            throw new InvalidDataException("未知快照格式。");
    }

    internal static long ValidatePayload(FileStream stream, CancellationToken cancellationToken)
    {
        long payload = stream.Length - 32;
        if (payload < 24) throw new InvalidDataException("快照不完整。");
        Span<byte> actual = stackalloc byte[32], expected = stackalloc byte[32];
        CalculateChecksum(stream, payload, actual, cancellationToken);
        stream.ReadExactly(expected);
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            throw new InvalidDataException("快照校验失败。可尝试加载上一代 .bak；原文件未修改。");
        return payload;
    }

    internal static void CalculateChecksum(Stream stream, long end, Span<byte> digest, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            long remaining = end - stream.Position;
            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) throw new InvalidDataException("快照提前结束。");
                hash.AppendData(buffer, 0, read);
                remaining -= read;
            }
            hash.GetHashAndReset(digest);
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static void WriteUtf16(BinaryWriter writer, ReadOnlySpan<char> text)
    {
        writer.Write(text.Length);
        writer.Write(MemoryMarshal.AsBytes(text));
    }

    internal static void ReadUtf16(Stream stream, Span<char> text) => stream.ReadExactly(MemoryMarshal.AsBytes(text));

    private static void WriteArray<T>(Stream stream, ReadOnlySpan<T> values, CancellationToken token) where T : unmanaged
    {
        for (int start = 0; start < values.Length; start += 65536)
        {
            token.ThrowIfCancellationRequested();
            stream.Write(MemoryMarshal.AsBytes(values.Slice(start, Math.Min(65536, values.Length - start))));
        }
    }

    private static T[] ReadArray<T>(Stream stream, int count, long payload, CancellationToken token) where T : unmanaged
    {
        if (count > (payload - stream.Position) / Unsafe.SizeOf<T>()) throw new InvalidDataException("快照数组越界。");
        var result = IndexArrays.Persistent<T>(count);
        for (int start = 0; start < count; start += 65536)
        {
            token.ThrowIfCancellationRequested();
            stream.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan(start, Math.Min(65536, count - start))));
        }
        return result;
    }

    private static int ReadCount(BinaryReader reader, long available, int unit)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > available / unit) throw new InvalidDataException("快照长度字段越界。");
        return value;
    }
}
