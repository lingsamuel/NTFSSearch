using System.Text;
using System.Runtime.CompilerServices;

namespace NtfsSearch.Core;

internal sealed class NameArena(int initialCapacity = NameArena.ChunkChars)
{
    internal const int ChunkChars = 1 << 20;
    internal readonly List<char[]> Chunks = [];
    internal int LastUsed;
    internal ulong[] Descriptors = [];
    internal int Count;
    private const ulong AsciiFlag = 1UL << 63;
    // ASCII低16位为cut/length；UTF16保留ushort长度。完整字符块至少容纳16个名称，47位偏移覆盖31位名称数。
    private const ulong SuffixUpperFlag = 1UL << 62;
    private const ulong AsciiOffsetMask = (1UL << 46) - 1;
    internal readonly List<byte[]> AsciiChunks = [];
    internal int AsciiLastUsed;
    private byte[]? workspace;
    private int workspaceBusy;
    private string? lastPattern;
    private bool lastPrefix;
    internal byte[]? TryRentWorkspace(string pattern, bool prefix)
    {
        // 只保留最后一个不可变名称谓词。取消留下的已完成项仍有效；并发租用失败直接走原匹配。
        if (Interlocked.CompareExchange(ref workspaceBusy, 1, 0) != 0) return null;
        try
        {
            workspace ??= IndexArrays.Persistent<byte>(Count);
            if (lastPrefix != prefix || !string.Equals(lastPattern, pattern, StringComparison.Ordinal))
            {
                Array.Clear(workspace);
                lastPattern = pattern; lastPrefix = prefix;
            }
            return workspace;
        }
        catch { Volatile.Write(ref workspaceBusy, 0); throw; }
    }
    internal void ReturnWorkspace() => Volatile.Write(ref workspaceBusy, 0);

    private HashSet<int>? unique;

    public long CapacityBytes => Chunks.Sum(c => (long)c.Length * sizeof(char)) + (long)Descriptors.Length * sizeof(ulong) + AsciiChunks.Sum(c => (long)c.Length) + (workspace?.LongLength ?? 0);

