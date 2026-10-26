using System.Buffers.Binary;
using System.Text;
using NtfsSearch.Core;
using Xunit;

namespace NtfsSearch.Tests;

public class UsnTests
{
    [Fact]
    public void DecodesVersionTwoAndRejectsTruncationAndUnknownVersions()
    {
        byte[] name = Encoding.Unicode.GetBytes("报告.txt");
        byte[] data = new byte[8 + 64 + name.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 64 + name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 2);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), 123);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(24), 5);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(64), (ushort)name.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(66), 64);
        name.CopyTo(data, 72);
        var names = new List<string>();
        UsnParser.Visit(data, (frn, parent, text, attributes) =>
        {
            Assert.Equal(123UL, frn); Assert.Equal(5UL, parent); names.Add(text.ToString());
        });
        Assert.Equal(new[] { "报告.txt" }, names);
        Assert.Throws<InvalidDataException>(() => UsnParser.Visit(data[..^1], (_, _, _, _) => { }));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(12), 3);
        Assert.Throws<NotSupportedException>(() => UsnParser.Visit(data, (_, _, _, _) => { }));
    }
}
