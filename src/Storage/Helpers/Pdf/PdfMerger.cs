using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace Altinn.Platform.Storage.Helpers.Pdf;

/// <summary>
/// Combines the pages of several PDF documents into one new document and can draw an overlay on
/// top of every page.
/// </summary>
/// <remarks>
/// Pages are copied with the objects they reference, renumbered into the new document. Document-level
/// structures that refer to the whole source document (outlines, structure tree, named destinations,
/// forms) are not carried over. The overlay is drawn with the standard Helvetica font, which PDF
/// readers provide, so no font data is embedded.
/// </remarks>
internal sealed class PdfMerger
{
    private const string _overlayFontName = "AltinnOverlayHelvetica";

    private static readonly HashSet<string> _pageKeysNotCopied =
    [
        "Type",
        "Parent",
        "Contents",
        "Resources",
        "MediaBox",
        "CropBox",
        "Rotate",
        "StructParents",
        "B",
    ];

    private readonly List<PdfObject?> _objects = [null];
    private readonly List<PdfReference> _pages = [];
    private readonly PdfReference _catalog;
    private readonly PdfReference _pageTree;
    private readonly PdfReference _overlayFont;
    private readonly PdfReference _saveGraphicsState;
    private readonly Func<PdfOverlayPage, string>? _overlay;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfMerger"/> class.
    /// </summary>
    /// <param name="overlay">
    /// Optional content stream operators to draw on top of each page, in default user space. Text
    /// operators can use <see cref="PdfOverlayPage.FontResourceName"/> to select Helvetica.
    /// </param>
    public PdfMerger(Func<PdfOverlayPage, string>? overlay = null)
    {
        _overlay = overlay;
        _catalog = Allocate();
        _pageTree = Allocate();
        _overlayFont = Allocate();
        _saveGraphicsState = Allocate();

        var catalog = new PdfDictionary();
        catalog.Set("Type", new PdfName("Catalog"));
        catalog.Set("Pages", _pageTree);
        _objects[_catalog.ObjectNumber] = catalog;

        var font = new PdfDictionary();
        font.Set("Type", new PdfName("Font"));
        font.Set("Subtype", new PdfName("Type1"));
        font.Set("BaseFont", new PdfName("Helvetica"));
        font.Set("Encoding", new PdfName("WinAnsiEncoding"));
        _objects[_overlayFont.ObjectNumber] = font;

        _objects[_saveGraphicsState.ObjectNumber] = new PdfStream(
            new PdfDictionary(),
            "q\n"u8.ToArray()
        );
    }

    /// <summary>
    /// The number of pages added so far.
    /// </summary>
    public int PageCount => _pages.Count;

    /// <summary>
    /// Appends all pages of a PDF document.
    /// </summary>
    /// <param name="pdf">The PDF file.</param>
    /// <param name="orientation">
    /// When set, a page whose media box has the other orientation gets its width and height swapped.
    /// </param>
    public void AddDocument(ReadOnlyMemory<byte> pdf, PdfPageOrientation? orientation = null)
    {
        var source = new PdfDocumentReader(pdf);
        var copier = new ObjectCopier(this, source);
        List<PdfPageSource> pages = source.GetPages();

        // Pages are numbered before anything is copied, so that links between pages of the
        // same document point at the copies instead of pulling in the source pages again.
        var targets = pages.Select(page => copier.MapPage(page.ObjectNumber)).ToList();

        for (int i = 0; i < pages.Count; i++)
        {
            _objects[targets[i].ObjectNumber] = CreatePage(pages[i], copier, source, orientation);
            _pages.Add(targets[i]);
            copier.CopyPending();
        }
    }

