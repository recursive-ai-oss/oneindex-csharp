using System.Buffers.Binary;
using System.Text;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>Which way a sort key runs.</summary>
public enum SortDirection : byte
{
    /// <summary>Smallest first (A to Z, oldest first, nearest first).</summary>
    Ascending,

    /// <summary>Largest first.</summary>
    Descending,
}

/// <summary>Where documents that have no value for a sort key go. The placement does not depend on the direction.</summary>
public enum MissingValuePlacement : byte
{
    /// <summary>After every document that has a value (the default).</summary>
    Last,

    /// <summary>Before every document that has a value.</summary>
    First,
}

/// <summary>One key of a sort. Keys are applied in order; a later key only separates documents the earlier ones tie.</summary>
public abstract record SortCriterion
{
    private protected SortCriterion()
    {
    }

    /// <summary>The direction. Ascending unless the kind says otherwise (score sorts descending by default).</summary>
    public SortDirection Direction { get; init; }

    /// <summary>Where documents without a value go. Default <see cref="MissingValuePlacement.Last"/>.</summary>
    public MissingValuePlacement Missing { get; init; }

    internal abstract void Describe(StringBuilder into);
}

/// <summary>Sorts by the relevance score. Descending (best first) by default; a document without a score cannot be missing.</summary>
public sealed record ScoreSort : SortCriterion
{
    /// <summary>Creates a score key, descending.</summary>
    public ScoreSort() => Direction = SortDirection.Descending;

    internal override void Describe(StringBuilder into) => into.Append("score:").Append((int)Direction).Append(';');
}

/// <summary>
/// Sorts by a column: keyword (byte order of the UTF-8 text), integral, date, bool (false before true) or fractional (NaN after every
/// number, -0.0 equal to 0.0). A document with several values sorts by its smallest when ascending and its largest when descending.
/// </summary>
/// <param name="Field">The field; it must keep a column.</param>
public sealed record FieldSort(string Field) : SortCriterion
{
    internal override void Describe(StringBuilder into) => into.Append("field:").Append(Field).Append(':').Append((int)Direction).Append((int)Missing).Append(';');
}

/// <summary>
/// Sorts by the great-circle distance in metres from a point to the document's geo point field (<see cref="GeoMath.HaversineMeters"/>).
/// A document with several points sorts by its nearest when ascending and its farthest when descending.
/// </summary>
/// <param name="Field">The geo point field; it must keep a column.</param>
/// <param name="Latitude">The point's latitude in degrees.</param>
/// <param name="Longitude">The point's longitude in degrees.</param>
public sealed record GeoDistanceSort(string Field, double Latitude, double Longitude) : SortCriterion
{
    internal override void Describe(StringBuilder into)
        => into.Append("geo:").Append(Field).Append(':').Append(BitConverter.DoubleToInt64Bits(Latitude)).Append(':').Append(BitConverter.DoubleToInt64Bits(Longitude)).Append(':').Append((int)Direction).Append((int)Missing).Append(';');
}

/// <summary>A document's value for one sort key: nothing, a number, or text.</summary>
public readonly struct SortValue : IEquatable<SortValue>
{
    private readonly byte _kind; // 0 missing, 1 number, 2 text
    private readonly long _number;
    private readonly byte[]? _text;

    private SortValue(byte kind, long number, byte[]? text)
    {
        _kind = kind;
        _number = number;
        _text = text;
    }

    /// <summary>The value of a document that has none.</summary>
    public static SortValue Missing => default;

    /// <summary>Whether the document had no value.</summary>
    public bool IsMissing => _kind == 0;

    /// <summary>Whether the value is text (a keyword).</summary>
    public bool IsText => _kind == 2;

    /// <summary>
    /// The number in ordered integer form: the integer, date (milliseconds) or bool (0 or 1) itself, and for fractional values, scores
    /// and distances <see cref="SortableBits.Encode"/> of the double.
    /// </summary>
    public long Number => _number;

    /// <summary>The UTF-8 bytes of a text value, or an empty array.</summary>
    public ReadOnlySpan<byte> Text => _text;

    /// <summary>The number as a double, for a value that came from a fractional field, a score or a distance.</summary>
    /// <returns>The double.</returns>
    public double AsDouble() => SortableBits.Decode(_number);

    internal static SortValue FromNumber(long number) => new(1, number, null);

    internal static SortValue FromText(byte[] text) => new(2, 0, text);

    /// <inheritdoc/>
    public bool Equals(SortValue other) => _kind == other._kind && _number == other._number && Text.SequenceEqual(other.Text);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SortValue v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_kind, _number);

    /// <summary>Compares two values for equality.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(SortValue left, SortValue right) => left.Equals(right);

    /// <summary>Compares two values for inequality.</summary>
    /// <param name="left">The first.</param>
    /// <param name="right">The second.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(SortValue left, SortValue right) => !left.Equals(right);
}

/// <summary>A hit of a sorted search: the document, its score and the values it was ordered by.</summary>
/// <param name="DocId">The global document id.</param>
/// <param name="Score">The score; 0 when the sort did not need scores.</param>
/// <param name="Values">The document's value for each sort key, in key order.</param>
/// <param name="Id">The document's identifier (UTF-8) when the schema has an identifier field, else <see langword="null"/>.</param>
public sealed record FieldDoc(int DocId, float Score, IReadOnlyList<SortValue> Values, byte[]? Id);

/// <summary>The best documents of a sorted search.</summary>
/// <param name="TotalHits">How many documents matched (and, for a search-after search, how many matched in all, not only after the cursor).</param>
/// <param name="Hits">The documents in sort order.</param>
public sealed record TopFieldDocs(long TotalHits, IReadOnlyList<FieldDoc> Hits);

