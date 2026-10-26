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
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (text.Length > 4096) throw new ArgumentException("查询最多 4096 个字符。", nameof(text));
        cancellationToken.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var terms = SearchQuery.Parse(text);
        if (terms.Length > 64) throw new ArgumentException("查询最多 64 个条件。", nameof(text));
        var rows = new List<SearchRow>(Math.Min(limit, Math.Min(Count, 1024)));
        if (terms.Length == 0)
        {
            int end = (int)Math.Min(Count, (long)offset + limit);
            for (int i = offset; i < end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(GetRow(OrderedId(sort, descending, i)));
            }
            return new(Count, rows, timer.Elapsed);
        }

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

        int words = (int)(((long)Count + 63) / 64);
        var bits = ArrayPool<ulong>.Shared.Rent(Math.Max(words, 1));
        long total = 0;
        try
        {
            void Scan(int from, int to)
            {
                long local = 0;
                for (int w = from; w < to; w++)
                {
                    if ((w & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
                    int first = w * 64, end = (int)Math.Min(first + 64L, Count);
                    ulong mask = 0;
                    for (int id = first; id < end; id++)
                    {
                        ref readonly var record = ref Records[id];
                        bool matches = true;
                        for (int t = 0; t < terms.Length; t++)
                        {
                            byte state = states[t][record.Directory];
                            if (state == 1) continue;
                            if (state == 3 || (state == 2
                                ? !Name(id).StartsWith(suffixes[t], StringComparison.OrdinalIgnoreCase)
                                : !Name(id).Contains(terms[t], StringComparison.OrdinalIgnoreCase)))
                            { matches = false; break; }
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

            if (total > 0 && limit > 0 && offset < total)
            {
                if (total <= 16_384)
                {
                    var ids = new int[(int)total];
                    int next = 0;
                    for (int w = 0; w < words; w++)
                    {
                        if ((w & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        ulong mask = bits[w];
                        while (mask != 0)
                        {
                            ids[next++] = w * 64 + BitOperations.TrailingZeroCount(mask);
                            mask &= mask - 1;
                        }
                    }
                    Array.Sort(ids, (a, b) => descending && IsKnown(Records[a], sort) && IsKnown(Records[b], sort)
                        ? Compare(b, a, sort) : Compare(a, b, sort));
                    int end = (int)Math.Min(total, (long)offset + limit);
                    for (int i = offset; i < end; i++) rows.Add(GetRow(ids[i]));
                }
                else
                {
                    long skipped = 0;
                    for (int position = 0; position < Count && rows.Count < limit; position++)
                    {
                        if ((position & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                        int id = OrderedId(sort, descending, position);
                        if ((bits[id / 64] & (1UL << (id % 64))) == 0) continue;
                        if (skipped++ >= offset) rows.Add(GetRow(id));
                    }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new(total, rows, timer.Elapsed);
        }
        finally { ArrayPool<ulong>.Shared.Return(bits); }
    }
}
