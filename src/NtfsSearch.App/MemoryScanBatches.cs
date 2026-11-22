using System.IO;
using NtfsSearch.Core;

namespace NtfsSearch.App;

// 活动帧最多保存256 KiB数据外加最后一条记录。路径表随帧清空，重复父路径只需传一次。
internal sealed class MemoryScanWriter(Stream stream, CancellationToken token, IndexMetadata metadata = IndexMetadata.All)
{
    private const int BlockBytes = 256 << 10;
    private readonly CollectorFrameWriter frames = new(stream);
    private readonly MemoryStream rows = new(BlockBytes);
    private readonly Dictionary<(string Path, ulong Reference), int> directories = [];
    private BinaryWriter? rowWriter;

    internal void Start(int capacity)
        => frames.WriteAsync(CollectorFrameKind.ScanStart, writer => writer.Write(capacity), token).GetAwaiter().GetResult();

    internal void Add(string parent, ReadOnlySpan<char> name, long? size, long? modified,
        bool directory, ulong file, ulong parentReference, long? created = null)
    {
        if (rows.Length >= BlockBytes) Finish();
        rowWriter ??= new(rows);
        var key = (parent, parentReference);
        if (directories.TryGetValue(key, out int id)) rowWriter.Write(id);
        else
        {
            id = directories.Count;
            directories.Add(key, id);
            rowWriter.Write(~id);
            CollectorWire.WriteName(rowWriter, parent);
            rowWriter.Write(parentReference);
        }
        rowWriter.Write(file);
        if (!metadata.HasFlag(IndexMetadata.FileSize)) size = null;
        if (!metadata.HasFlag(IndexMetadata.ModifiedTime)) modified = null;
        if (!metadata.HasFlag(IndexMetadata.CreationTime)) created = null;
        byte flags = (byte)((directory ? 1 : 0) | (size.HasValue ? 2 : 0) | (modified.HasValue ? 4 : 0) | (created.HasValue ? 8 : 0));
        rowWriter.Write(flags);
        if (size is { } bytes) rowWriter.Write(bytes);
        if (modified is { } ticks) rowWriter.Write(ticks);
        if (created is { } creationTicks) rowWriter.Write(creationTicks);
        CollectorWire.WriteName(rowWriter, name);
    }

    internal void Progress(string message)
        => frames.WriteAsync(CollectorFrameKind.Progress, writer => writer.Write(message), token).GetAwaiter().GetResult();

    internal void Finish()
    {
        if (rows.Length == 0) return;
        frames.WriteAsync(CollectorFrameKind.ScanRows, rows.GetBuffer().AsMemory(0, (int)rows.Length), token).GetAwaiter().GetResult();
        rows.SetLength(0);
        directories.Clear();
    }
}

internal sealed class MemoryScanReader(int capacity, IndexMetadata metadata = IndexMetadata.All, bool precomputeSort = true)
{
    private readonly IndexBuilder builder = new(capacity, fixedSize: false, metadata, precomputeSort);
    private Dictionary<(string Path, ulong Reference), int> directories = [];

    internal SearchIndex Build(CancellationToken token, IProgress<string>? progress = null)
    {
        directories = [];
        return builder.Build(token, progress);
    }

    internal void ReadRows(CollectorFrame frame, CancellationToken token)
    {
        using var reader = frame.Open(CollectorFrameKind.ScanRows);
        var parents = new List<int>();
        while (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            token.ThrowIfCancellationRequested();
            int directoryId = reader.ReadInt32();
            if (directoryId < 0)
            {
                directoryId = ~directoryId;
                if (directoryId != parents.Count) throw new InvalidDataException("采集消息中的父目录序号不连续。");
                string path = new(CollectorWire.ReadName(reader, frame.Payload));
                ulong reference = reader.ReadUInt64();
                var key = (path, reference);
                // 扫描期间同一路径可能被删除后重新创建，保留两个身份供USN回放修正。
                if (!directories.TryGetValue(key, out int index))
                {
                    index = builder.AddDirectory(path, reference);
                    directories.Add(key, index);
                }
                parents.Add(index);
            }
            if (directoryId >= parents.Count) throw new InvalidDataException("采集消息缺少父目录。");
            ulong file = reader.ReadUInt64();
            byte flags = reader.ReadByte();
            long? size = (flags & 2) != 0 ? reader.ReadInt64() : null;
            long? modified = (flags & 4) != 0 ? reader.ReadInt64() : null;
            long? created = (flags & 8) != 0 ? reader.ReadInt64() : null;
            builder.AddRecord(parents[directoryId], CollectorWire.ReadName(reader, frame.Payload), size, modified, (flags & 1) != 0, file, created);
        }
    }
}
