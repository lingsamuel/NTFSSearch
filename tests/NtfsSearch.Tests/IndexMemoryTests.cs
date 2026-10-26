using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class IndexMemoryTests
{
    [Fact]
    public void SmallBuilderDoesNotAllocateFullNameChunk()
    {
        var warmup = new IndexBuilder(1); warmup.Add(@"C:\", "warmup"); warmup.Build();
        long start = GC.GetAllocatedBytesForCurrentThread();
        var builder = new IndexBuilder(1); builder.Add(@"C:\", "one.txt");
        var index = builder.Build();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 64 * 1024, $"单条索引分配了 {allocated:N0} 字节。");
        Assert.Equal("one.txt", index.GetRow(0).Name);
    }

    [Fact]
    public void GrowingSmallNameArenaKeepsOffsetsAndSnapshotChunkProtocol()
    {
        string path = Path.Combine(Path.GetTempPath(), $"name-growth-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder(1);
            var names = Enumerable.Range(0, 36).Select(i => $"{i:D3}" + new string((char)('a' + i % 26), 65520)).ToArray();
            builder.Add(@"C:\", "first\ud800");
            foreach (string name in names) builder.Add(@"C:\", name);
            var index = builder.Build();
            Assert.True(index.Names.Chunks.Count > 1);
            Assert.All(index.Names.Chunks.SkipLast(1), chunk => Assert.Equal(NameArena.ChunkChars, chunk.Length));
            SnapshotStore.Save(index, path);
            var loaded = SnapshotStore.Load(path);
            Assert.Equal("first\ud800", loaded.GetRow(0).Name);
            for (int i = 0; i < names.Length; i++) Assert.Equal(names[i], loaded.GetRow(i + 1).Name);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryUpdateDoesNotAllocatePathsForUnrelatedDirectories(bool rename)
    {
        var baseline = DirectoryBaseline(20000);
        var live = new LiveVolumeIndex(baseline, new(1, 0));
        var state = new IndexedFileState(rename ? 100UL : 1000000UL, true, [new(5, @"C:\", "changed")]);
        var transaction = new LiveTransaction(new(1, 0), new(1, 1), [state]);
        long start = GC.GetAllocatedBytesForCurrentThread();
        live.Apply(transaction);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 1024 * 1024, $"仅改变一个目录分配了 {allocated:N0} 字节。");
        if (!rename) Assert.Same(baseline.Directories, live.Layers[0].Directories);
        for (int d = 0; d < baseline.Directories.Length; d++)
            if (!baseline.Directories[d].Contains("directory00000", StringComparison.Ordinal))
                Assert.Same(baseline.Directories[d], live.Layers[0].Directories[d]);
    }

    [Fact]
    public void CheckpointReusesUnchangedPathsEvenWhenOneDirectoryChanged()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"checkpoint-sharing-{Guid.NewGuid():N}");
        try
        {
            var baseline = DirectoryBaseline(20);
            var live = new LiveVolumeIndex(baseline, new(1, 0));
            live.Apply(new LiveTransaction(new(1, 0), new(1, 1), [new(100, true, [new(5, @"C:\", "changed")])]));
            live.SaveCheckpoint(Path.Combine(dir, "current"), 0);
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, Path.Combine(dir, "current"));
            for (int d = 0; d < baseline.Directories.Length; d++)
                if (baseline.Directories[d] == restored.Layers[0].Directories[d])
                    Assert.Same(baseline.Directories[d], restored.Layers[0].Directories[d]);
            Assert.Equal(live.Layers[0].GetRow(1).FullPath, restored.Layers[0].GetRow(1).FullPath);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void CheckpointLoadAndSegmentCleanupAvoidTemporaryDirectoryText()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"checkpoint-allocation-{Guid.NewGuid():N}");
        try
        {
            var baseline = DirectoryBaseline(4096, 384);
            var live = new LiveVolumeIndex(baseline);
            string path = Path.Combine(dir, "current");
            live.SaveCheckpoint(path, 0);
            long start = GC.GetAllocatedBytesForCurrentThread();
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            long loaded = GC.GetAllocatedBytesForCurrentThread() - start;
            long graphPayload = live.DirectoryGraph.Sum(pair => (long)pair.Value.Name.Length * 2 + 144);
            Assert.True(loaded < graphPayload + 3 * 1024 * 1024, $"检查点恢复分配了 {loaded:N0} 字节，目录图预算 {graphPayload:N0}。");
            Assert.Same(baseline.Directories, restored.Layers[0].Directories);
            start = GC.GetAllocatedBytesForCurrentThread();
            live.SaveCheckpoint(path, 0);
            long saved = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(saved < 5 * 1024 * 1024, $"只提取段ID的清理流程分配了 {saved:N0} 字节。");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    private static SearchIndex DirectoryBaseline(int directories, int padding = 0)
    {
        var builder = new IndexBuilder(directories * 2);
        for (int i = 0; i < directories; i++)
        {
            string name = $"directory{i:D5}" + new string('x', padding);
            ulong reference = (ulong)(100 + i);
            builder.Add(@"C:\", name, isDirectory: true, fileReference: reference, parentReference: 5);
            builder.Add(@"C:\" + name + @"\", "file.txt", fileReference: (ulong)(directories + 100 + i), parentReference: reference);
        }
        return builder.Build();
    }
}
