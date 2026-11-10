using System;
using System.IO;
using System.Linq;
using Altinn.Platform.Storage.Helpers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Annotations;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using Xunit;

namespace Altinn.Platform.Storage.UnitTest.HelperTests;

public class A2PdfBuilderTests
{
    private const string _watermark = "06.11.2026 09:12:54 AR12345678";

    [Fact]
    public void Build_ChromiumOutput_KeepsPagesInOrderWithTheirSizeAndText()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("chromium-portrait.pdf"));
        builder.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        Assert.Equal(3, result.NumberOfPages);
        Assert.Contains("Portrait page æøå", result.GetPage(1).Text);
        Assert.Contains("End of portrait view", result.GetPage(2).Text);
        Assert.Contains("Landscape page ÆØÅ", result.GetPage(3).Text);
        using var portrait = PdfDocument.Open(ReadFixture("chromium-portrait.pdf"));
        using var landscape = PdfDocument.Open(ReadFixture("chromium-landscape.pdf"));
        AssertSameSize(portrait.GetPage(1), result.GetPage(1));
        AssertSameSize(landscape.GetPage(1), result.GetPage(3));
    }

    [Fact]
    public void Build_ObjectStreamsAndXrefStream_ReadsTheObjects()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("object-streams-landscape.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        Assert.Equal(1, result.NumberOfPages);
        Assert.Contains("Landscape page ÆØÅ", result.GetPage(1).Text);
    }

    [Fact]
    public void Build_PageWithLinks_KeepsExternalLinks()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("chromium-portrait.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        // Links to named destinations are dropped: the destinations belong to the source
        // document's catalog, which is not copied.
        var link = Assert.Single(
            result.GetPage(1).GetAnnotations(),
            a => a.Type == AnnotationType.Link
        );
        Assert.Contains("https://www.altinn.no/", link.AnnotationDictionary.ToString());
    }

    [Fact]
    public void Build_DrawsWatermarksAndRunningPageNumbers()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("chromium-portrait.pdf"));
        builder.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        for (int pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            Page page = result.GetPage(pageNumber);
            Assert.Equal(2, CountOccurrences(page.Text, "AR12345678"));

            var pageNumberLetters = page
                .Letters.Where(l => l.Location.X > page.Width - 30 && l.Location.Y < 10)
                .Select(l => l.Value);
            Assert.Equal(pageNumber.ToString(), string.Concat(pageNumberLetters));

            // Right box: near the top.
            var rightEdge = page
                .Letters.Where(l => IsWatermark(l) && l.Location.X > page.Width / 2)
                .ToList();
            Assert.Equal(_watermark.Length, rightEdge.Count);
            Assert.All(
                rightEdge,
                l =>
                    Assert.InRange(
                        l.Location.Y,
                        page.Height - A2PdfBuilder.BoxLength - 10,
                        page.Height
                    )
            );

            // Left box: near the bottom.
            var leftEdge = page
                .Letters.Where(l => IsWatermark(l) && l.Location.X < page.Width / 2)
                .ToList();
            Assert.Equal(_watermark.Length, leftEdge.Count);
            Assert.All(leftEdge, l => Assert.InRange(l.Location.Y, 0, A2PdfBuilder.BoxLength + 10));
        }
    }

    [Fact]
    public void Build_WatermarkIsRed()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        Page page = result.GetPage(1);
        var watermarkLetters = page.Letters.Where(l =>
            IsWatermark(l) && l.Location.X < page.Width / 2
        );
        Assert.All(watermarkLetters, l => Assert.Equal((1d, 0d, 0d), l.Color.ToRGBValues()));
    }

    [Fact]
    public void Build_TextWiderThanTheBox_IsScaledDownToFit()
    {
        const string longWatermark = "11/06/2026 09:12:54 PM AR1234567890123";
        using var builder = new A2PdfBuilder(longWatermark);
        builder.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        Page page = result.GetPage(1);
        var left = page
            .Letters.Where(l => IsWatermark(l) && l.Location.X < page.Width / 2)
            .ToList();
        double length = Math.Abs(left[^1].EndBaseLine.Y - left[0].StartBaseLine.Y);
        Assert.InRange(length, A2PdfBuilder.BoxLength - 3, A2PdfBuilder.BoxLength);
        Assert.True(A2PdfBuilder.MeasureText(longWatermark, 9) > A2PdfBuilder.BoxLength);
    }

    [Fact]
    public void MeasureText_UsesTheHelveticaMetrics()
    {
        // 22 digits (556), 2 dots, 2 colons and 2 spaces (278 each), A (667) and R (722).
        Assert.Equal(137.601, A2PdfBuilder.MeasureText(_watermark, 9), 3);
    }

    [Fact]
    public void Build_EncryptedWithEmptyUserPassword_WritesDecryptedPages()
    {
        using var builder = new A2PdfBuilder(_watermark);
        builder.AddDocument(ReadFixture("encrypted.pdf"));

        using var result = PdfDocument.Open(builder.Build());

        Assert.Contains("Landscape page ÆØÅ", result.GetPage(1).Text);
        Assert.False(result.IsEncrypted);
    }

    [Fact]
    public void AddDocument_NotAPdf_Throws()
    {
        using var builder = new A2PdfBuilder(_watermark);

        Assert.ThrowsAny<Exception>(() => builder.AddDocument("not a pdf"u8.ToArray()));
    }

    [Fact]
    public void Build_WithoutPages_Throws()
    {
        using var builder = new A2PdfBuilder(_watermark);

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static byte[] ReadFixture(string name)
    {
        string folder = Path.GetDirectoryName(typeof(A2PdfBuilderTests).Assembly.Location)!;
        return File.ReadAllBytes(Path.Combine(folder, "data", "pdf", name));
    }

    /// <summary>
    /// Watermark text runs along the page edge; the page content is horizontal.
    /// </summary>
    private static bool IsWatermark(Letter letter) =>
        letter.TextOrientation != TextOrientation.Horizontal;

    private static void AssertSameSize(Page expected, Page actual)
    {
        Assert.Equal(expected.Width, actual.Width, 2);
        Assert.Equal(expected.Height, actual.Height, 2);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty).Length) / value.Length;
}
