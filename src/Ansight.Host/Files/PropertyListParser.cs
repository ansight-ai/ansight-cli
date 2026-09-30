using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Ansight.Host.Files;

internal static class PropertyListParser
{
    private const int MaximumDepth = 128;
    private const int MaximumNodes = 8_000;
    private const int MaximumValueLength = 2_000;
    private static readonly DateTimeOffset AppleReferenceDate =
        new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static PropertyListPreview Parse(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("The property list is empty.");
        }

        var isBinary = data.Span.StartsWith("bplist00"u8);
        var value = isBinary
            ? new BinaryPropertyListReader(data).Read()
            : ReadXml(data);
        var context = new StructuredDataContext();
        var root = BuildStructuredNode("root", value, context);
        var document = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement("plist", new XAttribute("version", "1.0"), ToXml(value)));
        var sourceText = isBinary
            ? document.ToString(SaveOptions.None)
            : document.ToString(SaveOptions.DisableFormatting);
        return new PropertyListPreview(
            sourceText,
            isBinary ? "binary plist" : "XML plist",
            new FileStructuredData(root, context.IsTruncated));
    }

    private static PropertyListValue ReadXml(ReadOnlyMemory<byte> data)
    {
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            MaxCharactersInDocument = Math.Max(data.Length * 4L, 1_024L)
        });
        var document = XDocument.Load(reader, LoadOptions.None);
        if (document.Root is not { Name.LocalName: "plist" } plist)
        {
            throw new InvalidDataException("The XML document does not contain a plist root element.");
        }

        var valueElement = plist.Elements().FirstOrDefault()
            ?? throw new InvalidDataException("The property list does not contain a root value.");
        return ReadXmlValue(valueElement, depth: 0);
    }

    private static PropertyListValue ReadXmlValue(XElement element, int depth)
    {
        EnsureDepth(depth);
        return element.Name.LocalName switch
        {
            "dict" => ReadXmlDictionary(element, depth + 1),
            "array" => new PropertyListValue(
                "array",
                null,
                element.Elements()
                    .Select((child, index) => new PropertyListEntry(
                        $"[{index}]",
                        ReadXmlValue(child, depth + 1)))
                    .ToArray()),
            "string" => Scalar("string", element.Value),
            "integer" => Scalar("integer", element.Value.Trim()),
            "real" => Scalar("real", element.Value.Trim()),
            "date" => Scalar("date", element.Value.Trim()),
            "data" => Scalar("data", RemoveWhitespace(element.Value)),
            "true" => Scalar("boolean", "true"),
            "false" => Scalar("boolean", "false"),
            "null" => Scalar("null", "null"),
            _ => throw new InvalidDataException(
                $"The property list contains an unsupported <{element.Name.LocalName}> value.")
        };
    }

    private static PropertyListValue ReadXmlDictionary(XElement element, int depth)
    {
        var children = element.Elements().ToArray();
        if (children.Length % 2 != 0)
        {
            throw new InvalidDataException("A property-list dictionary has a key without a value.");
        }

        var entries = new List<PropertyListEntry>(children.Length / 2);
        for (var index = 0; index < children.Length; index += 2)
        {
            if (children[index].Name.LocalName != "key")
            {
                throw new InvalidDataException("A property-list dictionary entry does not begin with a key.");
            }
            entries.Add(new PropertyListEntry(
                children[index].Value,
                ReadXmlValue(children[index + 1], depth)));
        }
        return new PropertyListValue("dictionary", null, entries);
    }

    private static XElement ToXml(PropertyListValue value)
        => value.Kind switch
        {
            "dictionary" => new XElement(
                "dict",
                value.Entries.SelectMany(static entry => new object[]
                {
                    new XElement("key", entry.Name),
                    ToXml(entry.Value)
                })),
            "array" or "set" => new XElement(
                "array",
                value.Entries.Select(static entry => ToXml(entry.Value))),
            "string" => new XElement("string", value.Value ?? string.Empty),
            "integer" or "uid" => new XElement("integer", value.Value ?? "0"),
            "real" => new XElement("real", value.Value ?? "0"),
            "date" => new XElement("date", value.Value ?? string.Empty),
            "data" => new XElement("data", value.Value ?? string.Empty),
            "boolean" => new XElement(
                string.Equals(value.Value, "true", StringComparison.OrdinalIgnoreCase)
                    ? "true"
                    : "false"),
            "null" => new XElement("null"),
            _ => new XElement("string", value.Value ?? string.Empty)
        };

    private static FileStructuredNode BuildStructuredNode(
        string name,
        PropertyListValue value,
        StructuredDataContext context)
    {
        if (!context.TryAddNode())
        {
            return new FileStructuredNode(
                "…",
                "truncated",
                "Additional items are not shown",
                []);
        }

        var children = new List<FileStructuredNode>();
        foreach (var entry in value.Entries)
        {
            if (context.IsAtLimit)
            {
                children.Add(new FileStructuredNode(
                    "…",
                    "truncated",
                    "Additional items are not shown",
                    []));
                context.MarkTruncated();
                break;
            }
            children.Add(BuildStructuredNode(entry.Name, entry.Value, context));
        }

        var summary = value.Kind switch
        {
            "dictionary" => $"{value.Entries.Count:N0} keys",
            "array" or "set" => $"{value.Entries.Count:N0} items",
            _ => TrimValue(value.Value)
        };
        return new FileStructuredNode(name, value.Kind, summary, children);
    }

    private static PropertyListValue Scalar(string kind, string? value)
        => new(kind, value, []);

    private static string RemoveWhitespace(string value)
        => string.Concat(value.Where(static character => !char.IsWhiteSpace(character)));

    private static string? TrimValue(string? value)
        => value is { Length: > MaximumValueLength }
            ? $"{value[..MaximumValueLength]}…"
            : value;

    private static void EnsureDepth(int depth)
    {
        if (depth > MaximumDepth)
        {
            throw new InvalidDataException("The property list exceeds the supported nesting depth.");
        }
    }

    private sealed record PropertyListEntry(string Name, PropertyListValue Value);

    private sealed record PropertyListValue(
        string Kind,
        string? Value,
        IReadOnlyList<PropertyListEntry> Entries);

    private sealed class StructuredDataContext
    {
        private int nodeCount;

        public bool IsAtLimit => nodeCount >= MaximumNodes;

        public bool IsTruncated { get; private set; }

        public bool TryAddNode()
        {
            if (IsAtLimit)
            {
                IsTruncated = true;
                return false;
            }
            nodeCount++;
            return true;
        }

        public void MarkTruncated()
        {
            IsTruncated = true;
        }
    }

    private sealed class BinaryPropertyListReader
    {
        private readonly ReadOnlyMemory<byte> data;
        private readonly HashSet<ulong> activeObjects = [];
        private readonly int objectReferenceSize;
        private readonly ulong objectCount;
        private readonly int offsetSize;
        private readonly ulong offsetTableOffset;
        private readonly ulong topObject;

        public BinaryPropertyListReader(ReadOnlyMemory<byte> data)
        {
            this.data = data;
            if (data.Length < 40 || !data.Span.StartsWith("bplist00"u8))
            {
                throw new InvalidDataException("The binary property-list header is invalid.");
            }

            var trailer = data.Span[^32..];
            offsetSize = trailer[6];
            objectReferenceSize = trailer[7];
            objectCount = BinaryPrimitives.ReadUInt64BigEndian(trailer[8..16]);
            topObject = BinaryPrimitives.ReadUInt64BigEndian(trailer[16..24]);
            offsetTableOffset = BinaryPrimitives.ReadUInt64BigEndian(trailer[24..32]);
            if (offsetSize is < 1 or > 8
                || objectReferenceSize is < 1 or > 8
                || objectCount == 0
                || objectCount > 1_000_000
                || topObject >= objectCount
                || offsetTableOffset >= (ulong)(data.Length - 32)
                || objectCount > ((ulong)(data.Length - 32) - offsetTableOffset) / (ulong)offsetSize)
            {
                throw new InvalidDataException("The binary property-list trailer is invalid.");
            }
        }

        public PropertyListValue Read()
            => ReadObject(topObject, depth: 0);

        private PropertyListValue ReadObject(ulong objectIndex, int depth)
        {
            EnsureDepth(depth);
            if (objectIndex >= objectCount || !activeObjects.Add(objectIndex))
            {
                throw new InvalidDataException("The binary property list contains an invalid object graph.");
            }

            try
            {
                var offset = ReadOffset(objectIndex);
                var marker = ReadByte(offset);
                var type = marker >> 4;
                var info = (byte)(marker & 0x0f);
                return type switch
                {
                    0x0 => ReadSimple(info),
                    0x1 => ReadInteger(offset + 1, info),
                    0x2 => ReadReal(offset + 1, info),
                    0x3 => ReadDate(offset + 1, info),
                    0x4 => ReadData(offset + 1, info),
                    0x5 => ReadAsciiString(offset + 1, info),
                    0x6 => ReadUtf16String(offset + 1, info),
                    0x8 => ReadUid(offset + 1, info),
                    0xa => ReadCollection(offset + 1, info, depth + 1, "array"),
                    0xb or 0xc => ReadCollection(offset + 1, info, depth + 1, "set"),
                    0xd => ReadDictionary(offset + 1, info, depth + 1),
                    _ => throw new InvalidDataException(
                        $"The binary property list contains unsupported object type 0x{type:x}.")
                };
            }
            finally
            {
                activeObjects.Remove(objectIndex);
            }
        }

        private PropertyListValue ReadSimple(byte info)
            => info switch
            {
                0x0 => Scalar("null", "null"),
                0x8 => Scalar("boolean", "false"),
                0x9 => Scalar("boolean", "true"),
                _ => throw new InvalidDataException("The binary property list contains an invalid simple value.")
            };

        private PropertyListValue ReadInteger(ulong contentOffset, byte info)
        {
            var byteCount = 1 << info;
            var bytes = ReadBytes(contentOffset, byteCount);
            var integer = new BigInteger(bytes, isUnsigned: false, isBigEndian: true);
            return Scalar("integer", integer.ToString(CultureInfo.InvariantCulture));
        }

        private PropertyListValue ReadReal(ulong contentOffset, byte info)
        {
            var byteCount = 1 << info;
            var bytes = ReadBytes(contentOffset, byteCount);
            var value = byteCount switch
            {
                4 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
                8 => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
                _ => throw new InvalidDataException("The binary property list contains an unsupported real value.")
            };
            return Scalar("real", value.ToString("R", CultureInfo.InvariantCulture));
        }

        private PropertyListValue ReadDate(ulong contentOffset, byte info)
        {
            if (info != 0x3)
            {
                throw new InvalidDataException("The binary property list contains an invalid date value.");
            }
            var seconds = BitConverter.Int64BitsToDouble(
                BinaryPrimitives.ReadInt64BigEndian(ReadBytes(contentOffset, 8)));
            return Scalar(
                "date",
                AppleReferenceDate.AddSeconds(seconds).ToString("O", CultureInfo.InvariantCulture));
        }

        private PropertyListValue ReadData(ulong contentOffset, byte info)
        {
            var length = ReadLength(contentOffset, info, out var valueOffset);
            return Scalar("data", Convert.ToBase64String(ReadBytes(valueOffset, length)));
        }

        private PropertyListValue ReadAsciiString(ulong contentOffset, byte info)
        {
            var length = ReadLength(contentOffset, info, out var valueOffset);
            return Scalar("string", Encoding.ASCII.GetString(ReadBytes(valueOffset, length)));
        }

        private PropertyListValue ReadUtf16String(ulong contentOffset, byte info)
        {
            var characterCount = ReadLength(contentOffset, info, out var valueOffset);
            var byteCount = checked(characterCount * 2);
            return Scalar("string", Encoding.BigEndianUnicode.GetString(ReadBytes(valueOffset, byteCount)));
        }

        private PropertyListValue ReadUid(ulong contentOffset, byte info)
        {
            var byteCount = info + 1;
            return Scalar(
                "uid",
                ReadUnsigned(ReadBytes(contentOffset, byteCount)).ToString(CultureInfo.InvariantCulture));
        }

        private PropertyListValue ReadCollection(
            ulong contentOffset,
            byte info,
            int depth,
            string kind)
        {
            var count = ReadLength(contentOffset, info, out var referencesOffset);
            var entries = new List<PropertyListEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var objectIndex = ReadUnsigned(ReadBytes(
                    referencesOffset + checked((ulong)(index * objectReferenceSize)),
                    objectReferenceSize));
                entries.Add(new PropertyListEntry(
                    $"[{index}]",
                    ReadObject(objectIndex, depth)));
            }
            return new PropertyListValue(kind, null, entries);
        }

        private PropertyListValue ReadDictionary(ulong contentOffset, byte info, int depth)
        {
            var count = ReadLength(contentOffset, info, out var referencesOffset);
            var valuesOffset = referencesOffset + checked((ulong)(count * objectReferenceSize));
            var entries = new List<PropertyListEntry>(count);
            for (var index = 0; index < count; index++)
            {
                var keyIndex = ReadUnsigned(ReadBytes(
                    referencesOffset + checked((ulong)(index * objectReferenceSize)),
                    objectReferenceSize));
                var valueIndex = ReadUnsigned(ReadBytes(
                    valuesOffset + checked((ulong)(index * objectReferenceSize)),
                    objectReferenceSize));
                var key = ReadObject(keyIndex, depth);
                if (key.Kind != "string")
                {
                    throw new InvalidDataException("A binary property-list dictionary key is not a string.");
                }
                entries.Add(new PropertyListEntry(
                    key.Value ?? string.Empty,
                    ReadObject(valueIndex, depth)));
            }
            return new PropertyListValue("dictionary", null, entries);
        }

        private int ReadLength(ulong contentOffset, byte info, out ulong valueOffset)
        {
            if (info < 0x0f)
            {
                valueOffset = contentOffset;
                return info;
            }

            var marker = ReadByte(contentOffset);
            if ((marker >> 4) != 0x1)
            {
                throw new InvalidDataException("A binary property-list length is not encoded as an integer.");
            }
            var byteCount = 1 << (marker & 0x0f);
            if (byteCount > 8)
            {
                throw new InvalidDataException("A binary property-list object is too large to preview.");
            }
            var length = ReadUnsigned(ReadBytes(contentOffset + 1, byteCount));
            if (length > int.MaxValue)
            {
                throw new InvalidDataException("A binary property-list object is too large to preview.");
            }
            valueOffset = contentOffset + 1 + (ulong)byteCount;
            return (int)length;
        }

        private ulong ReadOffset(ulong objectIndex)
            => ReadUnsigned(ReadBytes(
                offsetTableOffset + checked(objectIndex * (ulong)offsetSize),
                offsetSize));

        private byte ReadByte(ulong offset)
        {
            if (offset >= (ulong)data.Length)
            {
                throw new InvalidDataException("The binary property list contains an out-of-range offset.");
            }
            return data.Span[(int)offset];
        }

        private ReadOnlySpan<byte> ReadBytes(ulong offset, int count)
        {
            if (count < 0 || offset > (ulong)data.Length || (ulong)count > (ulong)data.Length - offset)
            {
                throw new InvalidDataException("The binary property list contains out-of-range data.");
            }
            return data.Span.Slice((int)offset, count);
        }

        private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
        {
            if (bytes.Length is < 1 or > 8)
            {
                throw new InvalidDataException("The binary property list contains an invalid integer width.");
            }
            ulong value = 0;
            foreach (var item in bytes)
            {
                value = (value << 8) | item;
            }
            return value;
        }
    }
}