    /// <summary>
    /// Writes the combined document.
    /// </summary>
    public byte[] Save()
    {
        if (_pages.Count == 0)
        {
            throw new InvalidOperationException("A PDF document needs at least one page.");
        }

        var pageTree = new PdfDictionary();
        pageTree.Set("Type", new PdfName("Pages"));
        pageTree.Set("Kids", new PdfArray(_pages));
        pageTree.Set("Count", PdfNumber.FromInt(_pages.Count));
        _objects[_pageTree.ObjectNumber] = pageTree;

        using var stream = new MemoryStream();
        var output = new PdfOutput(stream);
        output.WriteRaw("%PDF-1.7\n%"u8);
        output.WriteRaw([0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        long[] offsets = new long[_objects.Count];
        for (int number = 1; number < _objects.Count; number++)
        {
            offsets[number] = output.Position;
            output.WriteRaw(string.Create(CultureInfo.InvariantCulture, $"{number} 0 obj\n"));
            (_objects[number] ?? PdfNull.Instance).WriteTo(output);
            output.WriteRaw("\nendobj\n"u8);
        }

        long xrefOffset = output.Position;
        var xref = new StringBuilder();
        xref.Append(
            CultureInfo.InvariantCulture,
            $"xref\n0 {_objects.Count}\n0000000000 65535 f\r\n"
        );
        for (int number = 1; number < _objects.Count; number++)
        {
            xref.Append(CultureInfo.InvariantCulture, $"{offsets[number]:D10} 00000 n\r\n");
        }

        output.WriteRaw(xref.ToString());
        var trailer = new PdfDictionary();
        trailer.Set("Size", PdfNumber.FromInt(_objects.Count));
        trailer.Set("Root", _catalog);
        output.WriteRaw("trailer\n"u8);
        trailer.WriteTo(output);
        output.WriteRaw(
            string.Create(CultureInfo.InvariantCulture, $"\nstartxref\n{xrefOffset}\n%%EOF\n")
        );
        return stream.ToArray();
    }

    private PdfReference Allocate()
    {
        _objects.Add(null);
        return new PdfReference(_objects.Count - 1, 0);
    }

    private PdfDictionary CreatePage(
        PdfPageSource source,
        ObjectCopier copier,
        PdfDocumentReader reader,
        PdfPageOrientation? orientation
    )
    {
        var page = new PdfDictionary();
        page.Set("Type", new PdfName("Page"));
        page.Set("Parent", _pageTree);
        foreach (var entry in source.Dictionary.Entries)
        {
            if (!_pageKeysNotCopied.Contains(entry.Key.Value))
            {
                page.Set(entry.Key, copier.Copy(entry.Value));
            }
        }

        PdfRectangle mediaBox = PdfRectangle.FromArray(reader, source.Inherited["MediaBox"]);
        if (
            orientation is PdfPageOrientation wanted
            && mediaBox.Orientation != wanted
            && mediaBox.Width != mediaBox.Height
        )
        {
            mediaBox = new PdfRectangle(
                mediaBox.Left,
                mediaBox.Bottom,
                mediaBox.Left + mediaBox.Height,
                mediaBox.Bottom + mediaBox.Width
            );
        }

        page.Set("MediaBox", mediaBox.ToArray());
        if (source.Inherited["CropBox"] is { } cropBox)
        {
            page.Set("CropBox", copier.Copy(cropBox));
        }

        long rotate = (reader.Resolve(source.Inherited["Rotate"]) as PdfNumber)?.AsLong() ?? 0;
        if (rotate % 360 != 0)
        {
            if (_overlay is not null)
            {
                throw new PdfFormatException("Overlays on rotated pages are not supported.");
            }

            page.Set("Rotate", PdfNumber.FromInt(rotate));
        }

        var sourceResources = reader.Resolve(source.Inherited["Resources"]) as PdfDictionary;
        var resources = copier.CopyDirect(sourceResources);
        var contents = new PdfArray(
            GetContentStreams(reader, source.Dictionary["Contents"]).Select(copier.Copy)
        );

        if (_overlay is not null)
        {
            var fonts = copier.CopyDirect(sourceResources?["Font"]);
            string fontResourceName = _overlayFontName;
            for (int suffix = 1; fonts[fontResourceName] is not null; suffix++)
            {
                fontResourceName = _overlayFontName + suffix.ToString(CultureInfo.InvariantCulture);
            }

            fonts.Set(fontResourceName, _overlayFont);
            resources.Set("Font", fonts);

            // The source content may leave its graphics state changed (Chromium, for one, ends
            // with its coordinate transform still applied), so it is wrapped in q/Q and the overlay
            // starts from default user space.
            string overlay = _overlay(
                new PdfOverlayPage(_pages.Count + 1, mediaBox, fontResourceName)
            );
            PdfReference overlayStream = Allocate();
            _objects[overlayStream.ObjectNumber] = CreateFlateStream("Q\n" + overlay);
            contents.Items.Insert(0, _saveGraphicsState);
            contents.Items.Add(overlayStream);
        }

        page.Set("Resources", resources);
        page.Set("Contents", contents);
        return page;
    }

    private static IEnumerable<PdfObject> GetContentStreams(
        PdfDocumentReader reader,
        PdfObject? contents
    )
    {
        return contents switch
        {
            null => [],
            PdfReference reference when reader.Resolve(reference) is PdfArray array => array.Items,
            PdfReference reference => [reference],
            PdfArray array => array.Items,
            _ => throw new PdfFormatException("Invalid page contents."),
        };
    }

    private static PdfStream CreateFlateStream(string content)
    {
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(Encoding.ASCII.GetBytes(content));
        }

        var dictionary = new PdfDictionary();
        dictionary.Set("Filter", new PdfName("FlateDecode"));
        return new PdfStream(dictionary, compressed.ToArray());
    }

    /// <summary>
    /// Copies objects from one source document into the merger, giving each indirect object a new
    /// number once.
    /// </summary>
    private sealed class ObjectCopier(PdfMerger merger, PdfDocumentReader source)
    {
        private const int _maxDepth = 64;

        private readonly Dictionary<int, PdfReference> _mapped = [];
        private readonly Queue<(int Source, PdfReference Target)> _pending = new();

        public PdfReference MapPage(int sourceObjectNumber)
        {
            PdfReference target = merger.Allocate();
            _mapped[sourceObjectNumber] = target;
            return target;
        }

        public PdfObject Copy(PdfObject obj) => Copy(obj, 0);

        /// <summary>
        /// Copies a dictionary of the source document so that it can be changed without affecting
        /// other pages, resolving it first when it is an indirect object. A missing value gives an
        /// empty dictionary.
        /// </summary>
        public PdfDictionary CopyDirect(PdfObject? obj) =>
            source.Resolve(obj) switch
            {
                PdfNull => new PdfDictionary(),
                PdfDictionary dictionary => CopyDictionary(dictionary, 0),
                _ => throw new PdfFormatException("Expected a dictionary."),
            };

        public void CopyPending()
        {
            while (_pending.TryDequeue(out var item))
            {
                merger._objects[item.Target.ObjectNumber] = Copy(source.GetObject(item.Source), 0);
            }
        }

        private PdfObject Copy(PdfObject obj, int depth)
        {
            if (depth > _maxDepth)
            {
                throw new PdfFormatException("Objects are nested too deeply.");
            }

            switch (obj)
            {
                case PdfReference reference:
                    return MapReference(reference);
                case PdfArray array:
                    return new PdfArray(array.Items.Select(item => Copy(item, depth + 1)));
                case PdfStream stream:
                    return new PdfStream(
                        CopyDictionary(stream.Dictionary, depth, "Length"),
                        stream.Data
                    );
                case PdfDictionary dictionary:
                    return CopyDictionary(dictionary, depth);
                default:
                    return obj;
            }
        }

        private PdfDictionary CopyDictionary(
            PdfDictionary dictionary,
            int depth,
            string? skipKey = null
        )
        {
            var copy = new PdfDictionary();
            foreach (var entry in dictionary.Entries)
            {
                if (entry.Key.Value == skipKey)
                {
                    continue;
                }

                copy.Set(entry.Key, Copy(entry.Value, depth + 1));
            }

            return copy;
        }

        private PdfObject MapReference(PdfReference reference)
        {
            if (_mapped.TryGetValue(reference.ObjectNumber, out PdfReference? target))
            {
                return target;
            }

            // References into the document structure (catalog, page tree, unrelated pages) would
            // drag the whole source document along, so they are dropped.
            PdfObject resolved = source.GetObject(reference.ObjectNumber);
            if (
                resolved is PdfNull
                || (
                    resolved is PdfDictionary d
                    && (d.IsType("Catalog") || d.IsType("Pages") || d.IsType("Page"))
                )
            )
            {
                return PdfNull.Instance;
            }

            target = merger.Allocate();
            _mapped[reference.ObjectNumber] = target;
            _pending.Enqueue((reference.ObjectNumber, target));
            return target;
        }
    }
}

/// <summary>
/// Page orientation.
/// </summary>
internal enum PdfPageOrientation
{
    /// <summary>
    /// Height greater than or equal to width.
    /// </summary>
    Portrait,

