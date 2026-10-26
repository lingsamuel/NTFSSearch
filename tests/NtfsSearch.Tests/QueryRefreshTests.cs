using NtfsSearch.App;
using Xunit;

namespace NtfsSearch.Tests;

public class QueryRefreshTests
{
    [Fact]
    public void BackgroundBurstWaitsForWindowAndPreservesUserTiming()
    {
        var state = new QueryRefreshState();
        state.RequestUser();
        Assert.True(state.TryStart(out var user));
        Assert.True(state.Complete(user, TimeSpan.FromMilliseconds(25)));
        for (int i = 0; i < 100; i++) state.RequestRefresh();
        Assert.False(state.TryStart(out _));
        state.RefreshWindowElapsed();
        Assert.True(state.TryStart(out var refresh));
        Assert.False(refresh.UserInitiated);
        Assert.True(state.Complete(refresh, TimeSpan.FromMilliseconds(250)));
        Assert.Equal(TimeSpan.FromMilliseconds(25), state.UserElapsed);
        Assert.False(state.TryStart(out _));
    }

    [Fact]
    public void UpdatesDuringSearchDoNotDiscardResultAndCoalesceOneSuccessor()
    {
        var state = new QueryRefreshState();
        state.RequestUser();
        Assert.True(state.TryStart(out var user));
        for (int i = 0; i < 100; i++) state.RequestRefresh();
        state.RefreshWindowElapsed();
        Assert.False(state.TryStart(out _));
        Assert.True(state.Complete(user, TimeSpan.FromMilliseconds(10)));
        Assert.True(state.TryStart(out var refresh));
        Assert.True(state.Complete(refresh, TimeSpan.FromMilliseconds(30)));
        Assert.False(state.TryStart(out _));
    }

    [Fact]
    public void NewUserInputSupersedesOldResultAndConsumesPendingRefresh()
    {
        var state = new QueryRefreshState();
        state.RequestUser();
        Assert.True(state.TryStart(out var old));
        state.RequestRefresh();
        state.RefreshWindowElapsed();
        for (int i = 0; i < 100; i++) state.RequestUser();
        Assert.False(state.TryStart(out _));
        Assert.False(state.Complete(old, TimeSpan.FromMilliseconds(99)));
        Assert.Null(state.UserElapsed);
        Assert.True(state.TryStart(out var latest));
        Assert.True(latest.UserInitiated);
        Assert.True(state.Complete(latest, TimeSpan.FromMilliseconds(3)));
        Assert.Equal(TimeSpan.FromMilliseconds(3), state.UserElapsed);
        Assert.False(state.TryStart(out _));
    }

    [Fact]
    public void ContinuousChangesPublishEveryCompletedSnapshot()
    {
        var state = new QueryRefreshState();
        state.RequestRefresh();
        state.RefreshWindowElapsed();
        for (int i = 0; i < 100; i++)
        {
            Assert.True(state.TryStart(out var refresh));
            state.RequestRefresh();
            state.RefreshWindowElapsed();
            Assert.True(state.Complete(refresh, TimeSpan.FromMilliseconds(i + 1)));
        }
        Assert.Null(state.UserElapsed);
    }

    [Fact]
    public void CancelAndCloseInvalidateRunningResultAndClearPendingWork()
    {
        var state = new QueryRefreshState();
        state.RequestUser();
        Assert.True(state.TryStart(out var running));
        state.RequestRefresh();
        state.RefreshWindowElapsed();
        state.Cancel();
        Assert.False(state.Complete(running, TimeSpan.FromMilliseconds(50)));
        Assert.False(state.TryStart(out _));
        state.RequestUser();
        Assert.True(state.TryStart(out var next));
        Assert.True(state.Complete(next, TimeSpan.FromMilliseconds(4)));
        state.Close();
        state.RequestRefresh();
        state.RefreshWindowElapsed();
        state.RequestUser();
        Assert.False(state.TryStart(out _));
        Assert.Equal(TimeSpan.FromMilliseconds(4), state.UserElapsed);
    }
}
