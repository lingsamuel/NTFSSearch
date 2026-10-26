using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class IndexFingerprintTests
{
    [Fact]
    public void CanonicalFingerprintIgnoresPhysicalNameIdsAndUnknownMetadata()
    {
        SearchIndex Build(bool reverse)
        {
            var builder = new IndexBuilder();
            foreach (int id in reverse ? new[] { 11, 12, 10 } : new[] { 10, 11, 12 })
                builder.Add(@"C:\", id == 11 ? "unique\ud800" : "repeat", fileReference: (ulong)id, parentReference: 5);
            return builder.Build();
        }
        var a = Build(false); var b = Build(true);
        Assert.NotEqual(a.Records[0].NameId, b.Records[0].NameId);
        b.Records[0] = new(b.Records[0].NameId, uint.MaxValue, long.MaxValue, 0, false, false);
        Assert.Equal(IndexFingerprint.Canonical(a, default), IndexFingerprint.Canonical(b, default));
        Assert.Equal(IndexFingerprint.Canonical(a, default), IndexFingerprint.Canonical(
            a.WithView([1UL], [@"C:\moved\"], default), default));
    }

    [Fact]
    public void CanonicalFingerprintIncludesMetadataCaseAndFullGeneration()
    {
        SearchIndex Build(string name, ulong reference, long size)
        {
            var builder = new IndexBuilder(); builder.Add(@"C:\", name, size, long.MinValue, fileReference: reference, parentReference: 5);
            return builder.Build();
        }
        byte[] original = IndexFingerprint.Canonical(Build("Case\ud800", 10, 1), default);
        Assert.NotEqual(original, IndexFingerprint.Canonical(Build("case\ud800", 10, 1), default));
        Assert.NotEqual(original, IndexFingerprint.Canonical(Build("Case\ud801", 10, 1), default));
        Assert.NotEqual(original, IndexFingerprint.Canonical(Build("Case\ud800", (1UL << 48) | 10, 1), default));
        Assert.NotEqual(original, IndexFingerprint.Canonical(Build("Case\ud800", 10, long.MaxValue), default));
    }

    [Fact]
    public void LegacyFingerprintHashesOriginal32ByteRecordsIn65536RowBatches()
    {
        string path = Path.Combine(Path.GetTempPath(), "legacy-hash-" + Guid.NewGuid().ToString("N") + ".ntidx");
        try
        {
            var builder = new IndexBuilder(65537);
            for (int i = 0; i < 65537; i++) builder.Add(@"C:\", "x", i, -i, fileReference: (ulong)i + 10, parentReference: 5);
            var baseline = builder.Build();
            LegacySnapshotFixture.Write(baseline, path);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (string directory in baseline.Directories) hash.AppendData(MemoryMarshal.AsBytes(directory.AsSpan()));
            for (int start = 0; start < baseline.Count; start += 65536)
            {
                int count = Math.Min(65536, baseline.Count - start);
                using var records = new MemoryStream(); using var writer = new BinaryWriter(records);
                for (int id = start; id < start + count; id++)
                {
                    writer.Write((long)id); writer.Write((long)id); writer.Write((long)-id); writer.Write(0);
                    writer.Write((ushort)1); writer.Write((ushort)6);
                }
                hash.AppendData(records.ToArray());
                hash.AppendData(MemoryMarshal.AsBytes(baseline.FileReferences!.AsSpan(start, count)));
            }
            hash.AppendData(MemoryMarshal.AsBytes(baseline.DirectoryReferences!.AsSpan()));
            hash.AppendData(MemoryMarshal.AsBytes(new string('x', baseline.Count).AsSpan()));
            Assert.Equal(hash.GetHashAndReset(), SnapshotStore.Load(path).CheckpointFingerprint);
        }
        finally { File.Delete(path); }
    }
}
