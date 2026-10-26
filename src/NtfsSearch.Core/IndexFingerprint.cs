using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NtfsSearch.Core;

// 检查点身份与快照存储布局分开：旧格式保留来源字节，新格式仅散列逻辑内容。
internal static class IndexFingerprint
{
    internal static byte[] Legacy(SearchIndex index, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return index.IdentitySource.CheckpointFingerprint
            ?? throw new InvalidDataException("基线未保留旧版检查点所需的来源指纹。");
    }

    internal static byte[] LegacyFromSerialized(SearchIndex index, Stream source, long recordsPosition,
        IReadOnlyList<(long Offset, int Length)> nameChunks, CancellationToken token)
    {
        index = index.IdentitySource;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string directory in index.Directories)
        {
            token.ThrowIfCancellationRequested(); hash.AppendData(MemoryMarshal.AsBytes(directory.AsSpan()));
        }
        byte[] buffer = new byte[65536 * 32];
        long originalPosition = source.Position;
        try
        {
            for (int start = 0; start < index.Count; start += 65536)
            {
                token.ThrowIfCancellationRequested();
                int count = Math.Min(65536, index.Count - start);
                source.Position = recordsPosition + (long)start * 32;
                source.ReadExactly(buffer.AsSpan(0, count * 32));
                hash.AppendData(buffer.AsSpan(0, count * 32));
                hash.AppendData(MemoryMarshal.AsBytes(index.FileReferences!.AsSpan(start, count)));
            }
            hash.AppendData(MemoryMarshal.AsBytes(index.DirectoryReferences!.AsSpan()));
            foreach (var (offset, length) in nameChunks)
            {
                source.Position = offset;
                long remaining = (long)length * sizeof(char);
                while (remaining > 0)
                {
                    token.ThrowIfCancellationRequested();
                    int count = (int)Math.Min(buffer.Length, remaining);
                    source.ReadExactly(buffer.AsSpan(0, count)); hash.AppendData(buffer.AsSpan(0, count)); remaining -= count;
                }
            }
            return hash.GetHashAndReset();
        }
        finally { source.Position = originalPosition; }
    }

    internal static byte[] Canonical(SearchIndex index, CancellationToken token)
    {
        index = index.IdentitySource;
        if (index.CanonicalCheckpointFingerprint is { } cached) return cached;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var output = new HashBuffer(hash);
        output.Bytes("NTS-CANONICAL-1"u8); output.Int32(index.Count); output.Int32(index.Directories.Length);
        for (int d = 0; d < index.Directories.Length; d++)
        {
            token.ThrowIfCancellationRequested(); output.Text(index.Directories[d].AsSpan());
            output.UInt64(index.DirectoryReferences![d]);
        }
        Span<char> scratch = stackalloc char[255];
        for (int id = 0; id < index.Count; id++)
        {
            if ((id & 65535) == 0) token.ThrowIfCancellationRequested();
            ref readonly var record = ref index.Records[id];
            output.UInt64(index.FileReferences![id]); output.Int32(record.Directory);
            output.Int32((record.IsDirectory ? 1 : 0) | (record.HasSize ? 2 : 0) | (record.HasModified ? 4 : 0));
            // 未知值的物理占位不属于逻辑元数据，跨快照格式统一为零。
            output.Int64(record.HasSize ? index.Size(id) : 0); output.Int64(record.HasModified ? record.Modified : 0);
            output.Text(index.Name(id, scratch));
        }
        output.Flush(); token.ThrowIfCancellationRequested();
        return index.CanonicalCheckpointFingerprint = hash.GetHashAndReset();
    }

    private sealed class HashBuffer(IncrementalHash hash)
    {
        private readonly byte[] buffer = new byte[65536];
        private int used;
        internal void Flush() { if (used > 0) { hash.AppendData(buffer.AsSpan(0, used)); used = 0; } }
        private Span<byte> Reserve(int count)
        {
            if (buffer.Length - used < count) Flush();
            var span = buffer.AsSpan(used, count); used += count; return span;
        }
        internal void Int32(int value) => BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
        internal void UInt64(ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(Reserve(8), value);
        internal void Int64(long value) => BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
        internal void Text(ReadOnlySpan<char> value) { Int32(value.Length); Bytes(MemoryMarshal.AsBytes(value)); }
        internal void Bytes(ReadOnlySpan<byte> value)
        {
            while (!value.IsEmpty)
            {
                int count = Math.Min(buffer.Length - used, value.Length);
                value[..count].CopyTo(Reserve(count)); value = value[count..];
                if (used == buffer.Length) Flush();
            }
        }
    }
}
