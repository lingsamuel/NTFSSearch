using System.Diagnostics;
using System.Text.Json;
using NtfsSearch.Core;

internal static class ExitSnapshotBenchmark
{
    internal static object Run(string legacyDirectory, string output, CancellationToken token)
    {
        var sources = new List<ExitSnapshotVolume>();
        var timer = Stopwatch.StartNew();
        foreach (string manifestName in Directory.EnumerateFiles(legacyDirectory, "*.ready.json").Order())
        {
            token.ThrowIfCancellationRequested();
            var manifest = JsonSerializer.Deserialize<VolumeManifest>(File.ReadAllText(manifestName));
            if (manifest?.Journal is not { } position || Path.GetFileName(manifest.SnapshotFile) != manifest.SnapshotFile)
                continue;
            var index = SnapshotStore.Load(Path.Combine(legacyDirectory, manifest.SnapshotFile), token);
            sources.Add(new(new(manifest.VolumeId, manifest.Root, 0, ""), position, [index]));
            Console.Error.WriteLine($"已读取 {manifest.Root} 基线 {index.VisibleCount:N0} 条");
        }
        if (sources.Count == 0) throw new InvalidDataException("未读取到带 USN 水位的旧版基线。");
        long expected = sources.Sum(source => (long)source.Layers[0].VisibleCount);
        double sourceSeconds = timer.Elapsed.TotalSeconds;

        timer.Restart();
        ExitSnapshotStore.Save(output, sources, token,
            (written, total) => Console.Error.WriteLine($"退出快照已写入 {written}/{total} 个卷"));
        double saveSeconds = timer.Elapsed.TotalSeconds;
        sources.Clear();

        timer.Restart();
        long restored = 0;
        int volumes = 0;
        int parallelism = IndexingConcurrencyPolicy.Create(Environment.ProcessorCount,
            VolumeResources.AvailableMemoryBytes()).CacheLoadLimit;
        foreach (var volume in ExitSnapshotStore.LoadVolumes(output, token, maxConcurrency: parallelism))
        {
            restored += volume.Layers.Sum(layer => (long)layer.VisibleCount);
            volumes++;
            Console.Error.WriteLine($"已恢复并重建排序 {volume.Volume.Root}");
        }
        if (expected != restored) throw new InvalidDataException($"退出快照恢复计数不符：{expected:N0} 和 {restored:N0}。");
        return new { Volumes = volumes, Parallelism = parallelism, Records = restored, SnapshotBytes = new FileInfo(output).Length,
            SourceSeconds = sourceSeconds, SaveSeconds = saveSeconds, ReloadAndSortSeconds = timer.Elapsed.TotalSeconds };
    }
}
