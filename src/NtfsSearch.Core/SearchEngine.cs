using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    public SearchResult Search(string text, SortField sort = SortField.Name, bool descending = false,
        bool matchPath = true, int offset = 0, int limit = 100, CancellationToken cancellationToken = default)
    {
        ValidateRequest(text, sort, offset, limit, cancellationToken);
        var timer = Stopwatch.StartNew();
        using var query = Prepare(text, matchPath, cancellationToken);
        var rows = new List<SearchRow>(Math.Min(limit, Math.Min(Count, 1024)));
        if (query.TotalCount == Count && !HasOrderCorrections)
        {
            int end = (int)Math.Min(Count, (long)offset + limit);
            for (int i = offset; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(GetRow(OrderedId(sort, descending, i)));
            }
            return new(Count, rows, timer.Elapsed);
        }
        if (limit > 0 && offset < query.TotalCount)
        {
            long skipped = 0;
            foreach (int id in query.Ids(sort, descending, cancellationToken))
            {
                if (skipped++ < offset) continue;
                rows.Add(GetRow(id));
                if (rows.Count == limit) break;
            }
        }
        return new(query.TotalCount, rows, timer.Elapsed);
    }

    internal PreparedSearch Prepare(string text, bool matchPath, CancellationToken cancellationToken)
    {
        ValidateRequest(text, SortField.Name, 0, 0, cancellationToken);
        var terms = SearchQuery.Parse(text);
        if (terms.Length > 64) throw new ArgumentException("查询最多 64 个条件。", nameof(text));
        if (terms.Length == 0) return new(this, null, VisibleCount);
        var states = new byte[terms.Length][];
        var suffixes = new string[terms.Length];
        for (int t = 0; t < terms.Length; t++)
        {
            states[t] = new byte[Directories.Length];
            int slash = terms[t].LastIndexOf('\\');
            suffixes[t] = slash >= 0 ? terms[t][(slash + 1)..] : "";
            for (int d = 0; d < Directories.Length; d++)
            {
                if ((d & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if ((matchPath || slash >= 0) && Directories[d].Contains(terms[t], StringComparison.OrdinalIgnoreCase))
                    states[t][d] = 1;
                else if (slash >= 0)
                    states[t][d] = Directories[d].EndsWith(terms[t][..(slash + 1)], StringComparison.OrdinalIgnoreCase)
                        ? (byte)2 : (byte)3;
            }
        }

        var asciiTerms = terms.Select(AsciiQuery.Encode).ToArray();
        var asciiSuffixes = suffixes.Select(AsciiQuery.Encode).ToArray();
        int words = (int)(((long)Count + 63) / 64);
        var bits = ArrayPool<ulong>.Shared.Rent(Math.Max(words, 1));
        long total = 0;
        byte[]? memo = null;
        try
        {
            bool prefixPredicate = terms[0].Contains('\\');
            if (!prefixPredicate || suffixes[0].Length != 0)
                memo = Names.TryRentWorkspace(prefixPredicate ? suffixes[0] : terms[0], prefixPredicate);
            void Scan(int from, int to)
            {
                Span<char> scratch = stackalloc char[255];
                long local = 0;
                for (int w = from; w < to; w++)
                {
                    if ((w & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int first = w * 64, end = (int)Math.Min(first + 64L, Count);
                    ulong mask = 0;
                    for (int id = first; id < end; id++)
                    {
                        if (!IsVisible(id)) continue;
                        ref readonly var record = ref Records[id];
                        bool matches = true;
                        for (int t = 0; t < terms.Length; t++)
                        {
                            byte state = states[t][record.Directory];
                            if (state == 1) continue;
                            if (state == 3) { matches = false; break; }
                            byte cached = t == 0 && memo is not null ? memo[record.NameId] : (byte)0;
                            if (cached != 0) { if (cached == 1) { matches = false; break; } continue; }
                            bool found = state == 2
                                ? Names.Matches(record.NameId, suffixes[t], asciiSuffixes[t], true, scratch)
                                : Names.Matches(record.NameId, terms[t], asciiTerms[t], false, scratch);
                            if (t == 0 && memo is not null) memo[record.NameId] = found ? (byte)2 : (byte)1;
                            if (!found) { matches = false; break; }
                        }
                        if (matches) mask |= 1UL << (id - first);
                    }
                    bits[w] = mask;
                    local += BitOperations.PopCount(mask);
                }
                Interlocked.Add(ref total, local);
            }
            if (Count < 100_000) Scan(0, words);
            else Parallel.ForEach(Partitioner.Create(0, words, 512),
                new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = Math.Min(Environment.ProcessorCount, 16) },
                range => Scan(range.Item1, range.Item2));

            cancellationToken.ThrowIfCancellationRequested();
            return new(this, bits, total);
        }
        catch { ArrayPool<ulong>.Shared.Return(bits); throw; }
        finally { if (memo is not null) Names.ReturnWorkspace(); }
    }

    internal IEnumerable<int> EnumerateMatches(ulong[]? bits, long total, SortField sort, bool descending, CancellationToken token)
    {
        if (bits is not null && total <= 16_384)
        {
            var ids = new int[(int)total];
            int next = 0, words = (int)(((long)Count + 63) / 64);
            for (int w = 0; w < words; w++)
            {
                if ((w & 4095) == 0) token.ThrowIfCancellationRequested();
                ulong mask = bits[w];
                while (mask != 0)
                {
                    ids[next++] = w * 64 + BitOperations.TrailingZeroCount(mask);
                    mask &= mask - 1;
                }
            }
            Array.Sort(ids, (a, b) => CompareAcross(this, a, this, b, sort, descending));
            foreach (int id in ids) { token.ThrowIfCancellationRequested(); yield return id; }
            yield break;
        }
        foreach (int id in EnumerateOrdered(sort, descending, token))
            if (IsVisible(id) && (bits is null || (bits[id / 64] & (1UL << (id % 64))) != 0)) yield return id;
    }

    internal static void ValidateRequest(string text, SortField sort, int offset, int limit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (text.Length > 4096) throw new ArgumentException("查询最多 4096 个字符。", nameof(text));
        token.ThrowIfCancellationRequested();
    }
}
