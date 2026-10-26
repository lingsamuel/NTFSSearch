using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class VolumeSchedulerTests
{
    private static NtfsVolume Disk(string root, params string[] devices) => new(root, root, 100, "", devices);
    private static VolumeWork Work(NtfsVolume disk, string revision = "one", long memory = 1) => new(disk, revision, memory);

    [Fact]
    public async Task IndependentDisksOverlapAndCompletionAdmitsNextDisk()
    {
        var gate = new Jobs();
        await using var scheduler = new VolumeWorkScheduler(2, 10, gate.Execute);
        scheduler.Reconcile([Work(Disk("C", "0")), Work(Disk("D", "1")), Work(Disk("E", "2"))]);
        scheduler.Pump();
        await gate.Started("C"); await gate.Started("D");
        Assert.Equal(2, scheduler.ActiveCount); Assert.False(gate.HasStarted("E"));
        gate.Finish("D"); await scheduler.WaitForActivityAsync(default); scheduler.Pump();
        await gate.Started("E");
        Assert.Equal(2, scheduler.ActiveCount);
        gate.FinishAll();
    }

    [Fact]
    public async Task SharedPhysicalDiskDoesNotBlockOtherPhysicalDisksInQueue()
    {
        var jobs = new Jobs();
        await using var scheduler = new VolumeWorkScheduler(2, 10, jobs.Execute);
        scheduler.Reconcile([Work(Disk("C", "0")), Work(Disk("D", "0")), Work(Disk("E", "1"))]); scheduler.Pump();
        await jobs.Started("C"); await jobs.Started("E");
        Assert.False(jobs.HasStarted("D"));
        jobs.Finish("C"); await scheduler.WaitForActivityAsync(default); scheduler.Pump(); await jobs.Started("D");
        jobs.FinishAll();
    }

    [Fact]
    public async Task MemoryReservationsAndOversizedSingleJobCannotDeadlockQueue()
    {
        var jobs = new Jobs();
        await using var scheduler = new VolumeWorkScheduler(3, 10, jobs.Execute);
        scheduler.Reconcile([Work(Disk("C", "0"), memory: 7), Work(Disk("D", "1"), memory: 7), Work(Disk("E", "2"), memory: 2)]); scheduler.Pump();
        await jobs.Started("C"); await jobs.Started("E"); Assert.False(jobs.HasStarted("D"));
        jobs.Finish("C"); await scheduler.WaitForActivityAsync(default); scheduler.Pump();
        await jobs.Started("D"); jobs.Finish("E"); jobs.Finish("D"); await scheduler.WaitUntilIdleAsync();
        scheduler.Reconcile([Work(Disk("F", "3"), memory: 100)]); scheduler.Pump();
        await jobs.Started("F"); Assert.Equal(1, scheduler.ActiveCount); jobs.FinishAll();
    }

    [Fact]
    public async Task RefreshWhileRunningDoesNotStartDuplicateAndOnlyRunsLatestRevisionOnce()
    {
        int starts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VolumeWorkScheduler(4, 10, async (_, token) =>
        { Interlocked.Increment(ref starts); first.TrySetResult(); await release.Task.WaitAsync(token); });
        var disk = Disk("C", "0");
        scheduler.Reconcile([Work(disk)]); scheduler.Pump(); await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        scheduler.Reconcile([Work(disk, "two")]); scheduler.Reconcile([Work(disk, "three")]); scheduler.Pump();
        Assert.Equal(1, starts);
        release.SetResult(); await scheduler.WaitUntilIdleAsync();
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task FailedVolumeDoesNotStopOtherJobs()
    {
        var finished = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var scheduler = new VolumeWorkScheduler(2, 10, (work, _) =>
        {
            if (work.Volume.Root == "C") throw new IOException("bad disk");
            finished.Add(work.Volume.Root); return Task.CompletedTask;
        });
        scheduler.Reconcile([Work(Disk("C", "0")), Work(Disk("D", "1")), Work(Disk("E", "2"))]);
        await scheduler.WaitUntilIdleAsync();
        Assert.Equal(2, finished.Count); Assert.Single(scheduler.Failures);
    }

    [Fact]
    public async Task UnknownTopologyRunsAlone()
    {
        var jobs = new Jobs();
        await using var scheduler = new VolumeWorkScheduler(3, 10, jobs.Execute);
        scheduler.Reconcile([Work(Disk("C")), Work(Disk("D", "1")), Work(Disk("E", "2"))]); scheduler.Pump();
        await jobs.Started("C"); Assert.Equal(1, scheduler.ActiveCount);
        Assert.False(jobs.HasStarted("D"));
        jobs.Finish("C"); await scheduler.WaitForActivityAsync(default); scheduler.Pump();
        await jobs.Started("D"); await jobs.Started("E"); Assert.Equal(2, scheduler.ActiveCount); jobs.FinishAll();
    }

    [Fact]
    public async Task RemovedJobKeepsItsDeviceUntilExecutorActuallyExits()
    {
        var oldStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scheduler = new VolumeWorkScheduler(2, 10, async (work, _) =>
        {
            if (work.Volume.Root == "C") { oldStarted.TrySetResult(); await release.Task; }
            else nextStarted.TrySetResult();
        });
        try
        {
            scheduler.Reconcile([Work(Disk("C", "0"))]); scheduler.Pump(); await oldStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            scheduler.Reconcile([Work(Disk("D", "0"))]); scheduler.Pump();
            Assert.Equal(1, scheduler.ActiveCount); Assert.False(nextStarted.Task.IsCompleted);
            release.TrySetResult(); await scheduler.WaitForActivityAsync(default); scheduler.Pump();
            await nextStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.TrySetResult(); }
    }

    private sealed class Jobs
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> started = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource> releases = new();
        public async Task Execute(VolumeWork work, CancellationToken token)
        {
            var release = releases.GetOrAdd(work.Volume.Root, _ => new(TaskCreationOptions.RunContinuationsAsynchronously));
            started.GetOrAdd(work.Volume.Root, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
            await release.Task.WaitAsync(token);
        }
        public Task Started(string root) => started.GetOrAdd(root, _ => new(TaskCreationOptions.RunContinuationsAsynchronously)).Task.WaitAsync(TimeSpan.FromSeconds(5));
        public bool HasStarted(string root) => started.TryGetValue(root, out var result) && result.Task.IsCompleted;
        public void Finish(string root) => releases[root].TrySetResult();
        public void FinishAll() { foreach (var release in releases.Values) release.TrySetResult(); }
    }
}
