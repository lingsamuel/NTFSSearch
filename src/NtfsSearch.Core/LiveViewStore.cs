using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace NtfsSearch.Core;

// 检查点只写小型视图清单与首次出现的增量段；基线继续使用已有卷快照。
internal static class LiveViewStore
{
    private static readonly byte[] Magic = "NTSLIVE1"u8.ToArray();
    internal static void Save(LiveVolumeIndex live, string path, long sequence, CancellationToken token)
    {
        if (sequence < live.AppliedSequence) throw new ArgumentOutOfRangeException(nameof(sequence));
        path = Path.GetFullPath(path);
        string segments = path + ".segments";
        Directory.CreateDirectory(segments);
        var layers = live.Layers;
        for (int i = 1; i < layers.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            string segment = Path.Combine(segments, layers[i].StorageId + ".ntidx");
            if (!File.Exists(segment)) SnapshotStore.Save(layers[i], segment, token);
        }
        byte[] fingerprint = Fingerprint(layers[0], token);
        SnapshotStore.SaveChecked(path, (writer, stream) =>
        {
            writer.Write(Magic); writer.Write(fingerprint); writer.Write(sequence);
            writer.Write(live.Position is not null);
            if (live.Position is { } position) { writer.Write(position.JournalId); writer.Write(position.NextUsn); }
            writer.Write(layers.Count);
            for (int i = 0; i < layers.Count; i++)
            {
                var layer = layers[i];
                WriteText(writer, stream, layer.StorageId);
                writer.Write(layer.Count);
                writer.Write(layer.HiddenRows is not null);
                if (layer.HiddenRows is { } hidden) WriteArray(stream, hidden, token);
                writer.Write(layer.Directories.Length);
                foreach (string directory in layer.Directories) { token.ThrowIfCancellationRequested(); WriteText(writer, stream, directory); }
            }
            writer.Write(live.DirectoryGraph.Count);
            foreach (var (reference, node) in live.DirectoryGraph)
            {
                token.ThrowIfCancellationRequested();
                writer.Write(reference); writer.Write(node.Parent); writer.Write(node.Deleted);
                WriteText(writer, stream, node.Name);
                writer.Write(node.RootPath is not null);
                if (node.RootPath is { } root) WriteText(writer, stream, root);
            }
        }, token);
        // 原子替换清单后，仍保留 .bak 所引用的段。更老一代段可以安全回收。
        var keep = SegmentNames(path, token).Concat(File.Exists(path + ".bak") ? SegmentNames(path + ".bak", token) : []).ToHashSet(StringComparer.Ordinal);
        foreach (string segment in Directory.EnumerateFiles(segments, "*.ntidx"))
            if (!keep.Contains(Path.GetFileNameWithoutExtension(segment))) File.Delete(segment);
    }

    internal static LiveVolumeIndex Load(SearchIndex baseIndex, string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long payload = SnapshotStore.ValidatePayload(stream, token); stream.Position = 0;
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(8).AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("未知实时检查点格式。");
        if (!CryptographicOperations.FixedTimeEquals(reader.ReadBytes(32), Fingerprint(baseIndex, token)))
            throw new InvalidDataException("实时检查点与基线身份不符。");
        long sequence = reader.ReadInt64();
        if (sequence < 0) throw new InvalidDataException("检查点序号无效。");
        JournalPosition? position = reader.ReadBoolean() ? new(reader.ReadUInt64(), reader.ReadInt64()) : null;
        int count = Count(reader, payload - stream.Position, 1);
        if (count == 0) throw new InvalidDataException("检查点缺少基线。");
        var layers = new SearchIndex[count];
        var textBuffer = new char[32768];
        for (int i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            string id = ReadText(reader, stream, payload, textBuffer);
            if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("增量段名称无效。");
            int rows = Count(reader, int.MaxValue, 1);
            string segmentRoot = path.EndsWith(".bak", StringComparison.Ordinal) ? path[..^4] : path;
            var layer = i == 0 ? baseIndex : SnapshotStore.Load(Path.Combine(segmentRoot + ".segments", id + ".ntidx"), token);
            if (rows != layer.Count) throw new InvalidDataException("检查点段记录数不符。");
            ulong[]? hidden = reader.ReadBoolean() ? ReadArray(stream, (int)(((long)layer.Count + 63) / 64), payload, token) : null;
            if (hidden is not null && layer.Count % 64 != 0 && (hidden[^1] >> (layer.Count % 64)) != 0)
                throw new InvalidDataException("检查点可见性位图越界。");
            int directories = Count(reader, payload - stream.Position, 4);
            if (directories != layer.Directories.Length) throw new InvalidDataException("检查点目录覆盖长度不符。");
            var paths = new string[directories]; bool changed = false;
            for (int d = 0; d < directories; d++)
            {
                token.ThrowIfCancellationRequested(); paths[d] = ReadText(reader, stream, payload, textBuffer, layer.Directories[d]);
                if (paths[d].Length == 0 || paths[d][^1] != '\\') throw new InvalidDataException("检查点目录路径无效。");
                changed |= paths[d] != layer.Directories[d];
            }
            layers[i] = layer.WithView(hidden, changed ? paths : layer.Directories, token, i == 0 ? null : id);
        }
        int nodes = Count(reader, payload - stream.Position, 22);
        var graph = ImmutableDictionary.CreateBuilder<ulong, LiveVolumeIndex.DirectoryNode>();
        for (int i = 0; i < nodes; i++)
        {
            token.ThrowIfCancellationRequested();
            ulong reference = reader.ReadUInt64(), parent = reader.ReadUInt64(); bool deleted = reader.ReadBoolean();
            string name = ReadText(reader, stream, payload, textBuffer);
            string? root = reader.ReadBoolean() ? ReadText(reader, stream, payload, textBuffer) : null;
            if (!graph.TryAdd(reference, new(parent, name, root, deleted))) throw new InvalidDataException("检查点包含重复目录身份。");
        }
        if (stream.Position != payload) throw new InvalidDataException("检查点长度不符。");
        return new LiveVolumeIndex(layers, graph.ToImmutable(), sequence, position);
    }

