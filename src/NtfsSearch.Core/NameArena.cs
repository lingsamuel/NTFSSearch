namespace NtfsSearch.Core;

internal sealed class NameArena
{
    internal const int ChunkChars = 1 << 20;
    internal readonly List<char[]> Chunks = [];
    internal int LastUsed;

    public long CapacityBytes => Chunks.Sum(c => (long)c.Length * sizeof(char));

    public long Add(ReadOnlySpan<char> name)
    {
        if (name.Length > ushort.MaxValue) throw new ArgumentException("名称过长。", nameof(name));
        if (Chunks.Count == 0 || ChunkChars - LastUsed < name.Length)
        {
            Chunks.Add(new char[ChunkChars]);
            LastUsed = 0;
        }
        long offset = (long)(Chunks.Count - 1) * ChunkChars + LastUsed;
        name.CopyTo(Chunks[^1].AsSpan(LastUsed));
        LastUsed += name.Length;
        return offset;
    }

    public ReadOnlySpan<char> Get(long offset, int length) =>
        Chunks[(int)(offset / ChunkChars)].AsSpan((int)(offset % ChunkChars), length);

    public void Seal()
    {
        if (Chunks.Count == 0 || Chunks[^1].Length == LastUsed) return;
        var last = Chunks[^1];
        Array.Resize(ref last, LastUsed);
        Chunks[^1] = last;
    }
}
