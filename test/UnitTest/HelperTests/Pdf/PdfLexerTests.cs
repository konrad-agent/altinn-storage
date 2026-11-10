using System.IO;
using System.Text;
using Altinn.Platform.Storage.Helpers.Pdf;
using Xunit;

namespace Altinn.Platform.Storage.UnitTest.HelperTests.Pdf;

public class PdfLexerTests
{
    [Theory]
    [InlineData("<</A 1 0 R/B [1 0 2 0 R 3]>>", "<</A 1 0 R/B[1 0 2 0 R 3]>>")]
    [InlineData(
        "[ (a\\)b(c)) <41 42> /N#20x -1.5 .5 true null ]",
        "[(a\\)b(c))<41 42>/N#20x -1.5 .5 true null]"
    )]
    [InlineData("<< /K % comment\n /V >>", "<</K/V>>")]
    public void ReadObject_WritesBackEquivalentSyntax(string input, string expected)
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        PdfObject obj = lexer.ReadObject();

        Assert.Equal(expected, Write(obj));
    }

    [Fact]
    public void ReadObject_DistinguishesReferencesFromNumbers()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes("[4 0 R 4 0 5]"));

        var array = Assert.IsType<PdfArray>(lexer.ReadObject());

        var reference = Assert.IsType<PdfReference>(array.Items[0]);
        Assert.Equal(4, reference.ObjectNumber);
        Assert.All(array.Items.GetRange(1, 3), item => Assert.IsType<PdfNumber>(item));
    }

    [Theory]
    [InlineData("(unterminated")]
    [InlineData("<</Key")]
    [InlineData("<<1 2>>")]
    [InlineData("endobj")]
    public void ReadObject_InvalidSyntax_Throws(string input)
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        Assert.Throws<PdfFormatException>(() => lexer.ReadObject());
    }

    [Fact]
    public void ReadObject_DeeplyNestedArrays_Throws()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(new string('[', 1000)));

        Assert.Throws<PdfFormatException>(() => lexer.ReadObject());
    }

    private static string Write(PdfObject obj)
    {
        using var stream = new MemoryStream();
        obj.WriteTo(new PdfOutput(stream));
        return Encoding.ASCII.GetString(stream.ToArray());
    }
}
