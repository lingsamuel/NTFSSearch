using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class CompactDirectoryGraphTests
{
    private static SearchIndex Baseline(int count = 1000)
    {
        var builder = new IndexBuilder(count);
        for (int i = 0; i < count; i++)
            builder.Add(@"C:\", "directory" + i, isDirectory: true, fileReference: (ulong)i + 10, parentReference: 5);
        return builder.Build();
    }

    [Fact]
    public void BaselineDirectoryGraphDoesNotDuplicateNodesOrNames()
    {
        var baseline = Baseline(10000);
        long start = GC.GetAllocatedBytesForCurrentThread();
        var live = new LiveVolumeIndex(baseline);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 200_000, $"目录图分配了 {allocated:N0} 字节。");
        Assert.True(live.DirectoryGraph.TryGetValue(5010, out var node));
        Assert.Equal(5UL, node.Parent);
        Assert.Equal("directory5000", node.Name.ToString());
    }

    [Fact]
    public void CheckpointWritesCompactFormatAndOnlyOverrides()
    {
        string path = Path.Combine(Path.GetTempPath(), "compact-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline();
            var live = new LiveVolumeIndex(baseline);
            live.SaveCheckpoint(path, 0);
            using var stream = File.OpenRead(path);
            var magic = new byte[8]; stream.ReadExactly(magic);
            Assert.Equal("NTSLIVE2"u8.ToArray(), magic);
            Assert.True(live.DirectoryGraph.Count < 4);
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.Equal(1000, restored.Layers[0].Count);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void LegacyCheckpointRequiresEveryBaselineDirectoryIdentity()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline(2);
            WriteLegacy(baseline, path, [(5, 0, "", @"C:\", false), (10, 5, "directory0", null, false)]);
            Assert.Throws<InvalidDataException>(() => LiveVolumeIndex.LoadCheckpoint(baseline, path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void LegacyCheckpointPreservesCompactedAnchorsAndDeletedNodes()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline(2);
            WriteLegacy(baseline, path, [(5, 0, "", @"C:\", false), (10, 0, "", null, true),
                (11, 5, "directory1", null, false), (88, 0, "", @"C:\historic\", false)]);
            var live = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.True(live.DirectoryGraph.TryGetValue(10, out var deleted)); Assert.True(deleted.Deleted);
            Assert.True(live.DirectoryGraph.TryGetValue(88, out var anchor)); Assert.Equal(@"C:\historic\", anchor.RootPath);
            live.Apply(new LiveTransaction(new(1, 0), new(1, 1), [new(100, false, [new(88, @"C:\stale\", "new.txt")], 3, 4)]));
            Assert.Equal(@"C:\historic\new.txt", live.Layers[^1].Search("new.txt").Rows.Single().FullPath);
            live.SaveCheckpoint(path, 1);
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            Assert.True(restored.DirectoryGraph.TryGetValue(10, out deleted)); Assert.True(deleted.Deleted);
            Assert.True(restored.DirectoryGraph.TryGetValue(88, out anchor)); Assert.Equal(@"C:\historic\", anchor.RootPath);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void BaselineLookupBorrowsNameMemoryAndDoesNotAllocate()
    {
        var baseline = Baseline();
        var graph = new LiveVolumeIndex(baseline).DirectoryGraph;
        for (int i = 0; i < 10000; i++) graph.TryGetValue((ulong)(10 + i % 1000), out _);
        static (bool Found, long Allocated) Measure(DirectoryGraph graph, int count)
        {
            bool found = true;
            long start = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < count; i++) found &= graph.TryGetValue((ulong)(10 + i % 1000), out _);
            return (found, GC.GetAllocatedBytesForCurrentThread() - start);
        }
        var small = Measure(graph, 10000); var large = Measure(graph, 100000);
        Assert.True(small.Found && large.Found);
        // 允许运行时一次性初始化；每次创建RowRange的实现会随调用数增长，明确不能通过。
        Assert.InRange(small.Allocated, 0, 1024); Assert.InRange(large.Allocated, 0, 1024);
        Assert.InRange(large.Allocated - small.Allocated, -1024, 1024);
        Assert.True(graph.TryGetValue(10, out var node));
        Assert.True(MemoryMarshal.TryGetArray(node.Name, out var borrowed));
        Assert.Contains(baseline.Names.Chunks, chunk => ReferenceEquals(chunk, borrowed.Array));
    }

    [Fact]
    public void LegacyUnchangedGraphDoesNotAllocatePerDirectoryNamesOrNodes()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline(10000);
            var nodes = Enumerable.Range(0, baseline.Count).Select(id =>
                ((ulong)id + 10, 5UL, baseline.GetRow(id).Name, (string?)null, false)).ToList();
            nodes.Add((5, 0, "", @"C:\", false));
            WriteLegacy(baseline, path, nodes.ToArray());
            long start = GC.GetAllocatedBytesForCurrentThread();
            var restored = LiveVolumeIndex.LoadCheckpoint(baseline, path);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            Assert.True(allocated < 1024 * 1024 + 200_000, $"旧目录图恢复分配了 {allocated:N0} 字节。");
            Assert.Equal(1, restored.DirectoryGraph.Count);
        }
        finally { Delete(path); }
    }

    [Fact]
    public void LegacyV3SourceFingerprintSurvivesV4ResaveAndOldCheckpoint()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        string snapshot = path + ".ntidx";
        try
        {
            LegacySnapshotFixture.Write(Baseline(2), snapshot);
            var baseline = SnapshotStore.Load(snapshot);
            byte[] fingerprint = baseline.CheckpointFingerprint!;
            WriteLegacy(baseline, path, [(5, 0, "", @"C:\", false), (10, 5, "directory0", null, false),
                (11, 5, "directory1", null, false)]);
            Assert.Equal(1, LiveVolumeIndex.LoadCheckpoint(baseline, path).DirectoryGraph.Count);
            SnapshotStore.Save(baseline, snapshot);
            var compact = SnapshotStore.Load(snapshot);
            Assert.Equal(fingerprint, compact.CheckpointFingerprint);
            Assert.Equal(1, LiveVolumeIndex.LoadCheckpoint(compact, path).DirectoryGraph.Count);
        }
        finally { Delete(path); Delete(snapshot); }
    }

    [Fact]
    public void LegacyCheckpointRejectsMissingAnchorAndDuplicateIdentities()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            var baseline = Baseline(1);
            WriteLegacy(baseline, path, [(10, 5, "directory0", null, false)]);
            Assert.Throws<InvalidDataException>(() => LiveVolumeIndex.LoadCheckpoint(baseline, path));
            WriteLegacy(baseline, path, [(5, 0, "", @"C:\", false),
                (10, 5, "directory0", null, false), (10, 0, "", null, true)]);
            Assert.Throws<InvalidDataException>(() => LiveVolumeIndex.LoadCheckpoint(baseline, path));
            WriteLegacy(baseline, path, [(5, 0, "", @"C:\", false),
                (10, 5, "directory0", null, false), (5, 0, "", @"D:\", false)]);
            Assert.Throws<InvalidDataException>(() => LiveVolumeIndex.LoadCheckpoint(baseline, path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public void RevertingDirectoryToBaselineRemovesOverlay()
    {
        var baseline = Baseline(1);
        var live = new LiveVolumeIndex(baseline, new(1, 0));
        live.Apply(new LiveTransaction(new(1, 0), new(1, 1), [new(10, true, [new(5, @"C:\", "changed")])]));
        Assert.Equal(2, live.DirectoryGraph.Count);
        live.Apply(new LiveTransaction(new(1, 1), new(1, 2), [new(10, true, [new(5, @"C:\", "directory0")])]));
        Assert.Equal(1, live.DirectoryGraph.Count);
        Assert.True(live.DirectoryGraph.TryGetValue(10, out var node)); Assert.Equal("directory0", node.Name.ToString());
    }

    [Fact]
    public void BaselineLookupUsesLastVisibleDirectoryRowAndFullGeneration()
    {
        var builder = new IndexBuilder();
        builder.Add(@"C:\", "first", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\", "last", isDirectory: true, fileReference: 10, parentReference: 5);
        builder.Add(@"C:\", "new-generation", isDirectory: true, fileReference: (1UL << 48) | 10, parentReference: 5);
        var baseline = builder.Build();
        var graph = new LiveVolumeIndex(baseline).DirectoryGraph;
        Assert.True(graph.TryGetValue(10, out var last)); Assert.Equal("last", last.Name.ToString());
        Assert.True(graph.TryGetValue((1UL << 48) | 10, out var current)); Assert.Equal("new-generation", current.Name.ToString());
        var hidden = baseline.WithView([2UL], baseline.Directories, default);
        var hiddenGraph = new LiveVolumeIndex(hidden).DirectoryGraph;
        Assert.True(hiddenGraph.TryGetValue(10, out var first)); Assert.Equal("first", first.Name.ToString());
    }

    internal static void WriteLegacy(SearchIndex index, string path,
        (ulong Reference, ulong Parent, string Name, string? Root, bool Deleted)[] nodes,
        long sequence = 0, JournalPosition? position = null)
    {
        index.CheckpointFingerprint ??= SHA256.HashData("legacy-test-source"u8);
        SnapshotStore.SaveChecked(path, (writer, stream) =>
        {
            writer.Write("NTSLIVE1"u8); writer.Write(index.CheckpointFingerprint); writer.Write(sequence);
            writer.Write(position is not null);
            if (position is not null) { writer.Write(position.JournalId); writer.Write(position.NextUsn); }
            writer.Write(1); Text(writer, stream, index.StorageId); writer.Write(index.Count);
            writer.Write(false); writer.Write(index.Directories.Length);
            foreach (string directory in index.Directories) Text(writer, stream, directory);
            writer.Write(nodes.Length);
            foreach (var node in nodes)
            {
                writer.Write(node.Reference); writer.Write(node.Parent); writer.Write(node.Deleted);
                Text(writer, stream, node.Name); writer.Write(node.Root is not null);
                if (node.Root is not null) Text(writer, stream, node.Root);
            }
        }, default);
    }

    private static void Text(BinaryWriter writer, Stream stream, string text)
    { writer.Write(text.Length); stream.Write(MemoryMarshal.AsBytes(text.AsSpan())); }
    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".bak")) File.Delete(path + ".bak");
        if (Directory.Exists(path + ".segments")) Directory.Delete(path + ".segments", true);
    }
}