    /// <summary>
    /// Width greater than height.
    /// </summary>
    Landscape,
}

/// <summary>
/// A page the overlay is drawn on.
/// </summary>
/// <param name="PageNumber">The 1-based page number in the combined document.</param>
/// <param name="MediaBox">The page boundaries in default user space.</param>
/// <param name="FontResourceName">The resource name that selects Helvetica in text operators.</param>
internal sealed record PdfOverlayPage(
    int PageNumber,
    PdfRectangle MediaBox,
    string FontResourceName
);

/// <summary>
/// A rectangle in PDF user space, where y grows upwards.
/// </summary>
internal readonly record struct PdfRectangle(double Left, double Bottom, double Right, double Top)
{
    /// <summary>
    /// The width.
    /// </summary>
    public double Width => Right - Left;

    /// <summary>
    /// The height.
    /// </summary>
    public double Height => Top - Bottom;

    /// <summary>
    /// The orientation. A square counts as portrait.
    /// </summary>
    public PdfPageOrientation Orientation =>
        Width > Height ? PdfPageOrientation.Landscape : PdfPageOrientation.Portrait;

    /// <summary>
    /// Reads a rectangle array, normalizing the corner order.
    /// </summary>
    public static PdfRectangle FromArray(PdfDocumentReader reader, PdfObject? obj)
    {
        if (reader.Resolve(obj) is not PdfArray { Items.Count: 4 } array)
        {
            throw new PdfFormatException("Invalid or missing page boundaries.");
        }

        double[] v = array
            .Items.Select(item =>
                reader.Resolve(item) as PdfNumber
                ?? throw new PdfFormatException("Invalid rectangle.")
            )
            .Select(n => n.AsDouble())
            .ToArray();
        return new PdfRectangle(
            Math.Min(v[0], v[2]),
            Math.Min(v[1], v[3]),
            Math.Max(v[0], v[2]),
            Math.Max(v[1], v[3])
        );
    }

    /// <summary>
    /// The rectangle as a PDF array.
    /// </summary>
    public PdfArray ToArray() =>
        new([
            PdfNumber.FromDouble(Left),
            PdfNumber.FromDouble(Bottom),
            PdfNumber.FromDouble(Right),
            PdfNumber.FromDouble(Top),
        ]);
}
