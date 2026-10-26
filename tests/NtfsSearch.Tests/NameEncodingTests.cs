using System.Runtime.InteropServices;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class NameEncodingTests
{
    [Fact]
    public void RandomAsciiShapesPreserveOriginalCaseAndMixedQueriesMatchFramework()
    {
        var random = new Random(1937);
        Span<char> decoded = stackalloc char[255];
        for (int sample = 0; sample < 20000; sample++)
        {
            char[] chars = Enumerable.Range(0, random.Next(1, 256)).Select(_ => (char)random.Next(128)).ToArray();
            if (sample % 2 == 0)
            {
                System.Text.Ascii.ToLowerInPlace(chars, out _);
                int cut = random.Next(chars.Length + 1);
                System.Text.Ascii.ToUpperInPlace(sample % 4 == 0 ? chars.AsSpan(0, cut) : chars.AsSpan(cut), out _);
            }
            if (!AsciiCaseShape.TryEncode(chars, out byte boundary, out bool suffix)) continue;
            byte[] bytes = AsciiQuery.Encode(new string(chars))!;
            System.Text.Ascii.ToUtf16(bytes, decoded, out int written);
            AsciiCaseShape.Restore(decoded[..written], boundary, suffix);
            Assert.True(decoded[..written].SequenceEqual(chars));
        }
        string[] parts = ["name", "Title.txt", "file.JPG", "UUID-ABC.jpg", "multiCamelCase.txt", "报告", "\u212a", "\u017f", "\u0130", "\u0131", "\u03a3", "\u03c2", "\ud800"];
        var builder = new IndexBuilder(); var names = new List<string>();
        for (int id = 0; id < 1000; id++)
        {
            string name = parts[random.Next(parts.Length)] + $"{id:D4}.txt"; names.Add(name);
            builder.Add(@"C:\", name, isDirectory: id % 17 == 0);
        }
        var index = builder.Build();
        foreach (string term in parts.Concat(new[] { "", "TXT", "a", "not-present" }))
        {
            long expected = names.Count(name => name.Contains(term, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(expected, index.Search(term, matchPath: false, limit: 1000).TotalCount);
        }
    }

    [Fact]
    public void ReversibleAsciiNamesSaveMemoryAndDirectoryNamesBorrowUtf16()
    {
        string path = Path.Combine(Path.GetTempPath(), $"name-codec-{Guid.NewGuid():N}.ntidx");
        try
        {
            string[] names = [new('a', 255), new('A', 255), "Title.txt", "file.JPG", "AA11-BB22.jpg", "multiCamelCase.txt", "孤\ud800"];
            var builder = new IndexBuilder();
            foreach (string name in names) builder.Add(@"C:\", name, long.MaxValue, long.MinValue);
            builder.Add(@"C:\", "directory", isDirectory: true);
            builder.Add(@"C:\", "shared"); builder.Add(@"C:\", "shared", isDirectory: true);
            var index = builder.Build();
            long rawBytes = names.Sum(name => 2L * name.Length + 8) + 2 * "directory".Length + 8 + 2 * "shared".Length + 8;
            Assert.True(index.Statistics.NameBytes < rawBytes - 500);
            for (int id = 0; id < names.Length; id++) Assert.Equal(names[id], index.GetRow(id).Name);
            foreach (int id in new[] { names.Length, names.Length + 2 })
            {
                Assert.True(MemoryMarshal.TryGetArray(index.NameMemory(id), out var memory));
                Assert.Contains(index.Names.Chunks, chunk => ReferenceEquals(chunk, memory.Array));
            }
            SnapshotStore.Save(index, path);
            var restored = SnapshotStore.Load(path);
            for (int id = 0; id < index.Count; id++) Assert.Equal(index.GetRow(id), restored.GetRow(id));
            foreach (SortField field in Enum.GetValues<SortField>()) foreach (bool descending in new[] { false, true })
                    Assert.Equal(index.Search("", field, descending).Rows, restored.Search("", field, descending).Rows);
        }
        finally { File.Delete(path); }
    }
}
