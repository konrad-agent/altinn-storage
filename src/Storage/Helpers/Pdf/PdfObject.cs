using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Altinn.Platform.Storage.Helpers.Pdf;

/// <summary>
/// Base type for the PDF object model (ISO 32000-1, section 7.3).
/// Tokens keep their source bytes so that objects copied between documents are written back unchanged.
/// </summary>
internal abstract class PdfObject
{
    /// <summary>
    /// Writes the object in PDF syntax.
    /// </summary>
    public abstract void WriteTo(PdfOutput output);
}

/// <summary>
/// The PDF null object.
/// </summary>
internal sealed class PdfNull : PdfObject
{
    /// <summary>
    /// The single instance.
    /// </summary>
    public static readonly PdfNull Instance = new();

    private PdfNull() { }

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) => output.WriteToken("null"u8);
}

/// <summary>
/// A PDF boolean.
/// </summary>
internal sealed class PdfBoolean(bool value) : PdfObject
{
    /// <summary>
    /// The value.
    /// </summary>
    public bool Value { get; } = value;

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) =>
        output.WriteToken(Value ? "true"u8 : "false"u8);
}

/// <summary>
/// A PDF integer or real number, stored as its source text.
/// </summary>
internal sealed class PdfNumber : PdfObject
{
    private readonly string _text;

    private PdfNumber(string text)
    {
        _text = text;
    }

    /// <summary>
    /// Creates a number from its PDF source text.
    /// </summary>
    public static PdfNumber FromText(string text) => new(text);

    /// <summary>
    /// Creates an integer.
    /// </summary>
    public static PdfNumber FromInt(long value) =>
        new(value.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// Creates a real number.
    /// </summary>
    public static PdfNumber FromDouble(double value) => new(PdfOutput.FormatReal(value));

    /// <summary>
    /// The value as a double.
    /// </summary>
    public double AsDouble() =>
        double.Parse(_text, NumberStyles.Float, CultureInfo.InvariantCulture);

    /// <summary>
    /// The value as an integer. Real numbers are truncated.
    /// </summary>
    public long AsLong() =>
        long.TryParse(
            _text,
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out long l
        )
            ? l
            : (long)AsDouble();

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) => output.WriteToken(_text);

    /// <inheritdoc/>
    public override string ToString() => _text;
}

/// <summary>
/// A PDF name, stored in its escaped source form without the leading slash.
/// </summary>
internal sealed class PdfName : PdfObject, IEquatable<PdfName>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PdfName"/> class.
    /// </summary>
    public PdfName(string value)
    {
        Value = value;
    }

    /// <summary>
    /// The name without the leading slash.
    /// </summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) => output.WriteToken("/" + Value);

    /// <inheritdoc/>
    public bool Equals(PdfName? other) => other is not null && other.Value == Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is PdfName other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => "/" + Value;
}

/// <summary>
/// A literal or hexadecimal PDF string, stored as its source bytes including the delimiters.
/// </summary>
internal sealed class PdfString(byte[] source) : PdfObject
{
    /// <summary>
    /// The source bytes including the enclosing <c>()</c> or <c>&lt;&gt;</c>.
    /// </summary>
    public byte[] Source { get; } = source;

    /// <summary>
    /// Creates a literal string from text that only contains characters in the WinAnsi range.
    /// </summary>
    public static PdfString FromText(string text)
    {
        var sb = new StringBuilder(text.Length + 2).Append('(');
        foreach (char c in text)
        {
            if (c is '(' or ')' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return new PdfString(Encoding.ASCII.GetBytes(sb.Append(')').ToString()));
    }

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) => output.WriteDelimited(Source);
}

/// <summary>
/// A PDF array.
/// </summary>
internal sealed class PdfArray : PdfObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PdfArray"/> class.
    /// </summary>
    public PdfArray(IEnumerable<PdfObject>? items = null)
    {
        Items = items is null ? [] : [.. items];
    }

    /// <summary>
    /// The array items.
    /// </summary>
    public List<PdfObject> Items { get; }

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output)
    {
        output.WriteDelimited("["u8);
        foreach (PdfObject item in Items)
        {
            item.WriteTo(output);
        }

        output.WriteDelimited("]"u8);
    }
}

/// <summary>
/// A PDF dictionary. Keys keep their insertion order.
/// </summary>
internal class PdfDictionary : PdfObject
{
    private readonly List<KeyValuePair<PdfName, PdfObject>> _entries = [];

    /// <summary>
    /// The entries in insertion order.
    /// </summary>
    public IReadOnlyList<KeyValuePair<PdfName, PdfObject>> Entries => _entries;

    /// <summary>
    /// Gets the value for a key, or <c>null</c> when the key is absent.
    /// </summary>
    public PdfObject? this[string key]
    {
        get
        {
            foreach (var entry in _entries)
            {
                if (entry.Key.Value == key)
                {
                    return entry.Value;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Sets a value, replacing any existing value for the key.
    /// </summary>
    public void Set(string key, PdfObject value) => Set(new PdfName(key), value);

    /// <summary>
    /// Sets a value, replacing any existing value for the key.
    /// </summary>
    public void Set(PdfName key, PdfObject value)
    {
        for (int i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Key.Equals(key))
            {
                _entries[i] = new(key, value);
                return;
            }
        }

        _entries.Add(new(key, value));
    }

    /// <summary>
    /// Removes a key if present.
    /// </summary>
    public void Remove(string key) => _entries.RemoveAll(e => e.Key.Value == key);

    /// <summary>
    /// Whether the dictionary has a <c>/Type</c> entry with the given name.
    /// </summary>
    public bool IsType(string type) => this["Type"] is PdfName name && name.Value == type;

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output)
    {
        output.WriteDelimited("<<"u8);
        foreach (var entry in _entries)
        {
            entry.Key.WriteTo(output);
            entry.Value.WriteTo(output);
        }

        output.WriteDelimited(">>"u8);
    }
}

/// <summary>
/// A PDF stream: a dictionary followed by data. The data is kept encoded, as it appears in the file.
/// </summary>
internal sealed class PdfStream : PdfObject
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PdfStream"/> class.
    /// </summary>
    public PdfStream(PdfDictionary dictionary, ReadOnlyMemory<byte> data)
    {
        Dictionary = dictionary;
        Data = data;
    }

    /// <summary>
    /// The stream dictionary.
    /// </summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The encoded stream data.
    /// </summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output)
    {
        Dictionary.Set("Length", PdfNumber.FromInt(Data.Length));
        Dictionary.WriteTo(output);
        output.WriteRaw("\nstream\n"u8);
        output.WriteRaw(Data.Span);
        output.WriteRaw("\nendstream"u8);
    }
}

/// <summary>
/// An indirect reference (<c>n g R</c>).
/// </summary>
internal sealed class PdfReference(int objectNumber, int generation) : PdfObject
{
    /// <summary>
    /// The object number.
    /// </summary>
    public int ObjectNumber { get; } = objectNumber;

    /// <summary>
    /// The generation number.
    /// </summary>
    public int Generation { get; } = generation;

    /// <inheritdoc/>
    public override void WriteTo(PdfOutput output) =>
        output.WriteToken(
            string.Create(CultureInfo.InvariantCulture, $"{ObjectNumber} {Generation} R")
        );
}