/// <summary>
/// A place in a sorted result list: the sort values and identifier of the last document of a page. Searching after it returns the
/// documents that sort strictly after that point. Because it holds values and the identifier, not a position, it stays valid across
/// commits: documents added, changed or removed between pages shift nothing, and a document is neither repeated nor skipped as long as its own
/// sort values do not change. When the schema has no identifier field the cursor falls back to the global document id, which a merge renumbers,
/// so such a cursor holds only within one snapshot. Relevance scores can move when other documents change, so a cursor over a
/// score key is as stable as the scores are.
/// </summary>
public sealed class SortCursor
{
    internal SortCursor(uint fingerprint, IReadOnlyList<SortValue> values, byte[]? id, int docId)
    {
        Fingerprint = fingerprint;
        Values = values;
        Id = id;
        DocId = docId;
    }

    /// <summary>The values of the last document, one per sort key.</summary>
    public IReadOnlyList<SortValue> Values { get; }

    /// <summary>The identifier of the last document, or <see langword="null"/> when the schema has none.</summary>
    public byte[]? Id { get; }

    /// <summary>The global document id of the last document; used only when <see cref="Id"/> is null.</summary>
    public int DocId { get; }

    internal uint Fingerprint { get; }

    /// <summary>Makes the cursor that continues after a hit.</summary>
    /// <param name="keys">The sort keys the search used.</param>
    /// <param name="hit">The last hit of the page.</param>
    /// <returns>The cursor.</returns>
    public static SortCursor After(IReadOnlyList<SortCriterion> keys, FieldDoc hit)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(hit);
        if (hit.Values.Count != keys.Count)
        {
            throw new ArgumentException("The hit was not produced by this sort.", nameof(hit));
        }

        return new SortCursor(SortSpec.Fingerprint(keys), hit.Values, hit.Id, hit.DocId);
    }

    /// <summary>Writes the cursor as a URL-safe text token that <see cref="TryParse"/> reads back.</summary>
    /// <returns>The token.</returns>
    public string ToToken()
    {
        var sink = new ByteSink();
        sink.WriteByte(1);
        sink.WriteUInt32(Fingerprint);
        sink.WriteVarUInt((ulong)Values.Count);
        foreach (SortValue v in Values)
        {
            if (v.IsMissing)
            {
                sink.WriteByte(0);
            }
            else if (v.IsText)
            {
                sink.WriteByte(2);
                sink.WriteBytes(v.Text);
            }
            else
            {
                sink.WriteByte(1);
                sink.WriteUInt64((ulong)v.Number);
            }
        }

        if (Id is not null)
        {
            sink.WriteByte(1);
            sink.WriteBytes(Id);
        }
        else
        {
            sink.WriteByte(2);
            sink.WriteUInt32((uint)DocId);
        }

        return Convert.ToBase64String(sink.WrittenSpan).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>Reads a token written by <see cref="ToToken"/>. Never throws.</summary>
    /// <param name="token">The token.</param>
    /// <param name="cursor">The cursor when valid.</param>
    /// <returns><see langword="true"/> when the token is well formed.</returns>
    public static bool TryParse(string? token, out SortCursor? cursor)
    {
        cursor = null;
        if (string.IsNullOrEmpty(token) || token.Length > 1 << 16)
        {
            return false;
        }

        string b64 = token.Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        byte[] data;
        try
        {
            data = Convert.FromBase64String(b64);
        }
        catch (FormatException)
        {
            return false;
        }

        int p = 0;
        if (data.Length < 6 || data[p++] != 1)
        {
            return false;
        }

        uint fingerprint = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
        p += 4;
        if (!TryVar(data, ref p, out ulong count) || count > 64)
        {
            return false;
        }

        var values = new SortValue[(int)count];
        for (int i = 0; i < values.Length; i++)
        {
            if (p >= data.Length)
            {
                return false;
            }

            byte kind = data[p++];
            switch (kind)
            {
                case 0:
                    break;
                case 1:
                    if (data.Length - p < 8)
                    {
                        return false;
                    }

                    values[i] = SortValue.FromNumber(BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(p)));
                    p += 8;
                    break;
                case 2:
                    if (!TryBytes(data, ref p, out byte[]? text))
                    {
                        return false;
                    }

                    values[i] = SortValue.FromText(text!);
                    break;
                default:
                    return false;
            }
        }

        if (p >= data.Length)
        {
            return false;
        }

        byte idKind = data[p++];
        byte[]? id = null;
        int docId = 0;
        if (idKind == 1)
        {
            if (!TryBytes(data, ref p, out id))
            {
                return false;
            }
        }
        else if (idKind == 2)
        {
            if (data.Length - p < 4)
            {
                return false;
            }

            docId = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
            p += 4;
        }
        else
        {
            return false;
        }

        if (p != data.Length)
        {
            return false;
        }

        cursor = new SortCursor(fingerprint, values, id, docId);
        return true;
    }

    private static bool TryVar(byte[] data, ref int p, out ulong value)
    {
        value = 0;
        for (int shift = 0; shift < 64 && p < data.Length; shift += 7)
        {
            byte b = data[p++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryBytes(byte[] data, ref int p, out byte[]? bytes)
    {
        bytes = null;
        if (!TryVar(data, ref p, out ulong len) || len > (ulong)(data.Length - p))
        {
            return false;
        }

        bytes = data.AsSpan(p, (int)len).ToArray();
        p += (int)len;
        return true;
    }
}

internal static class SortSpec
{
    // A cursor only fits the sort it came from; the fingerprint is a checksum of the keys' canonical description.
    public static uint Fingerprint(IReadOnlyList<SortCriterion> keys)
    {
        var sb = new StringBuilder();
        foreach (SortCriterion k in keys)
        {
            k.Describe(sb);
        }

        return Crc32C.Compute(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
