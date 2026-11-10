using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Altinn.Platform.Storage.Helpers.Pdf;

/// <summary>
/// Reads the objects of an existing PDF file through its cross-reference data.
/// Supports classic cross-reference tables, cross-reference streams and object streams
/// (ISO 32000-1, sections 7.5.4 to 7.5.8). Encrypted files are not supported.
/// </summary>
internal sealed class PdfDocumentReader
{
    private const int _maxPageTreeDepth = 32;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly Dictionary<int, XrefEntry> _xref = [];
    private readonly Dictionary<int, PdfObject> _cache = [];
    private readonly Dictionary<int, (PdfLexer Lexer, int[] Offsets)> _objectStreams = [];
    private readonly HashSet<int> _resolving = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfDocumentReader"/> class.
    /// </summary>
    public PdfDocumentReader(ReadOnlyMemory<byte> data)
    {
        _data = data;
        if (!data.Span.StartsWith("%PDF-"u8))
        {
            throw new PdfFormatException("Missing PDF header.");
        }

        Trailer = ReadXrefChain(FindStartXref());
        if (Trailer["Encrypt"] is not null)
        {
            throw new PdfFormatException("Encrypted PDF files are not supported.");
        }
    }

    /// <summary>
    /// The trailer of the newest cross-reference section.
    /// </summary>
    public PdfDictionary Trailer { get; }

    /// <summary>
    /// The document catalog.
    /// </summary>
    public PdfDictionary Catalog =>
        Resolve(Trailer["Root"]) as PdfDictionary
        ?? throw new PdfFormatException("Missing document catalog.");

    /// <summary>
    /// Resolves an indirect reference. Other objects are returned as they are.
    /// </summary>
    public PdfObject Resolve(PdfObject? obj) =>
        obj switch
        {
            null => PdfNull.Instance,
            PdfReference reference => GetObject(reference.ObjectNumber),
            _ => obj,
        };

