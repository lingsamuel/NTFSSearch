namespace NtfsSearch.Core;

// 卷行与目录派生数据用同一发布身份。旧查询保留自身卷视图，不能读到新视图的行号或目录合计。
internal sealed class PublishedSearchVolume(string id, SearchIndex[] layers)
{
    internal string Id { get; } = id;
    internal IReadOnlyList<SearchIndex> Layers { get; } = layers;
    internal bool SupportsDirectorySizes { get; } = layers.Any(index => index.Count != 0) && layers.All(index => index.HasIdentity);
    internal volatile DirectorySizeOrder[]? DirectorySizes;
    // 合计正在切换到新增量视图时，保留可复用的缓存键，查询只读取已发布的合计。
    internal object[] DirectorySortOwners = [];
}

internal readonly record struct DirectorySearchRow(int PageIndex, PublishedSearchVolume Volume, int Layer, int Row,
    DirectorySizeOrder[]? SortSizes);