    private static IEnumerable<string> SegmentNames(string path, CancellationToken token)
    {
        // 清理只读取可信清单；失败则保留全部段，避免影响可恢复的旧检查点。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        long payload = SnapshotStore.ValidatePayload(stream, token); stream.Position = 8 + 32 + 8;
        using var reader = new BinaryReader(stream);
        if (reader.ReadBoolean()) stream.Position += 16;
        int count = Count(reader, payload - stream.Position, 1);
        var textBuffer = new char[32768];
        for (int i = 0; i < count; i++)
        {
            string id = ReadText(reader, stream, payload, textBuffer);
            if (i > 0) yield return id;
            int rows = Count(reader, int.MaxValue, 1);
            if (reader.ReadBoolean()) stream.Position += ((long)rows + 63) / 64 * 8;
            int directories = Count(reader, payload - stream.Position, 4);
            for (int d = 0; d < directories; d++)
            {
                token.ThrowIfCancellationRequested();
                int chars = TextLength(reader, stream, payload);
                stream.Position += (long)chars * sizeof(char);
            }
        }
    }

    private static byte[] Fingerprint(SearchIndex index, CancellationToken token)
    {
        index = index.IdentitySource;
        if (index.CheckpointFingerprint is { } cached) return cached;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string directory in index.Directories)
        {
            token.ThrowIfCancellationRequested();
            hash.AppendData(MemoryMarshal.AsBytes(directory.AsSpan()));
        }
        for (int start = 0; start < index.Count; start += 65536)
        {
            token.ThrowIfCancellationRequested(); int length = Math.Min(65536, index.Count - start);
            hash.AppendData(MemoryMarshal.AsBytes(index.Records.AsSpan(start, length)));
            hash.AppendData(MemoryMarshal.AsBytes(index.FileReferences!.AsSpan(start, length)));
        }
        hash.AppendData(MemoryMarshal.AsBytes(index.DirectoryReferences!.AsSpan()));
        foreach (var chunk in index.Names.Chunks) { token.ThrowIfCancellationRequested(); hash.AppendData(MemoryMarshal.AsBytes(chunk.AsSpan())); }
        return index.CheckpointFingerprint = hash.GetHashAndReset();
    }
    private static void WriteText(BinaryWriter writer, Stream stream, string text)
    {
        writer.Write(text.Length); stream.Write(MemoryMarshal.AsBytes(text.AsSpan()));
    }
    private static int TextLength(BinaryReader reader, Stream stream, long payload)
    {
        int count = Count(reader, payload - stream.Position - 4, 2);
        if (count > 32768) throw new InvalidDataException("检查点字符串过长。");
        return count;
    }
    private static string ReadText(BinaryReader reader, Stream stream, long payload, char[] buffer, string? existing = null)
    {
        int count = TextLength(reader, stream, payload);
        var text = buffer.AsSpan(0, count);
        stream.ReadExactly(MemoryMarshal.AsBytes(text));
        return existing is not null && text.SequenceEqual(existing.AsSpan()) ? existing : new(text);
    }
    private static int Count(BinaryReader reader, long available, int unit)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > available / unit) throw new InvalidDataException("检查点长度字段越界。");
        return count;
    }
    private static void WriteArray(Stream stream, ulong[] array, CancellationToken token)
    {
        for (int start = 0; start < array.Length; start += 65536)
        { token.ThrowIfCancellationRequested(); stream.Write(MemoryMarshal.AsBytes(array.AsSpan(start, Math.Min(65536, array.Length - start)))); }
    }
    private static ulong[] ReadArray(Stream stream, int count, long payload, CancellationToken token)
    {
        if (count > (payload - stream.Position) / 8) throw new InvalidDataException("检查点位图越界。");
        var array = new ulong[count];
        for (int start = 0; start < count; start += 65536)
        { token.ThrowIfCancellationRequested(); stream.ReadExactly(MemoryMarshal.AsBytes(array.AsSpan(start, Math.Min(65536, count - start)))); }
        return array;
    }
}
