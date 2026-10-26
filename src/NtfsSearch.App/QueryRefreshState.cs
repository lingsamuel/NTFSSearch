namespace NtfsSearch.App;

internal readonly record struct QueryRequest(long Generation, bool UserInitiated);

internal sealed class QueryRefreshState
{
    private long generation;
    private bool running, pendingUser, pendingRefresh, refreshReady, closed;
    public TimeSpan? UserElapsed { get; private set; }

    // 状态只由UI线程访问；连续通知仅占一个待刷新标记，不取消已绑定的索引快照。
    public void RequestUser()
    {
        if (closed) return;
        generation++;
        pendingUser = true;
        pendingRefresh = refreshReady = false;
    }

    public void RequestRefresh() { if (!closed) pendingRefresh = true; }
    public void RefreshWindowElapsed() { if (!closed && pendingRefresh) refreshReady = true; }

    public bool TryStart(out QueryRequest request)
    {
        request = default;
        if (closed || running || !pendingUser && !(pendingRefresh && refreshReady)) return false;
        request = new(generation, pendingUser);
        running = true;
        pendingUser = pendingRefresh = refreshReady = false;
        return true;
    }

    public bool Complete(QueryRequest request, TimeSpan? elapsed)
    {
        running = false;
        if (closed || request.Generation != generation) return false;
        if (request.UserInitiated && elapsed.HasValue) UserElapsed = elapsed;
        return true;
    }

    public void Cancel()
    {
        generation++;
        pendingUser = pendingRefresh = refreshReady = false;
    }

    public void Close() { Cancel(); closed = true; }
}
