using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class StorageTests
{
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
