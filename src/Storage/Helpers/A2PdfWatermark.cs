using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Altinn.Platform.Storage.Helpers.Pdf;

namespace Altinn.Platform.Storage.Helpers;

/// <summary>
/// Draws the archive watermark and page number on pages of a PDF made from an Altinn 2 form.
/// </summary>
/// <remarks>
/// Each page gets a red box with the archive timestamp and reference near the top of the right edge,
/// with text reading upwards, and one near the bottom of the left edge, with text reading downwards,
/// plus the page number in the bottom right corner. Measurements are in points (1/72 inch).
/// </remarks>
internal static class A2PdfWatermark
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
    /// Distance from the page edge to the side of the box nearest the page content.
    /// </summary>
    public const double EdgeMargin = 18;

    /// <summary>
    /// Distance from the top (right box) or bottom (left box) of the page to the end of the box.
    /// </summary>
    public const double EndMargin = 5;

    private const double _fontSize = 9;
    private const double _textPadding = 1;

    /// <summary>
    /// Helvetica glyph widths for character codes 32 to 126, in 1/1000 of the font size (from the
    /// Adobe font metrics for the standard 14 fonts).
    /// </summary>
    private static readonly short[] _helveticaWidths =
    [
        278,
        278,
        355,
        556,
        556,
        889,
        667,
        191,
        333,
        333,
        389,
        584,
        278,
        333,
        278,
        278,
        556,
        556,
        556,
        556,
        556,
        556,
        556,
        556,
        556,
        556,
        278,
        278,
        584,
        584,
        584,
        556,
        1015,
        667,
        667,
        722,
        722,
        667,
        611,
        778,
        722,
        278,
        500,
        667,
        556,
        833,
        722,
        778,
        667,
        778,
        722,
        667,
        611,
        722,
        667,
        944,
        667,
        667,
        611,
        278,
        278,
        278,
        469,
        556,
        333,
        556,
        556,
        500,
        556,
        556,
        278,
        556,
        556,
        222,
        222,
        500,
        222,
        833,
        556,
        556,
        556,
        556,
        333,
        500,
        278,
        556,
        500,
        722,
        500,
        500,
        500,
        334,
        260,
        334,
        584,
    ];

    private const double _helveticaAscent = 718;

    /// <summary>
    /// Width of text set in Helvetica, in points. Characters outside the printable ASCII range
    /// are measured as <c>?</c>, which is how they are written.
    /// </summary>
    public static double MeasureHelvetica(string text, double fontSize) =>
        text.Sum(c => _helveticaWidths[(c is >= ' ' and <= '~' ? c : '?') - ' ']) * fontSize / 1000;

    /// <summary>
    /// Creates the content stream operators for one page.
    /// </summary>
    /// <param name="page">The page.</param>
    /// <param name="watermark">The text inside the watermark boxes.</param>
    public static string CreateOverlay(PdfOverlayPage page, string watermark)
    {
        PdfRectangle box = page.MediaBox;
        var content = new StringBuilder();

        // Each box is anchored at the corner where its text starts, on the side facing the content.
        AppendWatermark(
            content,
            page.FontResourceName,
            watermark,
            box.Right - EdgeMargin,
            box.Top - EndMargin - BoxLength,
            90
        );
        AppendWatermark(
            content,
            page.FontResourceName,
            watermark,
            box.Left + EdgeMargin,
            box.Bottom + EndMargin + BoxLength,
            -90
        );

        content.Append("0 g\n");
        AppendText(
            content,
            page.FontResourceName,
            _fontSize,
            box.Right - 23,
            box.Bottom + 5,
            page.PageNumber.ToString(CultureInfo.InvariantCulture)
        );
        return content.ToString();
    }

    private static void AppendWatermark(
        StringBuilder content,
        string font,
        string watermark,
        double x,
        double y,
        double degrees
    )
    {
        double radians = degrees * Math.PI / 180;
        double cos = Math.Cos(radians);
        double sin = Math.Sin(radians);

        // Shrink long text to fit rather than letting it run outside the box.
        double fontSize = _fontSize;
        double width = MeasureHelvetica(watermark, fontSize);
        double available = BoxLength - (2 * _textPadding);
        if (width > available)
        {
            fontSize *= available / width;
            width = available;
        }

        // Box space: x runs along the text from the anchor; y = 0 is the side facing the content and
        // the box extends to y = -BoxThickness towards the page edge.
        double baseline = -((BoxThickness / 2) + (_helveticaAscent / 1000 * fontSize * 3 / 8));

        content.Append("q\n");
        content.Append(Format(cos, sin, -sin, cos, x, y)).Append(" cm\n");
        content.Append("1 0 0 RG 1 w\n");
        content.Append(Format(0, -BoxThickness, BoxLength, BoxThickness)).Append(" re S\n");
        content.Append("1 0 0 rg\n");
        AppendText(content, font, fontSize, (BoxLength - width) / 2, baseline, watermark);
        content.Append("Q\n");
    }

    private static void AppendText(
        StringBuilder content,
        string font,
        double fontSize,
        double x,
        double y,
        string text
    )
    {
        content
            .Append("BT /")
            .Append(font)
            .Append(' ')
            .Append(PdfOutput.FormatReal(fontSize))
            .Append(" Tf ");
        content.Append(Format(x, y)).Append(" Td ");
        content
            .Append(Encoding.ASCII.GetString(PdfString.FromText(text).Source))
            .Append(" Tj ET\n");
    }

    private static string Format(params double[] values) =>
        string.Join(' ', values.Select(PdfOutput.FormatReal));
}
