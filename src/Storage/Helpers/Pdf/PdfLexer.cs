using System;
using System.Text;

namespace Altinn.Platform.Storage.Helpers.Pdf;

/// <summary>
/// Parses PDF objects from a byte buffer (ISO 32000-1, sections 7.2 and 7.3).
/// </summary>
internal sealed class PdfLexer
{
    private const int _maxDepth = 64;

    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>
    /// Initializes a new instance of the <see cref="PdfLexer"/> class.
    /// </summary>
    public PdfLexer(ReadOnlyMemory<byte> data, int position = 0)
    {
        _data = data;
        Position = position;
    }

    /// <summary>
    /// The current byte offset.
    /// </summary>
    public int Position { get; set; }

    private ReadOnlySpan<byte> Span => _data.Span;

    /// <summary>
    /// Whether a byte is PDF whitespace.
    /// </summary>
    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    /// <summary>
    /// Whether a byte is a PDF delimiter.
    /// </summary>
    public static bool IsDelimiter(byte b) =>
        b
            is (byte)'('
                or (byte)')'
                or (byte)'<'
                or (byte)'>'
                or (byte)'['
                or (byte)']'
                or (byte)'{'
                or (byte)'}'
                or (byte)'/'
                or (byte)'%';

    /// <summary>
    /// Skips whitespace and comments.
    /// </summary>
    public void SkipWhitespace()
    {
        ReadOnlySpan<byte> span = Span;
        while (Position < span.Length)
        {
            byte b = span[Position];
            if (IsWhitespace(b))
            {
                Position++;
            }
            else if (b == '%')
            {
                while (
                    Position < span.Length && span[Position] is not (byte)'\n' and not (byte)'\r'
                )
                {
                    Position++;
                }
            }
            else
            {
                return;
            }
        }
    }

    /// <summary>
    /// Reads the next run of regular characters, such as a keyword or number, without consuming it
    /// when <paramref name="consume"/> is false.
    /// </summary>
    public string PeekToken(bool consume = false)
    {
        SkipWhitespace();
        ReadOnlySpan<byte> span = Span;
        int start = Position;
        int end = start;
        while (end < span.Length && !IsWhitespace(span[end]) && !IsDelimiter(span[end]))
        {
            end++;
        }

        if (consume)
        {
            Position = end;
        }

        return Encoding.ASCII.GetString(span[start..end]);
    }

    /// <summary>
    /// Consumes the given keyword, or throws when the next token is something else.
    /// </summary>
    public void ExpectKeyword(string keyword)
    {
        int start = Position;
        string token = PeekToken(consume: true);
        if (token != keyword)
        {
            throw new PdfFormatException(
                $"Expected '{keyword}' at offset {start}, found '{token}'."
            );
        }
    }

    /// <summary>
    /// Reads a non-negative integer.
    /// </summary>
    public long ReadInteger()
    {
        int start = Position;
        string token = PeekToken(consume: true);
        if (!long.TryParse(token, out long value) || value < 0)
        {
            throw new PdfFormatException(
                $"Expected an integer at offset {start}, found '{token}'."
            );
        }

        return value;
    }

    /// <summary>
    /// Reads one object. References (<c>n g R</c>) are returned unresolved.
    /// </summary>
    public PdfObject ReadObject() => ReadObject(0);

    private PdfObject ReadObject(int depth)
    {
        if (depth > _maxDepth)
        {
            throw new PdfFormatException("Objects are nested too deeply.");
        }

        SkipWhitespace();
        ReadOnlySpan<byte> span = Span;
        if (Position >= span.Length)
        {
            throw new PdfFormatException("Unexpected end of data.");
        }

        byte b = span[Position];
        switch (b)
        {
            case (byte)'/':
                Position++;
                return new PdfName(PeekToken(consume: true));
            case (byte)'(':
                return ReadLiteralString();
            case (byte)'<' when Position + 1 < span.Length && span[Position + 1] == '<':
                return ReadDictionary(depth);
            case (byte)'<':
                return ReadHexString();
            case (byte)'[':
                return ReadArray(depth);
        }

        int start = Position;
        string token = PeekToken(consume: true);
        switch (token)
        {
            case "true":
                return new PdfBoolean(true);
            case "false":
                return new PdfBoolean(false);
            case "null":
                return PdfNull.Instance;
        }

        if (token.Length == 0 || !IsNumberStart(token[0]))
        {
            throw new PdfFormatException($"Unexpected token '{token}' at offset {start}.");
        }

        if (int.TryParse(token, out int objectNumber) && objectNumber >= 0)
        {
            int afterNumber = Position;
            if (
                int.TryParse(PeekToken(consume: true), out int generation)
                && generation >= 0
                && PeekToken(consume: true) == "R"
            )
            {
                return new PdfReference(objectNumber, generation);
            }

            Position = afterNumber;
        }

        return PdfNumber.FromText(token);
    }

    private static bool IsNumberStart(char c) => c is (>= '0' and <= '9') or '+' or '-' or '.';

    private PdfDictionary ReadDictionary(int depth)
    {
        Position += 2;
        var dictionary = new PdfDictionary();
        while (true)
        {
            SkipWhitespace();
            if (Span[Position..].StartsWith(">>"u8))
            {
                Position += 2;
                return dictionary;
            }

            if (ReadObject(depth + 1) is not PdfName key)
            {
                throw new PdfFormatException(
                    $"Expected a dictionary key before offset {Position}."
                );
            }

            dictionary.Set(key, ReadObject(depth + 1));
        }
    }

    private PdfArray ReadArray(int depth)
    {
        Position++;
        var array = new PdfArray();
        while (true)
        {
            SkipWhitespace();
            if (Position < Span.Length && Span[Position] == ']')
            {
                Position++;
                return array;
            }

            array.Items.Add(ReadObject(depth + 1));
        }
    }

    private PdfString ReadLiteralString()
    {
        ReadOnlySpan<byte> span = Span;
        int start = Position;
        int nesting = 0;
        while (Position < span.Length)
        {
            byte b = span[Position++];
            if (b == '\\')
            {
                Position++;
            }
            else if (b == '(')
            {
                nesting++;
            }
            else if (b == ')' && --nesting == 0)
            {
                return new PdfString(span[start..Position].ToArray());
            }
        }

        throw new PdfFormatException($"Unterminated string at offset {start}.");
    }

    private PdfString ReadHexString()
    {
        int end = Span[Position..].IndexOf((byte)'>');
        if (end < 0)
        {
            throw new PdfFormatException($"Unterminated hex string at offset {Position}.");
        }

        var source = Span.Slice(Position, end + 1).ToArray();
        Position += end + 1;
        return new PdfString(source);
    }
}

/// <summary>
/// Thrown when PDF input cannot be parsed.
/// </summary>
public sealed class PdfFormatException(string message) : Exception(message);
