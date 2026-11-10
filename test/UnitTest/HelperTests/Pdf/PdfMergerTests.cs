using System;
using System.IO;
using System.Linq;
using Altinn.Platform.Storage.Helpers;
using Altinn.Platform.Storage.Helpers.Pdf;
using UglyToad.PdfPig.Annotations;
using Xunit;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace Altinn.Platform.Storage.UnitTest.HelperTests.Pdf;

/// <summary>
/// Tests for <see cref="PdfMerger"/>. The output is read back with PdfPig, an independent PDF
/// parser, so that the tests do not only check that the merger agrees with its own reader.
/// </summary>
public class PdfMergerTests
{
    private const string _watermark = "06.11.2026 09:12:54 AR12345678";

    [Fact]
    public void AddDocument_ChromiumOutput_KeepsPagesInOrderWithTheirSizeAndText()
    {
        var merger = new PdfMerger();
        merger.AddDocument(ReadFixture("chromium-portrait.pdf"));
        merger.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PigDocument.Open(merger.Save());

        Assert.Equal(3, result.NumberOfPages);
        Assert.Contains("Portrait page æøå", result.GetPage(1).Text);
        Assert.Contains("End of portrait view", result.GetPage(2).Text);
        Assert.Contains("Landscape page ÆØÅ", result.GetPage(3).Text);
        using var portrait = PigDocument.Open(ReadFixture("chromium-portrait.pdf"));
        using var landscape = PigDocument.Open(ReadFixture("chromium-landscape.pdf"));
        Assert.Equal(Size(portrait, 1), Size(result, 1), new SizeComparer());
        Assert.Equal(Size(landscape, 1), Size(result, 3), new SizeComparer());
    }

    [Fact]
    public void AddDocument_ObjectStreamsAndXrefStream_ReadsTheObjects()
    {
        var merger = new PdfMerger();
        merger.AddDocument(ReadFixture("object-streams-landscape.pdf"));

        using var result = PigDocument.Open(merger.Save());

        Assert.Equal(1, result.NumberOfPages);
        Assert.Contains("Landscape page ÆØÅ", result.GetPage(1).Text);
    }

    [Fact]
    public void AddDocument_PageWithLinks_KeepsTheLinkAnnotations()
    {
        var merger = new PdfMerger();
        merger.AddDocument(ReadFixture("chromium-portrait.pdf"));

        using var result = PigDocument.Open(merger.Save());

        var links = result
            .GetPage(1)
            .GetAnnotations()
            .Where(a => a.Type == AnnotationType.Link)
            .ToList();
        Assert.Equal(2, links.Count);
    }

    [Fact]
    public void Save_Output_CanBeMergedAgain()
    {
        var first = new PdfMerger();
        first.AddDocument(ReadFixture("chromium-portrait.pdf"));
        var second = new PdfMerger();

        second.AddDocument(first.Save());
        second.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PigDocument.Open(second.Save());
        Assert.Equal(3, result.NumberOfPages);
    }

    [Fact]
    public void Save_IsNotMuchLargerThanTheInputs()
    {
        byte[] portrait = ReadFixture("chromium-portrait.pdf");
        byte[] landscape = ReadFixture("chromium-landscape.pdf");
        var merger = new PdfMerger();
        merger.AddDocument(portrait);
        merger.AddDocument(landscape);

        byte[] result = merger.Save();

        Assert.True(result.Length < (portrait.Length + landscape.Length) * 1.1);
    }

    [Fact]
    public void AddDocument_OrientationDiffersFromMediaBox_SwapsWidthAndHeight()
    {
        var merger = new PdfMerger();
        merger.AddDocument(ReadFixture("chromium-landscape.pdf"), PdfPageOrientation.Portrait);

        using var result = PigDocument.Open(merger.Save());

        Assert.Equal((594.96, 841.92), Size(result, 1), new SizeComparer());
    }

