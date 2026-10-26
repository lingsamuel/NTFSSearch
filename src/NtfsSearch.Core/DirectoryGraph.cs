using System.Collections;
using System.Collections.Immutable;

namespace NtfsSearch.Core;

// 基线身份直接借索引；持久集合只保留缺失父锚与发生真实变化的目录。
internal sealed class DirectoryGraph : IEnumerable<KeyValuePair<ulong, LiveVolumeIndex.DirectoryNode>>
{
    private readonly SearchIndex baseline;
    internal ImmutableDictionary<ulong, LiveVolumeIndex.DirectoryNode> Overrides { get; }
    internal int Count => Overrides.Count;

    private DirectoryGraph(SearchIndex baseline, ImmutableDictionary<ulong, LiveVolumeIndex.DirectoryNode> overrides)
    { this.baseline = baseline; Overrides = overrides; }

    internal static DirectoryGraph Create(SearchIndex baseline, CancellationToken token)
    {
        var graph = new DirectoryGraph(baseline, ImmutableDictionary<ulong, LiveVolumeIndex.DirectoryNode>.Empty);
        var anchors = graph.Overrides.ToBuilder();
        for (int d = 0; d < baseline.Directories.Length; d++)
        {
            if ((d & 65535) == 0) token.ThrowIfCancellationRequested();
            ulong reference = baseline.DirectoryReferences![d];
            if (!anchors.ContainsKey(reference) && !graph.TryGetBaseline(reference, out _, out _))
                anchors.Add(reference, new(0, ReadOnlyMemory<char>.Empty, baseline.Directories[d]));
        }
        return new(baseline, anchors.ToImmutable());
    }

    internal bool TryGetBaseline(ulong reference, out LiveVolumeIndex.DirectoryNode node, out int row)
    {
        var (start, count) = baseline.FindFileRange(reference);
        // 旧图顺序写入同身份时由最后一条可见目录记录获胜，不能借隐藏后的层视图。
        for (int id = start + count - 1; id >= start; id--)
            if (baseline.Records[id].IsDirectory && baseline.IsVisible(id))
            {
                node = new(baseline.DirectoryReferences![baseline.Records[id].Directory], baseline.NameMemory(id));
                row = id; return true;
            }
        node = default; row = -1; return false;
    }

    internal bool TryGetValue(ulong reference, out LiveVolumeIndex.DirectoryNode node)
        => Overrides.TryGetValue(reference, out node) || TryGetBaseline(reference, out node, out _);
    internal bool ContainsKey(ulong reference) => TryGetValue(reference, out _);
    internal LiveVolumeIndex.DirectoryNode this[ulong reference] => TryGetValue(reference, out var node)
        ? node : throw new KeyNotFoundException();

    internal DirectoryGraph SetItem(ulong reference, LiveVolumeIndex.DirectoryNode node)
    {
        if (TryGetValue(reference, out var previous) && previous.SameValue(node)) return this;
        var next = TryGetBaseline(reference, out var original, out _) && original.SameValue(node)
            ? Overrides.Remove(reference) : Overrides.SetItem(reference, node);
        return ReferenceEquals(next, Overrides) ? this : new(baseline, next);
    }

    public IEnumerator<KeyValuePair<ulong, LiveVolumeIndex.DirectoryNode>> GetEnumerator() => Overrides.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
