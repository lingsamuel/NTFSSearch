using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using DiscUtils.Ntfs;
using DiscUtils.Ntfs.Internals;

namespace NtfsSearch.Core;

// 句柄分享限制或特殊元文件不支持class46时，复用DiscUtils的只读属性/ATTRIBUTE_LIST解析。
internal static class RawMftState
{
    internal sealed record Name(int AttributeId, ulong ParentReference, byte Namespace, string Text);
    internal sealed record NameReference(ulong RecordReference, int AttributeId);
    internal sealed record Record(ulong Reference, ulong BaseReference, bool InUse, bool IsDirectory,
        IReadOnlyList<Name> Names, IReadOnlyList<NameReference> NameReferences, bool HasAttributeList,
        long? Size, long? ModifiedUtcTicks);

    internal static IndexedFileState Read(char drive, ulong reference)
    {
        using var metadata = new MftMetadataReader(drive);
        return metadata.ReadFileState(drive, reference);
    }

    internal static IndexedFileState Read(NtfsFileSystem filesystem, char drive, ulong reference)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var table = filesystem.GetMasterFileTable();
        var cache = new Dictionary<ulong, Record>();
        Record Load(ulong id)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            if (cache.TryGetValue(id, out var cached)) return cached;
            var entry = table[(long)(id & 0x0000ffffffffffffUL)];
            if (entry is null) return cache[id] = new(id, 0, false, false, [], [], false, null, null);
            var names = new List<Name>(); var references = new List<NameReference>();
            bool hasList = false; long? size = null, modified = null;
            foreach (var attribute in entry.Attributes)
            {
                if (attribute is FileNameAttribute)
                {
                    if (attribute.ContentLength is < 66 or > 576) throw new InvalidDataException("FILE_NAME属性长度无效。");
                    byte[] content = new byte[(int)attribute.ContentLength];
                    var buffer = attribute.Content; int read = 0;
                    while (read < content.Length)
                    {
                        int count = buffer.Read(read, content, read, content.Length - read);
                        if (count <= 0) throw new EndOfStreamException("FILE_NAME属性提前结束。");
                        read += count;
                    }
                    names.Add(DecodeName(attribute.Identifier, content));
                }
                else if (attribute is AttributeListAttribute list)
                {
                    hasList = true;
                    foreach (var item in list.Entries)
                        if (item.AttributeType == AttributeType.FileName)
                            references.Add(new(Reference(item.MasterFileTableEntry), item.AttributeIdentifier));
                }
                else if (attribute is StandardInformationAttribute standard)
                    modified = standard.ModificationTime.ToFileTimeUtc() > 0 ? standard.ModificationTime.Ticks : null;
                else if (attribute.AttributeType == AttributeType.Data && string.IsNullOrEmpty(attribute.Name))
                    size = Math.Max(size.GetValueOrDefault(), attribute.ContentLength);
            }
            ulong actual = ((ulong)entry.SequenceNumber << 48) | (ulong)entry.Index;
            ulong baseReference = Reference(entry.BaseRecordReference);
            bool inUse = (entry.Flags & MasterFileTableEntryFlags.InUse) != 0
                && (baseReference != 0 || entry.HardLinkCount > 0 || DirectoryPathResolver.IsRoot(actual));
            return cache[id] = new(actual, baseReference,
                inUse,
                (entry.Flags & MasterFileTableEntryFlags.IsDirectory) != 0,
                names, references, hasList, size, modified);
        }
        return BuildState(drive, reference, Load);
    }

    internal static IndexedFileState BuildState(char drive, ulong reference, Func<ulong, Record> load)
    {
        var file = load(reference);
        if (!file.InUse || file.Reference != reference) return new(reference, file.IsDirectory, []);
        if (file.BaseReference != 0) throw new InvalidDataException("对象FRN指向扩展记录，不能作为完整文件状态。");
        var names = Links(load, file);
        var directories = new Dictionary<ulong, (ulong Parent, string Name)>();
        foreach (var link in names)
        {
            ulong cursor = link.ParentReference;
            var visiting = new HashSet<ulong>();
            while (!directories.ContainsKey(cursor))
            {
                if (!visiting.Add(cursor) || visiting.Count > 16384) throw new InvalidDataException("原始MFT父目录链存在循环或过深。");
                var directory = load(cursor);
                if (!directory.InUse || directory.Reference != cursor || !directory.IsDirectory)
                    throw new InvalidDataException($"原始MFT父目录身份失效：0x{cursor:X16}。");
                if (DirectoryPathResolver.IsRoot(cursor)) break;
                var links = Links(load, directory);
                if (links.Count != 1) throw new InvalidDataException("原始MFT父目录没有唯一长名称。");
                directories.Add(cursor, (links[0].ParentReference, links[0].Name));
                cursor = links[0].ParentReference;
            }
        }
        var resolver = new DirectoryPathResolver(drive, directories, _ => null);
        var result = new List<IndexedLink>(names.Count);
        foreach (var link in names)
        {
            string parent = resolver.Resolve(link.ParentReference)
                ?? throw new InvalidDataException($"原始MFT父目录链无法解析：0x{link.ParentReference:X16}。");
            result.Add(new(link.ParentReference, parent, link.Name));
        }
        return new(reference, file.IsDirectory, result, file.IsDirectory ? null : file.Size, file.ModifiedUtcTicks);
    }

    internal static IReadOnlyList<(ulong ParentReference, string Name)> Links(Func<ulong, Record> load, Record file)
    {
        IEnumerable<Name> names = file.Names;
        if (file.HasAttributeList)
        {
            var collected = new List<Name>();
            var loaded = new Dictionary<ulong, Record> { [file.Reference] = file };
            var seen = new HashSet<(ulong, int)>();
            foreach (var item in file.NameReferences)
            {
                if (!seen.Add((item.RecordReference, item.AttributeId))) throw new InvalidDataException("ATTRIBUTE_LIST重复FILE_NAME引用。");
                if (!loaded.TryGetValue(item.RecordReference, out var record)) loaded[item.RecordReference] = record = load(item.RecordReference);
                if (!record.InUse || record.Reference != item.RecordReference
                    || (record.Reference != file.Reference && record.BaseReference != file.Reference))
                    throw new InvalidDataException("ATTRIBUTE_LIST扩展记录代际或基础身份不符。");
                var matching = record.Names.Where(name => name.AttributeId == item.AttributeId).ToArray();
                if (matching.Length != 1) throw new InvalidDataException("ATTRIBUTE_LIST引用的FILE_NAME属性不存在或不唯一。");
                collected.Add(matching[0]);
            }
            // 未被列表引用的基础属性不能静默混入，防止发布不完整或过期链接集合。
            if (file.Names.Any(name => !seen.Contains((file.Reference, name.AttributeId))))
                throw new InvalidDataException("ATTRIBUTE_LIST遗漏基础FILE_NAME属性。");
            names = collected;
        }
        var links = names.Where(name => name.Namespace != 2).Select(name => (name.ParentReference, Name: name.Text)).Distinct().ToArray();
        if (links.Length == 0 && !DirectoryPathResolver.IsRoot(file.Reference)) throw new InvalidDataException("存活对象未找到完整长名称集合。");
        return links;
    }

    internal static Name DecodeName(int attributeId, ReadOnlySpan<byte> content)
    {
        byte space = ValidateNameContent(content);
        return new(attributeId, BinaryPrimitives.ReadUInt64LittleEndian(content), space,
            MemoryMarshal.Cast<byte, char>(content[66..]).ToString());
    }

    internal static byte ValidateNameContent(ReadOnlySpan<byte> content)
    {
        if (content.Length < 66) throw new InvalidDataException("FILE_NAME属性头不完整。");
        int characters = content[64]; byte space = content[65];
        if (characters == 0 || space > 3 || content.Length != 66 + characters * 2) throw new InvalidDataException("FILE_NAME名称长度或namespace无效。");
        return space;
    }
    [SupportedOSPlatform("windows")]
    private static ulong Reference(MasterFileTableReference reference) =>
        ((ulong)reference.RecordSequenceNumber << 48) | (ulong)reference.RecordIndex;
}
