using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Codecs;

/// <summary>
/// Numbers as terms: the encoding the text codec indexes integer, date, floating-point and boolean fields with, so a range query
/// works on any segment even before a point index exists. A value becomes 16 lowercase hexadecimal digits of its ordered 64-bit
/// form with the sign bit flipped; fixed width and hexadecimal digits sorting in byte order make byte order of terms equal numeric
/// order, so a range is a contiguous run of the term dictionary and the digits two bounds share narrow the walk.
/// </summary>
public static class NumericTerms
{
    /// <summary>The length of every numeric term in bytes (and characters).</summary>
    public const int TermLength = 16;

    /// <summary>The ordered 64-bit form of a field value: integers, dates and booleans as they are, doubles through <see cref="SortableBits"/>.</summary>
    /// <param name="type">The field's type.</param>
    /// <param name="value">The value.</param>
    /// <returns>The ordered integer.</returns>
    /// <exception cref="ArgumentException">The value does not fit the type.</exception>
    public static long Ordered(FieldType type, FieldValue value) => type switch
    {
        // -0.0 and +0.0 are the same number to a query, so both get the encoding of +0.0.
        FieldType.Fractional => SortableBits.Encode(value.AsFloat64() + 0.0),
        FieldType.Integral or FieldType.Date => value.AsInt64(),
        FieldType.Bool => value.AsBool() ? 1 : 0,
        _ => throw new ArgumentException($"{type} is not a numeric field type.", nameof(type)),
    };

    /// <summary>Whether a field type is indexed as numeric terms.</summary>
    /// <param name="type">The field type.</param>
    /// <returns><see langword="true"/> for integer, floating-point, date and boolean fields.</returns>
    public static bool IsNumeric(FieldType type) => type is FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool;

    /// <summary>Writes the term of an ordered value.</summary>
    /// <param name="ordered">The ordered 64-bit form.</param>
    /// <param name="destination">At least <see cref="TermLength"/> bytes.</param>
    public static void Write(long ordered, Span<byte> destination)
    {
        ulong u = (ulong)ordered ^ 0x8000_0000_0000_0000UL;
        for (int i = TermLength - 1; i >= 0; i--)
        {
            destination[i] = (byte)"0123456789abcdef"[(int)(u & 0xF)];
            u >>= 4;
        }
    }

    /// <summary>Reads a term back.</summary>
    /// <param name="term">A numeric term.</param>
    /// <param name="ordered">The ordered 64-bit form.</param>
    /// <returns><see langword="false"/> when the bytes are not a numeric term.</returns>
    public static bool TryRead(ReadOnlySpan<byte> term, out long ordered)
    {
        ordered = 0;
        if (term.Length != TermLength)
        {
            return false;
        }

        ulong u = 0;
        foreach (byte c in term)
        {
            int d = c is >= (byte)'0' and <= (byte)'9' ? c - '0' : c is >= (byte)'a' and <= (byte)'f' ? c - 'a' + 10 : -1;
            if (d < 0)
            {
                return false;
            }

            u = (u << 4) | (uint)d;
        }

        ordered = (long)(u ^ 0x8000_0000_0000_0000UL);
        return true;
    }
}
