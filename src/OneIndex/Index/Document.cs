using System.Text.Json;

namespace OneIndex.Index;

/// <summary>The kind of value a <see cref="FieldValue"/> holds.</summary>
public enum FieldValueKind : byte
{
    /// <summary>Text.</summary>
    Text = 1,

    /// <summary>A 64-bit integer.</summary>
    Integral = 2,

    /// <summary>A 64-bit float.</summary>
    Fractional = 3,

    /// <summary>A boolean.</summary>
    Bool = 4,

    /// <summary>A time, as milliseconds since the Unix epoch.</summary>
    Date = 5,

    /// <summary>A latitude and longitude.</summary>
    GeoPoint = 6,

    /// <summary>Bytes.</summary>
    Bytes = 7,

    /// <summary>A float vector.</summary>
    Vector = 8,

    /// <summary>A JSON value.</summary>
    Json = 9,
}

/// <summary>A single field value of any type, without boxing the numeric kinds.</summary>
public readonly struct FieldValue : IEquatable<FieldValue>
{
    private readonly long _a;
    private readonly long _b;
    private readonly object? _object;

    private FieldValue(FieldValueKind kind, long a, long b, object? obj)
    {
        Kind = kind;
        _a = a;
        _b = b;
        _object = obj;
    }

    /// <summary>What the value holds; 0 for a default value, which holds nothing.</summary>
    public FieldValueKind Kind { get; }

    /// <summary>Creates a text value.</summary>
    /// <param name="value">The text.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromText(string value) => new(FieldValueKind.Text, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Creates an integer value.</summary>
    /// <param name="value">The integer.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromInt64(long value) => new(FieldValueKind.Integral, value, 0, null);

    /// <summary>Creates a floating-point value.</summary>
    /// <param name="value">The number.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromFloat64(double value) => new(FieldValueKind.Fractional, BitConverter.DoubleToInt64Bits(value), 0, null);

    /// <summary>Creates a boolean value.</summary>
    /// <param name="value">The boolean.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromBool(bool value) => new(FieldValueKind.Bool, value ? 1 : 0, 0, null);

    /// <summary>Creates a date value with millisecond precision, in UTC.</summary>
    /// <param name="value">The time.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromDate(DateTimeOffset value) => new(FieldValueKind.Date, value.ToUnixTimeMilliseconds(), 0, null);

    /// <summary>Creates a geo point.</summary>
    /// <param name="latitude">Degrees, -90 to 90.</param>
    /// <param name="longitude">Degrees, -180 to 180.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate is out of range or not a number.</exception>
    public static FieldValue FromGeoPoint(double latitude, double longitude)
    {
        if (!(latitude is >= -90 and <= 90))
        {
            throw new ArgumentOutOfRangeException(nameof(latitude), latitude, "Latitude must be between -90 and 90.");
        }

        if (!(longitude is >= -180 and <= 180))
        {
            throw new ArgumentOutOfRangeException(nameof(longitude), longitude, "Longitude must be between -180 and 180.");
        }

        return new FieldValue(FieldValueKind.GeoPoint, BitConverter.DoubleToInt64Bits(latitude), BitConverter.DoubleToInt64Bits(longitude), null);
    }

    /// <summary>Creates a bytes value (the array is not copied).</summary>
    /// <param name="value">The bytes.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromBytes(byte[] value) => new(FieldValueKind.Bytes, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Creates a vector value (the array is not copied).</summary>
    /// <param name="value">The components.</param>
    /// <returns>The value.</returns>
    public static FieldValue FromVector(float[] value) => new(FieldValueKind.Vector, 0, 0, value ?? throw new ArgumentNullException(nameof(value)));

    /// <summary>Creates a JSON value.</summary>
    /// <param name="value">The element; it must stay valid (its document alive).</param>
    /// <returns>The value.</returns>
    public static FieldValue FromJson(JsonElement value) => new(FieldValueKind.Json, 0, 0, value);

    /// <summary>The text of a <see cref="FieldValueKind.Text"/> value.</summary>
    /// <returns>The text.</returns>
    /// <exception cref="InvalidOperationException">The value is not text.</exception>
    public string AsText() => Kind == FieldValueKind.Text ? (string)_object! : throw Wrong(FieldValueKind.Text);

    /// <summary>The integer of an <see cref="FieldValueKind.Integral"/> or <see cref="FieldValueKind.Date"/> (milliseconds) value.</summary>
    /// <returns>The integer.</returns>
    /// <exception cref="InvalidOperationException">The value is neither.</exception>
    public long AsInt64() => Kind is FieldValueKind.Integral or FieldValueKind.Date ? _a : throw Wrong(FieldValueKind.Integral);

    /// <summary>The number of a float (or integer) value.</summary>
    /// <returns>The number.</returns>
    /// <exception cref="InvalidOperationException">The value is not numeric.</exception>
    public double AsFloat64() => Kind switch
    {
        FieldValueKind.Fractional => BitConverter.Int64BitsToDouble(_a),
        FieldValueKind.Integral => _a,
        _ => throw Wrong(FieldValueKind.Fractional),
    };

    /// <summary>The boolean of a <see cref="FieldValueKind.Bool"/> value.</summary>
    /// <returns>The boolean.</returns>
    /// <exception cref="InvalidOperationException">The value is not a boolean.</exception>
    public bool AsBool() => Kind == FieldValueKind.Bool ? _a != 0 : throw Wrong(FieldValueKind.Bool);

    /// <summary>The coordinates of a geo point.</summary>
    /// <returns>Latitude and longitude in degrees.</returns>
    /// <exception cref="InvalidOperationException">The value is not a geo point.</exception>
    public (double Latitude, double Longitude) AsGeoPoint()
        => Kind == FieldValueKind.GeoPoint ? (BitConverter.Int64BitsToDouble(_a), BitConverter.Int64BitsToDouble(_b)) : throw Wrong(FieldValueKind.GeoPoint);

    /// <summary>The bytes of a bytes value.</summary>
    /// <returns>The bytes.</returns>
    /// <exception cref="InvalidOperationException">The value is not bytes.</exception>
    public ReadOnlyMemory<byte> AsBytes() => Kind == FieldValueKind.Bytes ? (byte[])_object! : throw Wrong(FieldValueKind.Bytes);

    /// <summary>The components of a vector value.</summary>
    /// <returns>The components.</returns>
    /// <exception cref="InvalidOperationException">The value is not a vector.</exception>
    public ReadOnlyMemory<float> AsVector() => Kind == FieldValueKind.Vector ? (float[])_object! : throw Wrong(FieldValueKind.Vector);

    /// <summary>The element of a JSON value.</summary>
    /// <returns>The element.</returns>
    /// <exception cref="InvalidOperationException">The value is not JSON.</exception>
    public JsonElement AsJson() => Kind == FieldValueKind.Json ? (JsonElement)_object! : throw Wrong(FieldValueKind.Json);

    /// <inheritdoc/>
    public bool Equals(FieldValue other) => Kind == other.Kind && _a == other._a && _b == other._b && Equals(_object, other._object);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FieldValue other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, _a, _b, _object); // in-process only, never persisted

    /// <summary>Whether two values are equal (arrays and JSON compare by reference).</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public static bool operator ==(FieldValue left, FieldValue right) => left.Equals(right);

    /// <summary>Whether two values differ.</summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns><see langword="true"/> when different.</returns>
    public static bool operator !=(FieldValue left, FieldValue right) => !left.Equals(right);

    private InvalidOperationException Wrong(FieldValueKind wanted) => new($"The value is {Kind}, not {wanted}.");
}