    public int Add(ReadOnlySpan<char> name, bool isDirectory = false)
    {
        if (name.IsEmpty || name.Length > ushort.MaxValue) throw new ArgumentException("名称长度无效。", nameof(name));
        unique ??= new HashSet<int>(new NameComparer(this));
        if (unique.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out int existing))
        {
            // 文件先出现、同名目录后出现时提升为UTF16；ID与Ordinal散列内容不变。
            if (isDirectory && IsAscii(existing)) Descriptors[existing] = PackUtf16(name);
            return existing;
        }
        if (Count == int.MaxValue) throw new InvalidOperationException("唯一名称数超出索引范围。");
        if (Count == Descriptors.Length) Array.Resize(ref Descriptors, Math.Max(16, checked(Count + Math.Max(Count / 2, 1))));
        int id = Count++;
        Descriptors[id] = !isDirectory && AsciiCaseShape.TryEncode(name, out byte cut, out bool suffixUpper)
            ? AppendAscii(name, cut, suffixUpper) : PackUtf16(name);
        unique.Add(id); return id;
    }

    private ulong PackUtf16(ReadOnlySpan<char> name)
    {
        long offset = Append(name);
        if ((ulong)offset >= (1UL << 47)) throw new InvalidOperationException("UTF16名称区超出47位字符偏移范围。");
        return ((ulong)offset << 16) | (ushort)name.Length;
    }

    private ulong AppendAscii(ReadOnlySpan<char> name, byte cut, bool suffixUpper)
    {
        if (AsciiChunks.Count == 0 || ChunkChars - AsciiLastUsed < name.Length)
        {
            if (AsciiChunks.Count > 0 && AsciiChunks[^1].Length != ChunkChars) ResizeAsciiLast(ChunkChars);
            AsciiChunks.Add(IndexArrays.Persistent<byte>(AsciiChunks.Count == 0
                ? Math.Clamp(Math.Max(initialCapacity, name.Length), 1, ChunkChars) : ChunkChars));
            AsciiLastUsed = 0;
        }
        int required = AsciiLastUsed + name.Length;
        if (AsciiChunks[^1].Length < required) ResizeAsciiLast(Math.Min(ChunkChars, Math.Max(required, AsciiChunks[^1].Length * 2)));
        long offset = (long)(AsciiChunks.Count - 1) * ChunkChars + AsciiLastUsed;
        if ((ulong)offset > AsciiOffsetMask) throw new InvalidOperationException("ASCII名称区超出46位字节偏移范围。");
        Ascii.ToLower(name, AsciiChunks[^1].AsSpan(AsciiLastUsed, name.Length), out int written);
        if (written != name.Length) throw new InvalidOperationException("ASCII名称编码失败。");
        AsciiLastUsed += written;
        return AsciiFlag | (suffixUpper ? SuffixUpperFlag : 0) | ((ulong)offset << 16) | ((ulong)cut << 8) | (byte)name.Length;
    }
    internal bool IsAscii(int id) => (Descriptors[id] & AsciiFlag) != 0;

    private long Append(ReadOnlySpan<char> name)
    {
        if (name.Length > ushort.MaxValue) throw new ArgumentException("名称过长。", nameof(name));
        if (Chunks.Count == 0)
        {
            Chunks.Add(IndexArrays.Persistent<char>(Math.Clamp(Math.Max(initialCapacity, name.Length), 1, ChunkChars)));
            LastUsed = 0;
        }
        else if (ChunkChars - LastUsed < name.Length)
        {
            // 快照和名称偏移要求非末块仍为完整逻辑块；只有首块可以按需扩容。
            if (Chunks[^1].Length != ChunkChars) ResizeLast(ChunkChars);
            Chunks.Add(IndexArrays.Persistent<char>(ChunkChars));
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

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool Matches(int id, string term, byte[]? lowerTerm, bool prefix, Span<char> scratch)
    {
        if (term.Length == 0) return true;
        ulong descriptor = Descriptors[id];
        bool ascii = (descriptor & AsciiFlag) != 0;
        int length = ascii ? (byte)descriptor : (ushort)descriptor;
        if (length < term.Length) return false;
        if (!ascii)
        {
            var text = Get((long)(descriptor >> 16), length);
            return prefix ? text.StartsWith(term, StringComparison.OrdinalIgnoreCase) : text.Contains(term, StringComparison.OrdinalIgnoreCase);
        }
        long offset = (long)((descriptor >> 16) & AsciiOffsetMask);
        var bytes = AsciiChunks[(int)(offset / ChunkChars)].AsSpan((int)(offset % ChunkChars), length);
        if (lowerTerm is not null) return prefix ? bytes.StartsWith(lowerTerm) : bytes.IndexOf(lowerTerm) >= 0;
        Ascii.ToUtf16(bytes, scratch, out int written);
        if (written != length) throw new InvalidOperationException();
        var restored = scratch[..length];
        AsciiCaseShape.Restore(restored, (byte)(descriptor >> 8), (descriptor & SuffixUpperFlag) != 0);
        return prefix ? restored.StartsWith(term, StringComparison.OrdinalIgnoreCase) : restored.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    internal ReadOnlySpan<byte> AsciiName(int id)
    {
        ulong descriptor = Descriptors[id];
        if ((descriptor & AsciiFlag) == 0) return default;
        long offset = (long)((descriptor >> 16) & AsciiOffsetMask);
        return AsciiChunks[(int)(offset / ChunkChars)].AsSpan((int)(offset % ChunkChars), (byte)descriptor);
    }

    internal int Length(int id) => (Descriptors[id] & AsciiFlag) != 0 ? (byte)Descriptors[id] : (ushort)Descriptors[id];

    internal ReadOnlySpan<char> Get(int id, Span<char> scratch)
    {
        ulong descriptor = Descriptors[id];
        if ((descriptor & AsciiFlag) == 0) return Get(id);
        long offset = (long)((descriptor >> 16) & AsciiOffsetMask); int length = (byte)descriptor;
        Ascii.ToUtf16(AsciiChunks[(int)(offset / ChunkChars)].AsSpan((int)(offset % ChunkChars), length), scratch, out int written);
        if (written != length) throw new InvalidOperationException();
        var result = scratch[..length];
        AsciiCaseShape.Restore(result, (byte)(descriptor >> 8), (descriptor & SuffixUpperFlag) != 0);
        return result;
    }

    public ReadOnlySpan<char> Get(int id)
    {
        ulong descriptor = Descriptors[id];
        return Get((long)(descriptor >> 16), (ushort)descriptor);
    }

    public ReadOnlyMemory<char> GetMemory(int id)
    {
        ulong descriptor = Descriptors[id];
        if ((descriptor & AsciiFlag) != 0) throw new InvalidOperationException("ASCII名称需要调用方提供解码缓冲区。");
        long offset = (long)(descriptor >> 16);
        return Chunks[(int)(offset / ChunkChars)].AsMemory((int)(offset % ChunkChars), (ushort)descriptor);
    }

    private void ResizeLast(int length)
    {
        var last = IndexArrays.Persistent<char>(length);
        Chunks[^1].AsSpan(0, Math.Min(Chunks[^1].Length, length)).CopyTo(last);
        Chunks[^1] = last;
    }

    private void ResizeAsciiLast(int length)
    {
        var last = IndexArrays.Persistent<byte>(length);
        AsciiChunks[^1].AsSpan(0, Math.Min(AsciiChunks[^1].Length, length)).CopyTo(last);
        AsciiChunks[^1] = last;
    }

    internal void ValidateReferences(CancellationToken token)
    {
        for (int id = 0; id < Count; id++)
        {
            if ((id & 4095) == 0) token.ThrowIfCancellationRequested();
            ulong descriptor = Descriptors[id]; bool ascii = (descriptor & AsciiFlag) != 0;
            int length = ascii ? (byte)descriptor : (ushort)descriptor;
            long offset = ascii ? (long)((descriptor >> 16) & AsciiOffsetMask) : (long)(descriptor >> 16);
            long chunk = offset / ChunkChars, within = offset % ChunkChars;
            if (length == 0 || (ascii ? chunk >= AsciiChunks.Count || within + length > AsciiChunks[(int)chunk].Length
                : chunk >= Chunks.Count || within + length > Chunks[(int)chunk].Length)) throw new InvalidDataException("快照名称引用越界。");
            if (ascii)
            {
                var bytes = AsciiName(id);
                if ((byte)(descriptor >> 8) > length || bytes.ContainsAnyInRange((byte)128, byte.MaxValue)
                    || bytes.ContainsAnyInRange((byte)'A', (byte)'Z')) throw new InvalidDataException("快照ASCII名称编码无效。");
            }
        }
    }

    public void Seal()
    {
        if (Chunks.Count > 0 && Chunks[^1].Length != LastUsed) ResizeLast(LastUsed);
        if (AsciiChunks.Count > 0 && AsciiChunks[^1].Length != AsciiLastUsed) ResizeAsciiLast(AsciiLastUsed);
        Descriptors = IndexArrays.Freeze(Descriptors, Count);
        // 散列表只在构建时需要，常驻索引只保留UTF16池和描述符。
        unique = null;
    }

    private sealed class NameComparer(NameArena arena) : IEqualityComparer<int>, IAlternateEqualityComparer<ReadOnlySpan<char>, int>
    {
        public bool Equals(int x, int y)
        {
            Span<char> a = stackalloc char[255], b = stackalloc char[255];
            return arena.Get(x, a).SequenceEqual(arena.Get(y, b));
        }
        public int GetHashCode(int value) { Span<char> scratch = stackalloc char[255]; return GetHashCode(arena.Get(value, scratch)); }
        public bool Equals(ReadOnlySpan<char> value, int other) { Span<char> scratch = stackalloc char[255]; return value.SequenceEqual(arena.Get(other, scratch)); }
        public int GetHashCode(ReadOnlySpan<char> value) => string.GetHashCode(value, StringComparison.Ordinal);
        public int Create(ReadOnlySpan<char> value) => throw new NotSupportedException();
    }
}
