namespace NtfsSearch.Core;

// 仅后台工作任务持有可变汇总。交给查询的排序与合计视图是只读的。
internal sealed class DirectorySizeAggregation(string root, Action<CancellationToken>? yieldToSearch = null)
{
    private SpaceUsageIndex? usage;
    private DirectorySizeOrder[] orders = [];

    internal DirectorySizeOrder[] Update(IReadOnlyList<SearchIndex> layers, CancellationToken token)
    {
        var updated = usage?.Update(layers, token)
            ?? SpaceUsageIndex.Create(root, layers, token, includeFileRows: false, yieldToSearch);
        var prior = updated == usage ? orders.ToDictionary(order => order.Index.Records) : null;
        var next = new DirectorySizeOrder[layers.Count];
        for (int i = 0; i < next.Length; i++)
        {
            var index = layers[i];
            next[i] = prior is not null && prior.TryGetValue(index.Records, out var previous)
                ? previous.Update(index, updated, token)
                : DirectorySizeOrder.Create(index, updated, token);
        }
        usage = updated;
        orders = next;
        return next;
    }
}
