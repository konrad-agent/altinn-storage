using System.Globalization;
using System.Text.RegularExpressions;
using Altinn.Platform.Storage.Helpers;
using Altinn.Platform.Storage.Helpers.Pdf;
using Xunit;

namespace Altinn.Platform.Storage.UnitTest.HelperTests;

public class A2PdfWatermarkTests
{
    private static readonly PdfOverlayPage _page = new(7, new PdfRectangle(0, 0, 595, 842), "F1");

    [Fact]
    public void MeasureHelvetica_UsesTheStandardFontMetrics()
    {
        // 22 digits (556), 2 dots, 2 colons and 2 spaces (278 each), A (667) and R (722).
        Assert.Equal(
            137.601,
            A2PdfWatermark.MeasureHelvetica("06.11.2026 09:12:54 AR12345678", 9),
            3
        );
    }

    [Fact]
    public void CreateOverlay_DrawsBothBoxesAndThePageNumber()
    {
        string overlay = A2PdfWatermark.CreateOverlay(_page, "06.11.2026 09:12:54 AR1");

        Assert.Equal(2, CountOccurrences(overlay, "(06.11.2026 09:12:54 AR1) Tj"));
        Assert.Equal(2, CountOccurrences(overlay, "0 -15 155 15 re S"));
        Assert.Contains("BT /F1 9 Tf 572 5 Td (7) Tj ET", overlay);
    }

    [Fact]
    public void CreateOverlay_TextWiderThanTheBox_IsScaledDown()
    {
        string text = "11/06/2026 09:12:54 PM AR1234567890123";

        string overlay = A2PdfWatermark.CreateOverlay(_page, text);

        var match = Regex.Match(overlay, @"/F1 ([0-9.]+) Tf [-0-9. ]+ Td \(11/06");
        Assert.True(match.Success);
        double fontSize = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.InRange(fontSize, 1, 8.99);
        Assert.InRange(
            A2PdfWatermark.MeasureHelvetica(text, fontSize),
            A2PdfWatermark.BoxLength - 3,
            A2PdfWatermark.BoxLength
        );
    }

    [Fact]
    public void CreateOverlay_EscapesStringDelimiters()
    {
        string overlay = A2PdfWatermark.CreateOverlay(_page, "a(b)\\c");

        Assert.Contains("(a\\(b\\)\\\\c) Tj", overlay);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty).Length) / value.Length;
}
