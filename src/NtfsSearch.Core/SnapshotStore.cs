using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NtfsSearch.Core;

public static class SnapshotStore
{
    private static readonly byte[] Magic = "NTSIDX01"u8.ToArray();

    public static void Save(SearchIndex index, string path, CancellationToken cancellationToken = default)
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
                    writer.Flush(); crypto.FlushFinalBlock();
                }
                stream.Write(hash.Hash!);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static SearchIndex Load(string path, CancellationToken cancellationToken = default, IProgress<string>? progress = null)
    {
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("快照要求小端平台。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
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
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("未知快照格式。");
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
        if (stream.Position != payload) throw new InvalidDataException("快照包含多余数据。");
        foreach (ref readonly var r in records.AsSpan())
        {
            cancellationToken.ThrowIfCancellationRequested();
            long chunk = r.NameOffset / NameArena.ChunkChars, offset = r.NameOffset % NameArena.ChunkChars;
            if (r.Directory < 0 || r.Directory >= directoryCount || r.NameOffset < 0 || chunk >= chunks
                || offset + r.NameLength > arena.Chunks[(int)chunk].Length || (r.Flags & ~7) != 0 || r.NameLength == 0)
                throw new InvalidDataException("快照记录引用越界。");
        }
        return new(arena, records, directories, cancellationToken, progress);
    }

    private static int ReadCount(BinaryReader reader, long available, int unit)
    {
        int value = reader.ReadInt32();
        if (value < 0 || value > available / unit) throw new InvalidDataException("快照长度字段越界。");
        return value;
    }
}