/// <summary>A named value in a document.</summary>
/// <param name="Name">The field name.</param>
/// <param name="Value">The value.</param>
public readonly record struct DocumentField(string Name, FieldValue Value);

/// <summary>An untyped document: an ordered list of field values. A field given several times holds several values (multi-valued fields).</summary>
public sealed class Document
{
    private readonly List<DocumentField> _fields = [];

    /// <summary>The values in the order they were added.</summary>
    public IReadOnlyList<DocumentField> Fields => _fields;

    /// <summary>Adds a value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The value.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, FieldValue value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (value.Kind == 0)
        {
            throw new ArgumentException($"Field '{name}' was given an empty value.", nameof(value));
        }

        _fields.Add(new DocumentField(name, value));
        return this;
    }

    internal void Remove(string name) => _fields.RemoveAll(f => string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>Adds a text value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The text.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, string value) => Add(name, FieldValue.FromText(value));

    /// <summary>Adds an integer value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The integer.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, long value) => Add(name, FieldValue.FromInt64(value));

    /// <summary>Adds a floating-point value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The number.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, double value) => Add(name, FieldValue.FromFloat64(value));

    /// <summary>Adds a boolean value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The boolean.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, bool value) => Add(name, FieldValue.FromBool(value));

    /// <summary>Adds a date value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The time.</param>
    /// <returns>This document.</returns>
    public Document Add(string name, DateTimeOffset value) => Add(name, FieldValue.FromDate(value));

    /// <summary>Adds a vector value.</summary>
    /// <param name="name">The field name.</param>
    /// <param name="value">The components (not copied).</param>
    /// <returns>This document.</returns>
    public Document Add(string name, float[] value) => Add(name, FieldValue.FromVector(value));

    /// <summary>The first value of a field.</summary>
    /// <param name="name">The field name.</param>
    /// <returns>The value, or an empty value (<see cref="FieldValue.Kind"/> is 0) if the document has none.</returns>
    public FieldValue Get(string name)
    {
        foreach (DocumentField f in _fields)
        {
            if (string.Equals(f.Name, name, StringComparison.Ordinal))
            {
                return f.Value;
            }
        }

        return default;
    }
}