    [Fact]
    public void Overlay_A2Watermark_DrawsWatermarksAndRunningPageNumbers()
    {
        var merger = new PdfMerger(page => A2PdfWatermark.CreateOverlay(page, _watermark));
        merger.AddDocument(ReadFixture("chromium-portrait.pdf"));
        merger.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PigDocument.Open(merger.Save());

        for (int pageNumber = 1; pageNumber <= 3; pageNumber++)
        {
            var page = result.GetPage(pageNumber);
            string text = page.Text;
            Assert.Equal(2, CountOccurrences(text, "AR12345678"));

            var pageNumberLetters = page
                .Letters.Where(l => l.Location.X > page.Width - 30 && l.Location.Y < 10)
                .Select(l => l.Value);
            Assert.Equal(pageNumber.ToString(), string.Concat(pageNumberLetters));

            var rightEdge = page
                .Letters.Where(l => l.Location.X > page.Width - A2PdfWatermark.EdgeMargin)
                .ToList();
            Assert.Equal(_watermark.Length, rightEdge.Count);
            Assert.All(
                rightEdge,
                l => Assert.True(l.Location.Y > page.Height - A2PdfWatermark.BoxLength - 10)
            );
        }
    }

    [Fact]
    public void Overlay_SourceLeavesTransformApplied_OverlayIsInDefaultUserSpace()
    {
        // Chromium starts each page with a scaling and flipping transform that it never restores.
        var merger = new PdfMerger(page => A2PdfWatermark.CreateOverlay(page, _watermark));
        merger.AddDocument(ReadFixture("chromium-landscape.pdf"));

        using var result = PigDocument.Open(merger.Save());

        var page = result.GetPage(1);
        var watermarkLetters = page.Letters.Where(l => "AR".Contains(l.Value)).ToList();
        Assert.NotEmpty(watermarkLetters);
        Assert.All(
            watermarkLetters,
            l => Assert.True(l.Location.X < 20 || l.Location.X > page.Width - 20)
        );
    }

    [Fact]
    public void AddDocument_EncryptedFile_Throws()
    {
        var merger = new PdfMerger();

        Assert.Throws<PdfFormatException>(() => merger.AddDocument(ReadFixture("encrypted.pdf")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a pdf")]
    [InlineData("%PDF-1.4\n1 0 obj\n<< /Type /Catalog")]
    public void AddDocument_InvalidFile_Throws(string content)
    {
        var merger = new PdfMerger();

        Assert.Throws<PdfFormatException>(() =>
            merger.AddDocument(System.Text.Encoding.ASCII.GetBytes(content))
        );
    }

    [Fact]
    public void AddDocument_TruncatedFile_Throws()
    {
        byte[] pdf = ReadFixture("chromium-landscape.pdf");
        var merger = new PdfMerger();

        Assert.Throws<PdfFormatException>(() =>
            merger.AddDocument(pdf.AsMemory(0, pdf.Length / 2))
        );
    }

    [Fact]
    public void Save_WithoutPages_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => new PdfMerger().Save());
    }

    private static byte[] ReadFixture(string name)
    {
        string folder = Path.GetDirectoryName(typeof(PdfMergerTests).Assembly.Location)!;
        return File.ReadAllBytes(Path.Combine(folder, "data", "pdf", name));
    }

    private static (double Width, double Height) Size(PigDocument document, int pageNumber)
    {
        var page = document.GetPage(pageNumber);
        return (page.Width, page.Height);
    }

    private static int CountOccurrences(string text, string value) =>
        (text.Length - text.Replace(value, string.Empty).Length) / value.Length;

    private sealed class SizeComparer
        : System.Collections.Generic.IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) =>
            Math.Abs(x.Item1 - y.Item1) < 0.01 && Math.Abs(x.Item2 - y.Item2) < 0.01;

        public int GetHashCode((double, double) obj) => 0;
    }
}
