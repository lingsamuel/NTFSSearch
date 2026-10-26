using System.IO.MemoryMappedFiles;

namespace NtfsSearch.Core;

// 仅在旧快照转换期间借用原UTF16页，所有Span均在Dispose前立即复制进新名称池。
internal sealed unsafe class MappedSnapshotNames : IDisposable
{
    private readonly MemoryMappedFile mapping;
    private readonly MemoryMappedViewAccessor view;
    private readonly IReadOnlyList<(long Offset, int Length)> chunks;
    private byte* pointer;

    internal MappedSnapshotNames(FileStream stream, IReadOnlyList<(long Offset, int Length)> chunks)
    {
        this.chunks = chunks;
        mapping = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        MemoryMappedViewAccessor? created = null;
        try
        {
            view = created = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
        }
        catch { created?.Dispose(); mapping.Dispose(); throw; }
    }

    internal ReadOnlySpan<char> Get(long offset, int length)
    {
        long chunk = offset / NameArena.ChunkChars;
        long within = offset % NameArena.ChunkChars;
        if (offset < 0 || length == 0 || chunk >= chunks.Count || within + length > chunks[(int)chunk].Length)
            throw new InvalidDataException("快照名称引用越界。");
        return new ReadOnlySpan<char>(pointer + view.PointerOffset + chunks[(int)chunk].Offset + within * sizeof(char), length);
    }

    public void Dispose()
    {
        view.SafeMemoryMappedViewHandle.ReleasePointer();
        pointer = null;
        view.Dispose(); mapping.Dispose();
    }
}
