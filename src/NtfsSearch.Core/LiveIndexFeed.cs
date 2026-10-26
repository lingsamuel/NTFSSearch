using System.Collections.Concurrent;

namespace NtfsSearch.Core;

public sealed record LiveIndexUpdate(string VolumeKey, string Generation, IReadOnlyList<SearchIndex> Layers);
public sealed record ConsumerPosition(string LogId, long KeepAfterSequence, long AppliedSequence, JournalPosition? Position);

public sealed class LiveIndexFeed(string directory)
{
    public int ActiveVolumeCount => sessions.Values.Count(s => s.Active && !s.Detached);
    private readonly ConcurrentDictionary<string, Session> sessions = new(StringComparer.Ordinal);
    public event Action<LiveIndexUpdate>? Updated;
    public event Action<NtfsVolume, string>? Failed;

    public async Task<IReadOnlyList<SearchIndex>> AttachAsync(NtfsVolume volume, CachedVolume cached, CancellationToken token)
    {
        if (!cached.Index.HasIdentity || cached.Manifest?.Journal is not { } initial) return [cached.Index];
        string key = VolumeCache.Key(volume), generation = cached.Manifest.SnapshotFile;
        sessions.TryGetValue(key, out var previous);
        bool previousLocked = false, attached = false, wasActive = false;
        ChangeLogStore? reader = null;
        try
        {
            // 从准备开始就暂停旧会话；同代重新加载也不能在交接期间发布更晚的层。
            if (previous is not null)
            {
                await previous.Gate.WaitAsync(token); previousLocked = true;
                wasActive = previous.Active; previous.Active = false;
            }
            reader = new ChangeLogStore(directory, generation, initial);
            string checkpoint = Path.Combine(directory, generation + ".live-" + reader.LogId + ".state");
            LiveVolumeIndex? view = null;
            foreach (string path in new[] { checkpoint, checkpoint + ".bak" })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    var restored = LiveVolumeIndex.LoadCheckpoint(cached.Index, path, token);
                    if (restored.AppliedSequence <= reader.Checkpoint.Sequence) { view = restored; break; }
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
            view ??= new(cached.Index, initial, token);
            var session = new Session(volume, generation, reader, view, checkpoint);
            ApplyAvailable(session, token, int.MaxValue);
            session.PendingPublication = false;
            if (previous is not null) previous.Detached = true;
            sessions[key] = session; attached = true;
            return view.Layers;
        }
        catch { reader?.Dispose(); throw; }
        finally
        {
            if (previousLocked)
            {
                if (!attached && !previous!.Detached) previous.Active = wasActive;
                previous!.Gate.Release();
                // Detached已在锁内发布；旧Pump再次取锁后只会跳过，不再使用reader租约。
                if (attached) Release(previous);
            }
        }
    }

    public bool Activate(NtfsVolume volume, string? generation)
    {
        string key = VolumeCache.Key(volume);
        if (!sessions.TryGetValue(key, out var session) || session.Generation != generation) return false;
        session.Gate.Wait();
        try
        {
            if (session.Detached || !sessions.TryGetValue(key, out var current) || !ReferenceEquals(current, session)) return false;
            // Catalog已发布准备好的层，后续尾读事件才能基于这一代继续更新。
            session.Active = true; return true;
        }
        finally { session.Gate.Release(); }
    }

    public void RemoveExcept(IEnumerable<NtfsVolume> volumes)
    {
        var keys = volumes.Select(VolumeCache.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var pair in sessions)
        {
            if (keys.Contains(pair.Key)) continue;
            bool removed = false;
            pair.Value.Gate.Wait();
            try
            {
                removed = sessions.TryRemove(pair);
                if (removed) { pair.Value.Detached = true; pair.Value.Active = false; }
            }
            finally { pair.Value.Gate.Release(); }
            if (removed) Release(pair.Value);
        }
    }

