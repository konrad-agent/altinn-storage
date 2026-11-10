using System;
using System.Globalization;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Graphics.Operations.General;
using UglyToad.PdfPig.Graphics.Operations.SpecialGraphicsState;
using UglyToad.PdfPig.Writer;

namespace Altinn.Platform.Storage.Helpers;

/// <summary>
/// Combines the PDFs generated for each print view of an Altinn 2 form into one document, and
/// draws the archive watermark and page number on every page.
/// </summary>
/// <remarks>
/// Each page gets a red box with the archive timestamp and reference near the top of the right edge,
/// with text reading upwards, and one near the bottom of the left edge, with text reading downwards,
/// plus the page number in the bottom right corner. Measurements are in points (1/72 inch).
/// Text is set in the standard Helvetica font, which PDF readers provide, so no font is embedded.
/// </remarks>
internal sealed class A2PdfBuilder : IDisposable
{
    /// <summary>
    /// Length of the watermark box along the page edge.
    /// </summary>
    public const double BoxLength = 155;

    /// <summary>
    /// Thickness of the watermark box.
    /// </summary>
    public const double BoxThickness = 15;

    /// <summary>
    /// Distance from the page edge to the side of the box facing the page content.
    /// </summary>
    public const double EdgeMargin = 18;

    /// <summary>
    /// Distance from the top (right box) or bottom (left box) of the page to the end of the box.
    /// </summary>
    public const double EndMargin = 5;

    private const double _fontSize = 9;
    private const double _textPadding = 1;
    private const double _helveticaAscent = 0.718;

    private readonly PdfDocumentBuilder _builder = new();
    private readonly PdfDocumentBuilder.AddedFont _font;
    private readonly string _watermark;
    private readonly double _watermarkFontSize;
    private readonly double _watermarkWidth;

    /// <summary>
    /// Initializes a new instance of the <see cref="A2PdfBuilder"/> class.
    /// </summary>
    /// <param name="watermark">The text inside the watermark boxes.</param>
    public A2PdfBuilder(string watermark)
    {
        _watermark = watermark;
        _font = _builder.AddStandard14Font(Standard14Font.Helvetica);

        // Shrink long text to fit rather than letting it run outside the box.
        _watermarkFontSize = _fontSize;
        _watermarkWidth = MeasureText(watermark, _fontSize);
        double available = BoxLength - (2 * _textPadding);
        if (_watermarkWidth > available)
        {
            _watermarkFontSize *= available / _watermarkWidth;
            _watermarkWidth = available;
        }
    }

    /// <summary>
    /// The number of pages added so far.
    /// </summary>
    public int PageCount { get; private set; }

    /// <summary>
    /// Appends all pages of a PDF document and stamps them.
    /// </summary>
    public void AddDocument(byte[] pdf)
    {
        using PdfDocument source = PdfDocument.Open(pdf);
        for (int pageNumber = 1; pageNumber <= source.NumberOfPages; pageNumber++)
        {
            Page sourcePage = source.GetPage(pageNumber);
            if (sourcePage.Rotation.Value % 360 != 0)
            {
                throw new NotSupportedException("Watermarks on rotated pages are not supported.");
            }

            PdfPageBuilder page = _builder.AddPage(source, pageNumber);
            PageCount++;

            // The source content may leave its graphics state changed (Chromium ends with its
            // coordinate transform still applied), so it is wrapped in q/Q and the stamp starts
            // from default user space.
            page.NewContentStreamBefore();
            page.CurrentStream.Operations.Add(Push.Value);
            page.NewContentStreamAfter();
            page.CurrentStream.Operations.Add(Pop.Value);

            // PdfPageBuilder does not report the size of a copied page, so it is taken from the source.
            PdfRectangle box = sourcePage.MediaBox.Bounds;
            DrawWatermark(page, box.Right - EdgeMargin, box.Top - EndMargin - BoxLength, 90);
            DrawWatermark(page, box.Left + EdgeMargin, box.Bottom + EndMargin + BoxLength, -90);
            page.ResetColor();
            page.AddText(
                PageCount.ToString(CultureInfo.InvariantCulture),
                _fontSize,
                new PdfPoint(box.Right - 23, box.Bottom + 5),
                _font
            );
        }
    }

    /// <summary>
    /// Writes the combined document.
    /// </summary>
    public byte[] Build()
    {
        if (PageCount == 0)
        {
            throw new InvalidOperationException("A PDF document needs at least one page.");
        }

        return _builder.Build();
    }

    /// <inheritdoc/>
    public void Dispose() => _builder.Dispose();

    /// <summary>
    /// Width of text set in the watermark font, in points.
    /// </summary>
    public static double MeasureText(string text, double fontSize)
    {
        using var scratch = new PdfDocumentBuilder();
        var font = scratch.AddStandard14Font(Standard14Font.Helvetica);
        var letters = scratch.AddPage(1, 1).MeasureText(text, fontSize, new PdfPoint(0, 0), font);
        return letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
    }

    /// <summary>
    /// Draws one watermark box, anchored at the corner where its text starts, on the side facing
    /// the content.
    /// </summary>
    private void DrawWatermark(PdfPageBuilder page, double x, double y, double degrees)
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        double fontSize = _watermarkFontSize;
        // Box space: x runs along the text from the anchor; y = 0 is the side facing the content and
        // the box extends to y = -BoxThickness towards the page edge. The baseline sits 3/8 of the
        // ascent below the centre line, which centres digits and capitals in the box.
        double baseline = -((BoxThickness / 2) + (_helveticaAscent * fontSize * 3 / 8));

        var operations = page.CurrentStream.Operations;
        operations.Add(Push.Value);
        operations.Add(new ModifyCurrentTransformationMatrix([cos, sin, -sin, cos, x, y]));
        page.SetStrokeColor(255, 0, 0);
        page.DrawRectangle(new PdfPoint(0, -BoxThickness), BoxLength, BoxThickness, 1);
        page.SetTextAndFillColor(255, 0, 0);
        page.AddText(
            _watermark,
            fontSize,
            new PdfPoint((BoxLength - _watermarkWidth) / 2, baseline),
            _font
        );
        operations.Add(Pop.Value);
    }
}
