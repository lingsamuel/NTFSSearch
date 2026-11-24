using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

namespace NtfsSearch.Core;

public sealed partial class SearchIndex
{
    internal const int DirectMatchSortLimit = 16_384;
    private const int MaxQueryTerms = 64;
    private const int MaxQueryCharacters = 4096;

    public SearchResult Search(string text, SortField sort = SortField.Name, bool descending = false,
        bool matchPath = true, int offset = 0, int limit = 100, CancellationToken cancellationToken = default,
        SearchFilter? filter = null)
    {
        ValidateRequest(text, sort, offset, limit, cancellationToken);
        filter?.Validate();
        var timer = Stopwatch.StartNew();
        using var query = Prepare(text, matchPath, cancellationToken, filter);
        var rows = new List<SearchRow>(Math.Min(limit, Math.Min(Count, 1024)));
        if (query.TotalCount == Count && !HasOrderCorrections)
        {
            int end = (int)Math.Min(Count, (long)offset + limit);
            if (offset < end)
            {
                var (order, known) = ResolveSortOrder(sort, cancellationToken);
                for (int i = offset; i < end; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add(GetRow(order[descending && i < known ? known - 1 - i : i]));
                }
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

    internal PreparedSearch Prepare(string text, bool matchPath, CancellationToken cancellationToken, SearchFilter? filter = null)
    {
        ValidateRequest(text, SortField.Name, 0, 0, cancellationToken);
        var terms = SearchQuery.Parse(text);
        if (terms.Length > MaxQueryTerms) throw new SearchQueryLimitException(MaxQueryTerms, countsCharacters: false);
        if (filter?.IsEmpty == true) filter = null;
        if (filter is { IncludeFiles: false, IncludeDirectories: false }) return new(this, null, 0);
        if (terms.Length == 0 && filter is null) return new(this, null, VisibleCount);
        var extensions = filter?.CreateExtensionSet();
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
            if (terms.Length > 0)
            {
                bool prefixPredicate = terms[0].Contains('\\');
                if (!prefixPredicate || suffixes[0].Length != 0)
                    memo = Names.TryRentWorkspace(prefixPredicate ? suffixes[0] : terms[0], prefixPredicate);
            }
            void Scan(int from, int to)
            {
                Span<char> scratch = stackalloc char[255];
                var extensionLookup = extensions?.GetAlternateLookup<ReadOnlySpan<char>>();
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
                        if (filter is not null && !filter.Matches(this, id)) continue;
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
                        if (!matches) continue;
                        // 只对其余条件已经命中的文件读取后缀，直接查询字符片段以避免分配字符串。
                        if (extensionLookup is { } lookup && !record.IsDirectory
                            && !lookup.Contains(FileExtension(id, scratch))) continue;
                        mask |= 1UL << (id - first);
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

    internal IEnumerable<int> EnumerateMatches(ulong[]? bits, long total, SortField sort, bool descending, CancellationToken token,
        DirectorySizeOrder? directorySizes = null, SearchSortSelection? selection = null, Action? beforeColdSort = null)
    {
        if (bits is not null && total <= DirectMatchSortLimit)
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
            Array.Sort(ids, (a, b) => directorySizes is null ? CompareAcross(this, a, this, b, sort, descending)
                : CompareSizes(this, a, EffectiveSize(a, directorySizes), this, b, EffectiveSize(b, directorySizes), descending));
            foreach (int id in ids) { token.ThrowIfCancellationRequested(); yield return id; }
            yield break;
        }
        if (!PrecomputeSort) selection ??= new(sort);
        if (directorySizes is not { HasKnownDirectories: true })
        {
            foreach (int id in EnumerateOrdered(sort, descending, token, selection: selection, beforeColdSort: beforeColdSort))
                if (Matches(id)) yield return id;
            yield break;
        }

        // 已知目录与文件归并完才访问未知段。大量目录命中时，第一页可直接从已知值开始。
        using var regular = EnumerateOrdered(sort, descending, token, knownOnly: true, selection, beforeColdSort).Where(Matches).GetEnumerator();
        using var folders = directorySizes.Ordered(descending, token, selection).Where(Matches).GetEnumerator();
        bool hasRegular = (bits is null || HasMatchingFile()) && regular.MoveNext();
        bool hasFolder = folders.MoveNext();
        while (hasRegular || hasFolder)
        {
            if (hasFolder && (!hasRegular || CompareSizes(this, folders.Current, directorySizes.GetSize(folders.Current),
                this, regular.Current, EffectiveSize(regular.Current, null), descending) < 0))
            {
                yield return folders.Current;
                hasFolder = folders.MoveNext();
            }
            else
            {
                yield return regular.Current;
                hasRegular = regular.MoveNext();
            }
        }
        foreach (int id in EnumerateOrdered(sort, descending, token, knownOnly: false, selection, beforeColdSort))
            if (Matches(id) && (!Records[id].IsDirectory || !directorySizes.GetSize(id).HasValue)) yield return id;

        bool Matches(int id) => IsVisible(id) && (bits is null || (bits[id / 64] & (1UL << (id % 64))) != 0);

        // 目录独占的宽查询从位图确认没有文件，避免顺着文件排列遍历整卷。
        bool HasMatchingFile()
        {
            int words = (int)(((long)Count + 63) / 64);
            for (int word = 0; word < words; word++)
            {
                if ((word & 4095) == 0) token.ThrowIfCancellationRequested();
                ulong mask = bits![word];
                while (mask != 0)
                {
                    if (HasSize(word * 64 + BitOperations.TrailingZeroCount(mask))) return true;
                    mask &= mask - 1;
                }
            }
            return false;
        }
    }

    internal static void ValidateRequest(string text, SortField sort, int offset, int limit, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        if (!Enum.IsDefined(sort)) throw new ArgumentOutOfRangeException(nameof(sort));
        if (text.Length > MaxQueryCharacters) throw new SearchQueryLimitException(MaxQueryCharacters, countsCharacters: true);
        token.ThrowIfCancellationRequested();
    }
}