    /// <summary>
    /// Gets an indirect object by number. Missing and free objects are <c>null</c>.
    /// </summary>
    public PdfObject GetObject(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out PdfObject? cached))
        {
            return cached;
        }

        if (!_xref.TryGetValue(objectNumber, out XrefEntry entry) || entry.IsFree)
        {
            return PdfNull.Instance;
        }

        if (!_resolving.Add(objectNumber))
        {
            throw new PdfFormatException($"Object {objectNumber} refers to itself.");
        }

        try
        {
            PdfObject obj = entry.InObjectStream
                ? ReadFromObjectStream(entry.Location, entry.Index)
                : ReadIndirectObject(entry.Location, objectNumber);
            _cache[objectNumber] = obj;
            return obj;
        }
        finally
        {
            _resolving.Remove(objectNumber);
        }
    }

    /// <summary>
    /// Lists the pages in document order, with the inheritable attributes from the page tree applied
    /// (ISO 32000-1, section 7.7.3.4).
    /// </summary>
    public List<PdfPageSource> GetPages()
    {
        var pages = new List<PdfPageSource>();
        var visited = new HashSet<int>();
        PdfObject root = Catalog["Pages"] ?? throw new PdfFormatException("Missing page tree.");
        CollectPages(root, new PdfDictionary(), pages, visited, 0);
        return pages;
    }

    /// <summary>
    /// Decodes stream data. Only the filters needed for cross-reference and object streams are supported.
    /// </summary>
    public byte[] Decode(PdfStream stream)
    {
        PdfObject filter = Resolve(stream.Dictionary["Filter"]);
        PdfObject parameters = Resolve(stream.Dictionary["DecodeParms"]);
        if (filter is PdfArray { Items.Count: 1 } filters)
        {
            filter = Resolve(filters.Items[0]);
            parameters =
                parameters is PdfArray p && p.Items.Count == 1 ? Resolve(p.Items[0]) : parameters;
        }

        return filter switch
        {
            PdfNull => stream.Data.ToArray(),
            PdfName { Value: "FlateDecode" } => ApplyPredictor(
                Inflate(stream.Data),
                parameters as PdfDictionary
            ),
            _ => throw new PdfFormatException($"Unsupported stream filter {filter}."),
        };
    }

    private void CollectPages(
        PdfObject node,
        PdfDictionary inherited,
        List<PdfPageSource> pages,
        HashSet<int> visited,
        int depth
    )
    {
        if (node is not PdfReference reference || !visited.Add(reference.ObjectNumber))
        {
            throw new PdfFormatException("Page tree nodes must be distinct indirect objects.");
        }

        if (depth > _maxPageTreeDepth || Resolve(reference) is not PdfDictionary dictionary)
        {
            throw new PdfFormatException("Invalid page tree.");
        }

        var attributes = new PdfDictionary();
        foreach (var entry in inherited.Entries)
        {
            attributes.Set(entry.Key, entry.Value);
        }

        foreach (string key in PdfPageSource.InheritableKeys)
        {
            if (dictionary[key] is { } value)
            {
                attributes.Set(key, value);
            }
        }

        if (dictionary.IsType("Pages"))
        {
            var kids =
                Resolve(dictionary["Kids"]) as PdfArray
                ?? throw new PdfFormatException("Pages node without kids.");
            foreach (PdfObject kid in kids.Items)
            {
                CollectPages(kid, attributes, pages, visited, depth + 1);
            }
        }
        else
        {
            pages.Add(new PdfPageSource(reference.ObjectNumber, dictionary, attributes));
        }
    }

    private int FindStartXref()
    {
        ReadOnlySpan<byte> span = _data.Span;
        int searchFrom = Math.Max(0, span.Length - 2048);
        int index = span[searchFrom..].LastIndexOf("startxref"u8);
        if (index < 0)
        {
            throw new PdfFormatException("Missing startxref.");
        }

        var lexer = new PdfLexer(_data, searchFrom + index + "startxref".Length);
        return checked((int)lexer.ReadInteger());
    }

    private PdfDictionary ReadXrefChain(int offset)
    {
        PdfDictionary? newest = null;
        var visited = new HashSet<int>();
        int? next = offset;
        while (next is int current)
        {
            if (!visited.Add(current) || current < 0 || current >= _data.Length)
            {
                throw new PdfFormatException("Invalid cross-reference chain.");
            }

            PdfDictionary trailer = ReadXrefSection(current);
            newest ??= trailer;
            if (trailer["XRefStm"] is PdfNumber hybrid)
            {
                ReadXrefSection(checked((int)hybrid.AsLong()));
            }

            next = trailer["Prev"] is PdfNumber prev ? checked((int)prev.AsLong()) : null;
        }

        return newest!;
    }

    private PdfDictionary ReadXrefSection(int offset)
    {
        var lexer = new PdfLexer(_data, offset);
        if (lexer.PeekToken() == "xref")
        {
            lexer.PeekToken(consume: true);
            return ReadXrefTable(lexer);
        }

        return ReadXrefStream(offset);
    }

    private PdfDictionary ReadXrefTable(PdfLexer lexer)
    {
        while (lexer.PeekToken() != "trailer")
        {
            int first = checked((int)lexer.ReadInteger());
            int count = checked((int)lexer.ReadInteger());
            for (int i = 0; i < count; i++)
            {
                long location = lexer.ReadInteger();
                lexer.ReadInteger();
                string type = lexer.PeekToken(consume: true);
                if (type == "n")
                {
                    _xref.TryAdd(first + i, new XrefEntry(false, checked((int)location), 0));
                }
                else if (type == "f")
                {
                    _xref.TryAdd(first + i, XrefEntry.Free);
                }
                else
                {
                    throw new PdfFormatException($"Invalid cross-reference entry type '{type}'.");
                }
            }
        }

        lexer.ExpectKeyword("trailer");
        return lexer.ReadObject() as PdfDictionary
            ?? throw new PdfFormatException("Invalid trailer.");
    }

    private PdfDictionary ReadXrefStream(int offset)
    {
        if (
            ReadIndirectObject(offset, null) is not PdfStream stream
            || !stream.Dictionary.IsType("XRef")
        )
        {
            throw new PdfFormatException($"No cross-reference data at offset {offset}.");
        }

        int[] widths = ReadIntArray(stream.Dictionary["W"]);
        if (widths.Length != 3 || widths.Any(w => w is < 0 or > 8))
        {
            throw new PdfFormatException("Invalid /W in cross-reference stream.");
        }

        int size = checked((int)((PdfNumber)Resolve(stream.Dictionary["Size"])).AsLong());
        int[] index = stream.Dictionary["Index"] is null
            ? [0, size]
            : ReadIntArray(stream.Dictionary["Index"]);
        byte[] data = Decode(stream);
        int rowLength = widths.Sum();
        int position = 0;
        for (int section = 0; section + 1 < index.Length; section += 2)
        {
            for (int i = 0; i < index[section + 1]; i++)
            {
                if (position + rowLength > data.Length)
                {
                    throw new PdfFormatException("Cross-reference stream is truncated.");
                }

                long type = widths[0] == 0 ? 1 : ReadBigEndian(data, ref position, widths[0]);
                long field2 = ReadBigEndian(data, ref position, widths[1]);
                long field3 = ReadBigEndian(data, ref position, widths[2]);
                int objectNumber = index[section] + i;
                switch (type)
                {
                    case 0:
                        _xref.TryAdd(objectNumber, XrefEntry.Free);
                        break;
                    case 1:
                        _xref.TryAdd(objectNumber, new XrefEntry(false, checked((int)field2), 0));
                        break;
                    case 2:
                        _xref.TryAdd(
                            objectNumber,
                            new XrefEntry(true, checked((int)field2), checked((int)field3))
                        );
                        break;
                }
            }
        }

        return stream.Dictionary;
    }

    private PdfObject ReadIndirectObject(int offset, int? expectedNumber)
    {
        if (offset < 0 || offset >= _data.Length)
        {
            throw new PdfFormatException($"Object offset {offset} is outside the file.");
        }

        var lexer = new PdfLexer(_data, offset);
        long number = lexer.ReadInteger();
        lexer.ReadInteger();
        lexer.ExpectKeyword("obj");
        if (expectedNumber is int expected && number != expected)
        {
            throw new PdfFormatException(
                $"Expected object {expected} at offset {offset}, found {number}."
            );
        }

        PdfObject obj = lexer.ReadObject();
        if (obj is PdfDictionary dictionary && lexer.PeekToken() == "stream")
        {
            lexer.PeekToken(consume: true);
            return ReadStreamData(dictionary, lexer.Position);
        }

        return obj;
    }

    private PdfStream ReadStreamData(PdfDictionary dictionary, int afterKeyword)
    {
        ReadOnlySpan<byte> span = _data.Span;
        int start = afterKeyword;
        if (start < span.Length && span[start] == '\r')
        {
            start++;
        }

        if (start < span.Length && span[start] == '\n')
        {
            start++;
        }

        if (Resolve(dictionary["Length"]) is PdfNumber lengthNumber)
        {
            long length = lengthNumber.AsLong();
            if (length >= 0 && start + length <= span.Length)
            {
                var after = new PdfLexer(_data, (int)(start + length));
                if (after.PeekToken() == "endstream")
                {
                    return new PdfStream(dictionary, _data.Slice(start, (int)length));
                }
            }
        }

        int end = span[start..].IndexOf("endstream"u8);
        if (end < 0)
        {
            throw new PdfFormatException($"Unterminated stream at offset {start}.");
        }

        int stop = start + end;
        if (stop > start && span[stop - 1] == '\n')
        {
            stop--;
        }

        if (stop > start && span[stop - 1] == '\r')
        {
            stop--;
        }

        return new PdfStream(dictionary, _data[start..stop]);
    }

    private PdfObject ReadFromObjectStream(int streamNumber, int index)
    {
        if (!_objectStreams.TryGetValue(streamNumber, out var objectStream))
        {
            if (
                GetObject(streamNumber) is not PdfStream stream
                || !stream.Dictionary.IsType("ObjStm")
            )
            {
                throw new PdfFormatException($"Object {streamNumber} is not an object stream.");
            }

            int count = checked((int)((PdfNumber)Resolve(stream.Dictionary["N"])).AsLong());
            int first = checked((int)((PdfNumber)Resolve(stream.Dictionary["First"])).AsLong());
            byte[] data = Decode(stream);
            var header = new PdfLexer(data);
            int[] offsets = new int[count];
            for (int i = 0; i < count; i++)
            {
                header.ReadInteger();
                offsets[i] = first + checked((int)header.ReadInteger());
            }

            objectStream = (new PdfLexer(data), offsets);
            _objectStreams[streamNumber] = objectStream;
        }

        if (index < 0 || index >= objectStream.Offsets.Length)
        {
            throw new PdfFormatException($"Object stream {streamNumber} has no entry {index}.");
        }

        objectStream.Lexer.Position = objectStream.Offsets[index];
        return objectStream.Lexer.ReadObject();
    }

    private int[] ReadIntArray(PdfObject? obj) =>
        Resolve(obj) is PdfArray array
            ? array.Items.Select(i => checked((int)((PdfNumber)Resolve(i)).AsLong())).ToArray()
            : throw new PdfFormatException("Expected an array of integers.");

    private static long ReadBigEndian(byte[] data, ref int position, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[position++];
        }

        return value;
    }

    private static byte[] Inflate(ReadOnlyMemory<byte> data)
    {
        using var input = new MemoryStream(data.ToArray());
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private byte[] ApplyPredictor(byte[] data, PdfDictionary? parameters)
    {
        long predictor = (Resolve(parameters?["Predictor"]) as PdfNumber)?.AsLong() ?? 1;
        if (predictor == 1)
        {
            return data;
        }

        if (predictor < 10)
        {
            throw new PdfFormatException($"Unsupported predictor {predictor}.");
        }

        int columns = (int)((Resolve(parameters?["Columns"]) as PdfNumber)?.AsLong() ?? 1);
        int colors = (int)((Resolve(parameters?["Colors"]) as PdfNumber)?.AsLong() ?? 1);
        int bits = (int)((Resolve(parameters?["BitsPerComponent"]) as PdfNumber)?.AsLong() ?? 8);
        int bytesPerPixel = Math.Max(1, (colors * bits) / 8);
        int rowLength = ((columns * colors * bits) + 7) / 8;
        var output = new List<byte>(data.Length);
        byte[] previous = new byte[rowLength];
        byte[] row = new byte[rowLength];
        for (int position = 0; position + 1 + rowLength <= data.Length; position += rowLength + 1)
        {
            byte type = data[position];
            for (int i = 0; i < rowLength; i++)
            {
                int raw = data[position + 1 + i];
                int left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                row[i] = type switch
                {
                    0 => (byte)raw,
                    1 => (byte)(raw + left),
                    2 => (byte)(raw + up),
                    3 => (byte)(raw + ((left + up) / 2)),
                    4 => (byte)(raw + Paeth(left, up, upLeft)),
                    _ => throw new PdfFormatException($"Invalid PNG predictor row type {type}."),
                };
            }

            output.AddRange(row);
            (previous, row) = (row, previous);
        }

        return [.. output];
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a
            : pb <= pc ? b
            : c;
    }

    /// <summary>
    /// Where an object is stored: at a byte offset, or as entry <c>Index</c> of an object stream.
    /// </summary>
    /// <remarks>
    /// Sections are read newest first, so the first entry recorded for an object number wins,
    /// including <see cref="Free"/> entries that hide older versions of the object.
    /// </remarks>
    private readonly record struct XrefEntry(bool InObjectStream, int Location, int Index)
    {
        public static readonly XrefEntry Free = new(false, -1, 0);

        public bool IsFree => !InObjectStream && Location < 0;
    }
}

/// <summary>
/// A page from a source document.
/// </summary>
/// <param name="ObjectNumber">The page object's number in the source document.</param>
/// <param name="Dictionary">The page dictionary as stored.</param>
/// <param name="Inherited">The inheritable attributes in effect for the page.</param>
internal sealed record PdfPageSource(
    int ObjectNumber,
    PdfDictionary Dictionary,
    PdfDictionary Inherited
)
{
    /// <summary>
    /// Page attributes that are inherited from ancestor nodes in the page tree.
    /// </summary>
    public static readonly string[] InheritableKeys =
    [
        "Resources",
        "MediaBox",
        "CropBox",
        "Rotate",
    ];
}
