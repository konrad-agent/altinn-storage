using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace Altinn.Platform.Storage.Helpers.Pdf;

/// <summary>
/// Writes PDF syntax to a stream, inserting whitespace only where two tokens would otherwise merge.
/// </summary>
internal sealed class PdfOutput
{
    private readonly Stream _stream;
    private bool _needsSeparator;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfOutput"/> class.
    /// </summary>
    public PdfOutput(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// The current byte offset, used for the cross-reference table.
    /// </summary>
    public long Position => _stream.Position;

    /// <summary>
    /// Formats a real number the way PDF expects: no exponent, no negative zero.
    /// </summary>
    public static string FormatReal(double value)
    {
        string text = Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>
    /// Writes a token made of regular characters, such as a number, keyword or name.
    /// </summary>
    public void WriteToken(string token) => WriteToken(Encoding.ASCII.GetBytes(token));

    /// <summary>
    /// Writes a token made of regular characters, such as a number, keyword or name.
    /// </summary>
    public void WriteToken(ReadOnlySpan<byte> token)
    {
        if (_needsSeparator && token.Length > 0 && !PdfLexer.IsDelimiter(token[0]))
        {
            _stream.WriteByte((byte)' ');
        }

        WriteRaw(token);
    }

    /// <summary>
    /// Writes bytes that start with a delimiter, such as a string, array or dictionary bracket.
    /// </summary>
    public void WriteDelimited(ReadOnlySpan<byte> bytes) => WriteRaw(bytes);

    /// <summary>
    /// Writes bytes as they are.
    /// </summary>
    public void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return;
        }

        _stream.Write(bytes);
        byte last = bytes[^1];
        _needsSeparator = !PdfLexer.IsWhitespace(last) && !PdfLexer.IsDelimiter(last);
    }

    /// <summary>
    /// Writes ASCII text as it is.
    /// </summary>
    public void WriteRaw(string text) => WriteRaw(Encoding.ASCII.GetBytes(text));
}
