namespace NtfsSearch.Core;

// 卷行与目录派生数据用同一发布身份。旧查询保留自身卷视图，不能读到新视图的行号或目录合计。
internal sealed class PublishedSearchVolume(string id, SearchIndex[] layers)
{
    internal string Id { get; } = id;
    internal IReadOnlyList<SearchIndex> Layers { get; } = layers;
    internal bool SupportsDirectorySizes { get; } = layers.Any(index => index.Count != 0) && layers.All(index => index.HasIdentity);
    internal volatile DirectorySizeOrder[]? DirectorySizes;
}

internal readonly record struct DirectorySearchRow(int PageIndex, PublishedSearchVolume Volume, int Layer, int Row,
    DirectorySizeOrder[]? SortSizes);