    public async Task RunAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                foreach (var pair in sessions.ToArray())
                {
                    var session = pair.Value;
                    await session.Gate.WaitAsync(token);
                    bool disposeReader = false;
                    try
                    {
                        if (session.Detached || !session.Active || DateTime.UtcNow < session.NextReadAttemptUtc) continue;
                        ApplyAvailable(session, token, 128);
                        if (session.PendingPublication && sessions.TryGetValue(pair.Key, out var current) && ReferenceEquals(current, session))
                        {
                            Updated?.Invoke(new(pair.Key, session.Generation, session.View.Layers));
                            session.PendingPublication = false;
                        }
                        if (session.View.AppliedSequence != session.SavedSequence
                            && DateTime.UtcNow >= session.NextCheckpointAttemptUtc
                            && (DateTime.UtcNow - session.SavedUtc > TimeSpan.FromMinutes(5)
                                || session.Reader.Length > 32L << 20 && DateTime.UtcNow - session.SavedUtc > TimeSpan.FromSeconds(30)))
                        {
                            try { Save(session, token); }
                            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
                            {
                                // 检查点失败不影响健康WAL与已发布视图；保留旧确认水位，稍后重试。
                                session.NextCheckpointAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                            }
                        }
                    }
                    catch (Exception error) when (error is InvalidDataException or EndOfStreamException)
                    {
                        bool current = sessions.TryRemove(new KeyValuePair<string, Session>(pair.Key, session));
                        session.Detached = true; session.Active = false; disposeReader = true;
                        if (current) Failed?.Invoke(session.Volume, "实时增量恢复失败，需要重建本卷：" + error.Message);
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        // 暂时无法打开/读取日志时继续持有会话及租约，不把共享冲突当成数据损坏。
                        session.NextReadAttemptUtc = DateTime.UtcNow.AddSeconds(1);
                    }
                    finally { session.Gate.Release(); }
                    if (disposeReader) Release(session);
                }
            }
        }
        finally
        {
            foreach (var session in sessions.Values)
            {
                await session.Gate.WaitAsync();
                try
                {
                    if (session.Detached) continue;
                    try { if (session.View.AppliedSequence != session.SavedSequence) Save(session, CancellationToken.None); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
                    session.Detached = true; session.Active = false;
                }
                finally { session.Gate.Release(); Release(session); }
            }
        }
    }

    private void Release(Session session)
    {
        session.Reader.Dispose();
        try { new VolumeCache(directory).CleanupRetired(session.Volume); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    private static bool ApplyAvailable(Session session, CancellationToken token, int maximum)
    {
        bool changed = false;
        foreach (var transaction in session.Reader.ReadAfter(session.View.AppliedSequence, maximum, token))
        {
            bool applied = session.View.Apply(transaction, token);
            changed |= applied;
            // 后续帧暂时读取失败时，已应用的前缀仍需在恢复后发布。
            session.PendingPublication |= applied;
        }
        return changed;
    }

    private static void Save(Session session, CancellationToken token)
    {
        long previous = session.SavedSequence;
        session.View.SaveCheckpoint(session.Checkpoint, session.View.AppliedSequence, token);
        AtomicJson.Write(Path.Combine(Path.GetDirectoryName(session.Reader.LogPath)!, "consumer.json"),
            new ConsumerPosition(session.Reader.LogId, previous, session.View.AppliedSequence, session.View.Position));
        session.SavedSequence = session.View.AppliedSequence; session.SavedUtc = DateTime.UtcNow;
    }

    private sealed class Session(NtfsVolume volume, string generation, ChangeLogStore reader, LiveVolumeIndex view, string checkpoint)
    {
        public NtfsVolume Volume { get; } = volume;
        public string Generation { get; } = generation;
        public ChangeLogStore Reader { get; } = reader;
        public LiveVolumeIndex View { get; } = view;
        public string Checkpoint { get; } = checkpoint;
        public SemaphoreSlim Gate { get; } = new(1);
        public bool Detached;
        public bool Active;
        public bool PendingPublication;
        public DateTime NextReadAttemptUtc;
        public DateTime NextCheckpointAttemptUtc;
        public long SavedSequence = view.AppliedSequence;
        public DateTime SavedUtc = DateTime.UtcNow;
    }
}
