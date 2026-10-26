using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NtfsSearch.Core;

public static class SnapshotStore
{
    private static readonly byte[] Magic = "NTSIDX03"u8.ToArray();

    public static void Save(SearchIndex index, string path, CancellationToken cancellationToken = default)
    {
        SaveChecked(path, (writer, crypto) =>
        {
            writer.Write(Magic);
            writer.Write(index.Count); writer.Write(index.Directories.Length);
            writer.Write(index.Names.Chunks.Count); writer.Write(index.Names.LastUsed);
            foreach (string directory in index.Directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(directory.Length);
                crypto.Write(MemoryMarshal.AsBytes(directory.AsSpan()));
            }
            foreach (char[] chunk in index.Names.Chunks)
            {
                cancellationToken.ThrowIfCancellationRequested();
                writer.Write(chunk.Length);
                crypto.Write(MemoryMarshal.AsBytes(chunk.AsSpan()));
            }
            for (int start = 0; start < index.Count; start += 16_384)
            {
                cancellationToken.ThrowIfCancellationRequested();
                crypto.Write(MemoryMarshal.AsBytes(index.Records.AsSpan(start, Math.Min(16_384, index.Count - start))));
            }
            var savedOrders = new int[4][];
            for (int field = 0; field < 4; field++)
            {
                int[] order = index.MaterializeSortOrder((SortField)field, cancellationToken);
                savedOrders[field] = order;
                int alias = -1;
                for (int previous = 0; previous < field; previous++)
                    if (ReferenceEquals(order, savedOrders[previous])) { alias = previous; break; }
                writer.Write(alias); writer.Write(index.KnownCount((SortField)field));
                if (alias >= 0) continue;
                for (int start = 0; start < order.Length; start += 65536)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    crypto.Write(MemoryMarshal.AsBytes(order.AsSpan(start, Math.Min(65536, order.Length - start))));
                }
            }
            writer.Write(index.HasIdentity);
            if (index.HasIdentity)
            {
                WriteReferences(crypto, index.FileReferences!, cancellationToken);
                WriteReferences(crypto, index.DirectoryReferences!, cancellationToken);
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
        bool v3 = magic.AsSpan().SequenceEqual(Magic);
        bool hasOrders = v3 || magic.AsSpan().SequenceEqual("NTSIDX02"u8);
        if (!hasOrders && !magic.AsSpan().SequenceEqual("NTSIDX01"u8)) throw new InvalidDataException("未知快照格式。");
        int count = ReadCount(reader, payload, 32), directoryCount = ReadCount(reader, payload, 4);
        int chunks = ReadCount(reader, payload, 4), lastUsed = ReadCount(reader, NameArena.ChunkChars, 1);
        var directories = new string[directoryCount];
        for (int i = 0; i < directoryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chars = ReadCount(reader, payload - stream.Position, 2);
            if (chars > 32768) throw new InvalidDataException("快照目录路径过长。");
            char[] text = new char[chars]; stream.ReadExactly(MemoryMarshal.AsBytes(text.AsSpan()));
            directories[i] = new(text);
            if (chars == 0 || text[^1] != '\\') throw new InvalidDataException("快照目录格式错误。");
        }
        var arena = new NameArena { LastUsed = lastUsed };
        for (int i = 0; i < chunks; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int chars = ReadCount(reader, Math.Min(NameArena.ChunkChars, (payload - stream.Position) / 2), 1);
            if (i < chunks - 1 && chars != NameArena.ChunkChars || i == chunks - 1 && chars != lastUsed)
                throw new InvalidDataException("快照名称块长度错误。");
            var chunk = new char[chars]; stream.ReadExactly(MemoryMarshal.AsBytes(chunk.AsSpan())); arena.Chunks.Add(chunk);
        }
        if (count > (payload - stream.Position) / 32) throw new InvalidDataException("快照记录数越界。");
        var records = new IndexRecord[count];
        for (int start = 0; start < count; start += 16_384)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stream.ReadExactly(MemoryMarshal.AsBytes(records.AsSpan(start, Math.Min(16_384, count - start))));
        }
        foreach (ref readonly var r in records.AsSpan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long chunk = r.NameOffset / NameArena.ChunkChars, offset = r.NameOffset % NameArena.ChunkChars;
            if (r.Directory < 0 || r.Directory >= directoryCount || r.NameOffset < 0 || chunk >= chunks
                || offset + r.NameLength > arena.Chunks[(int)chunk].Length || (r.Flags & ~7) != 0 || r.NameLength == 0)
                throw new InvalidDataException("快照记录引用越界。");
        }
        int[][]? orders = null;
        int[]? known = null;
        if (hasOrders)
        {
            orders = new int[4][]; known = new int[4];
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
                    var order = new int[count];
                    for (int start = 0; start < count; start += 65536)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        stream.ReadExactly(MemoryMarshal.AsBytes(order.AsSpan(start, Math.Min(65536, count - start))));
                    }
                    Array.Clear(seen);
                    int checkedIds = 0;
                    foreach (int id in order)
                    {
                        if ((checkedIds++ & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        if (id < 0 || id >= count || (seen[id / 8] & (1 << (id % 8))) != 0)
                            throw new InvalidDataException("排序缓存包含重复或越界 ID。");
                        seen[id / 8] |= (byte)(1 << (id % 8));
                    }
                    orders[field] = order;
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
        if (v3 && reader.ReadBoolean())
        {
            references = ReadReferences(stream, count, payload, cancellationToken);
            parents = ReadReferences(stream, directoryCount, payload, cancellationToken);
            for (int i = 1; i < count; i++)
                if (SearchIndex.CompareReference(references[i - 1], references[i]) > 0)
                    throw new InvalidDataException("文件身份分组顺序无效。");
        }
        if (stream.Position != payload) throw new InvalidDataException("快照包含多余数据。");
        var index = new SearchIndex(arena, records, directories, cancellationToken, progress, orders, known, references, parents);
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
        if (!magic.SequenceEqual(Magic) && !magic.SequenceEqual("NTSIDX02"u8) && !magic.SequenceEqual("NTSIDX01"u8))
            throw new InvalidDataException("未知快照格式。");
    }

    internal static long ValidatePayload(FileStream stream, CancellationToken cancellationToken)
    {
        long payload = stream.Length - 32;
        if (payload < 24) throw new InvalidDataException("快照不完整。");
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            var buffer = new byte[1 << 20];
            while (stream.Position < payload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, payload - stream.Position));
                if (read == 0) throw new InvalidDataException("快照提前结束。");
                hash.AppendData(buffer, 0, read);
            }
            Span<byte> expected = stackalloc byte[32]; stream.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(expected, hash.GetHashAndReset()))
                throw new InvalidDataException("快照校验失败。可尝试加载上一代 .bak；原文件未修改。");
        }
        return payload;
    }

    private static void WriteReferences(Stream stream, ulong[] values, CancellationToken token)
    {
        for (int start = 0; start < values.Length; start += 65536)
        {
            token.ThrowIfCancellationRequested();
            stream.Write(MemoryMarshal.AsBytes(values.AsSpan(start, Math.Min(65536, values.Length - start))));
        }
    }

    private static ulong[] ReadReferences(Stream stream, int count, long payload, CancellationToken token)
    {
        if (count > (payload - stream.Position) / sizeof(ulong)) throw new InvalidDataException("身份数组越界。");
        var result = new ulong[count];
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
