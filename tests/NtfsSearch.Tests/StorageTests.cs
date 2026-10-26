using NtfsSearch.Core;
using System.Buffers.Binary;
using System.Security.Cryptography;
using Xunit;

namespace NtfsSearch.Tests;

public class StorageTests
{
    [Fact]
    public void SemanticallyUnsortedCacheWithValidChecksumIsRejected()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntfs-bad-sort-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            foreach (string name in new[] { "z", "b", "a" }) builder.Add(@"C:\", name);
            var index = builder.Build(); SnapshotStore.Save(index, path);
            byte[] bytes = File.ReadAllBytes(path);
            int order = checked(40 + index.Directories.Sum(d => 4 + d.Length * 2) + index.Names.Count * 8
                + index.Names.Chunks.Sum(c => 4 + c.Length * 2) + index.Names.AsciiChunks.Sum(c => 4 + c.Length) + index.BigSizes.Length * 8 + index.Count * 20 + 8);
            int first = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(order));
            int last = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(order + 8));
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(order), last);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(order + 8), first);
            SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32), bytes.AsSpan(bytes.Length - 32));
            File.WriteAllBytes(path, bytes);
            Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(path));
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void CachedOrdersAreRestoredInsteadOfRebuiltOnStartup()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntfs-orders-{Guid.NewGuid():N}.ntidx");
        try
        {
            var builder = new IndexBuilder();
            builder.Add(@"C:\", "z"); builder.Add(@"D:\", "a");
            SnapshotStore.Save(builder.Build(), path);
            var stages = new StageLog();
            var loaded = SnapshotStore.Load(path, progress: stages);
            Assert.DoesNotContain(stages.Messages, s => s.StartsWith("建立"));
            Assert.Equal(16, loaded.Statistics.SortBytes);
            Assert.Equal(new[] { "a", "z" }, loaded.Search("").Rows.Select(r => r.Name));
        }
        finally { File.Delete(path); }
    }

    private sealed class StageLog : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
    [Fact]
    public void CsvQuotesMultilineUnicodeAndFileTimesAreImported()
    {
        string csv = "Filename,Size,Date Modified,Attributes\r\n\"C:\\a,b\\hello \"\"world\"\".txt\",12,133444736000000000,32\r\n\"D:\\工作\\line\nname.txt\",,,32\r\n";
        var index = EfuImporter.Read(new StringReader(csv));
        Assert.Equal(2, index.Count);
        var row = Assert.Single(index.Search("hello").Rows);
        Assert.Equal("hello \"world\".txt", row.Name);
        Assert.Equal(12, row.Size);
        Assert.Equal(DateTime.FromFileTimeUtc(133444736000000000).Ticks, row.ModifiedUtcTicks);
        Assert.Equal(1, index.Search("工作").TotalCount);
    }

    [Fact]
    public void SnapshotRoundTripPreservesUnpairedUtf16AndDetectsCorruption()
    {
        string path = Path.Combine(Path.GetTempPath(), $"ntfs-search-test-{Guid.NewGuid():N}.ntidx");
        try
        {
            var b = new IndexBuilder();
            b.Add(@"C:\工作\", "name\ud800.txt", 42, 100);
            var original = b.Build();
            SnapshotStore.Save(original, path);
            var loaded = SnapshotStore.Load(path);
            Assert.Equal(original.GetRow(0), loaded.GetRow(0));
            SnapshotStore.Save(loaded, path);
            Assert.True(File.Exists(path + ".bak"));
            byte[] data = File.ReadAllBytes(path); data[40] ^= 1; File.WriteAllBytes(path, data);
            Assert.Throws<InvalidDataException>(() => SnapshotStore.Load(path));
            Assert.Equal(1, SnapshotStore.Load(path + ".bak").Count);
        }
        finally { File.Delete(path); File.Delete(path + ".bak"); }
    }
}
