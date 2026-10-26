namespace NtfsSearch.Core;

public sealed record VolumeWork(NtfsVolume Volume, string Revision, long EstimatedBytes);
public sealed record VolumeWorkFailure(VolumeWork Work, Exception Error);

public sealed class VolumeWorkScheduler(int maxConcurrency, long memoryBudget,
    Func<VolumeWork, CancellationToken, Task> execute) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, State> requested = new(StringComparer.Ordinal);
    private readonly List<Running> running = [];
    private readonly List<VolumeWorkFailure> failures = [];
    private bool disposed;
    public int ActiveCount { get { lock (gate) return running.Count; } }
    public int PeakActive { get; private set; }
    public IReadOnlyList<string> ActiveRoots { get { lock (gate) return running.Select(j => j.Work.Volume.Root).ToArray(); } }
    public IReadOnlyList<VolumeWorkFailure> Failures { get { lock (gate) return failures.ToArray(); } }

    public void Reconcile(IEnumerable<VolumeWork> work)
    {
        var current = work.ToDictionary(w => VolumeCache.Key(w.Volume), StringComparer.Ordinal);
        var cancel = new List<CancellationTokenSource>();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (string key in requested.Keys.Except(current.Keys).ToArray())
            {
                var owner = requested[key]; requested.Remove(key);
                cancel.AddRange(running.Where(j => ReferenceEquals(j.Owner, owner)).Select(j => j.Cancellation));
            }
            foreach (var pair in current)
            {
                if (pair.Value.EstimatedBytes < 0) throw new ArgumentOutOfRangeException(nameof(work));
                if (requested.TryGetValue(pair.Key, out var state)) state.Work = pair.Value;
                else requested.Add(pair.Key, new(pair.Value));
            }
        }
        foreach (var source in cancel) source.Cancel();
    }

    public void Pump(CancellationToken token = default, bool allowNew = true)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (maxConcurrency < 1 || memoryBudget < 1) throw new InvalidOperationException("调度并发与内存预算必须为正数。");
            foreach (var job in running.Where(j => j.Task.IsCompleted).ToArray())
            {
                var error = job.Task.GetAwaiter().GetResult();
                if (!job.Cancellation.IsCancellationRequested)
                {
                    job.Owner.Completed = job.Work.Revision;
                    if (error is not null)
                    {
                        failures.Add(new(job.Work, error));
                        if (failures.Count > 128) failures.RemoveAt(0);
                    }
                }
                running.Remove(job); job.Cancellation.Dispose();
            }
            if (!allowNew) return;
            var busy = running.SelectMany(j => Devices(j.Work.Volume)).ToHashSet(StringComparer.Ordinal);
            long reserved = running.Sum(j => j.Work.EstimatedBytes);
            foreach (var state in requested.Values)
            {
                if (running.Count >= maxConcurrency) break;
                if (state.Completed == state.Work.Revision || running.Any(j => j.Owner.Key == state.Key)) continue;
                string[] devices = Devices(state.Work.Volume);
                if (devices.Any(busy.Contains) || busy.Contains("unknown-device")
                    || running.Count > 0 && devices.Contains("unknown-device")) continue;
                // 大于估算预算的单卷仍可独占运行；有任务运行时严格限制累计预留。
                if (running.Count > 0 && state.Work.EstimatedBytes > memoryBudget - Math.Min(reserved, memoryBudget)) continue;
                var source = CancellationTokenSource.CreateLinkedTokenSource(token);
                var work = state.Work;
                Task<Exception?> task = Task.Run(async () =>
                {
                    try { await execute(work, source.Token); return null; }
                    catch (Exception error) { return error; }
                }, CancellationToken.None);
                running.Add(new(state, work, source, task));
                reserved = checked(reserved + work.EstimatedBytes);
                busy.UnionWith(devices);
                PeakActive = Math.Max(PeakActive, running.Count);
            }
        }
    }

    public async Task WaitForActivityAsync(CancellationToken token)
    {
        Task[] tasks;
        lock (gate) tasks = running.Select(j => (Task)j.Task).ToArray();
        if (tasks.Length > 0) await Task.WhenAny(tasks).WaitAsync(token);
    }

    public async Task WaitUntilIdleAsync(CancellationToken token = default)
    {
        while (true)
        {
            Pump(token);
            lock (gate)
                if (running.Count == 0 && requested.Values.All(s => s.Completed == s.Work.Revision)) return;
            await WaitForActivityAsync(token);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Running[] jobs;
        lock (gate) { if (disposed) return; disposed = true; jobs = running.ToArray(); }
        foreach (var job in jobs) await job.Cancellation.CancelAsync();
        await Task.WhenAll(jobs.Select(j => j.Task));
        foreach (var job in jobs) job.Cancellation.Dispose();
        lock (gate) running.Clear();
    }

    private static string[] Devices(NtfsVolume volume) => volume.PhysicalDisks is { Length: > 0 } disks ? disks : ["unknown-device"];
    private sealed class State(VolumeWork work) { public string Key { get; } = VolumeCache.Key(work.Volume); public VolumeWork Work = work; public string? Completed; }
    private sealed record Running(State Owner, VolumeWork Work, CancellationTokenSource Cancellation, Task<Exception?> Task);
}
