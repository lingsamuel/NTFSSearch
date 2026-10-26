namespace NtfsSearch.Core;

internal sealed class NameArena(int initialCapacity = NameArena.ChunkChars)
{
    internal const int ChunkChars = 1 << 20;
    internal readonly List<char[]> Chunks = [];
    internal int LastUsed;

    public long CapacityBytes => Chunks.Sum(c => (long)c.Length * sizeof(char));

    public long Add(ReadOnlySpan<char> name)
    {
        if (name.Length > ushort.MaxValue) throw new ArgumentException("名称过长。", nameof(name));
        if (Chunks.Count == 0)
        {
            Chunks.Add(new char[Math.Clamp(Math.Max(initialCapacity, name.Length), 1, ChunkChars)]);
            LastUsed = 0;
        }
        else if (ChunkChars - LastUsed < name.Length)
        {
            // 快照和名称偏移要求非末块仍为完整逻辑块；只有首块可以按需扩容。
            if (Chunks[^1].Length != ChunkChars) ResizeLast(ChunkChars);
            Chunks.Add(new char[ChunkChars]);
            LastUsed = 0;
        }
        int required = LastUsed + name.Length;
        if (Chunks[^1].Length < required)
            ResizeLast(Math.Min(ChunkChars, Math.Max(required, Chunks[^1].Length * 2)));
        long offset = (long)(Chunks.Count - 1) * ChunkChars + LastUsed;
        name.CopyTo(Chunks[^1].AsSpan(LastUsed));
        LastUsed += name.Length;
        return offset;
    }

    public ReadOnlySpan<char> Get(long offset, int length) =>
        Chunks[(int)(offset / ChunkChars)].AsSpan((int)(offset % ChunkChars), length);

    private void ResizeLast(int length)
    {
        var last = Chunks[^1];
        Array.Resize(ref last, length);
        Chunks[^1] = last;
    }

    public void Seal()
    {
        if (Chunks.Count == 0 || Chunks[^1].Length == LastUsed) return;
        ResizeLast(LastUsed);
    }
}
