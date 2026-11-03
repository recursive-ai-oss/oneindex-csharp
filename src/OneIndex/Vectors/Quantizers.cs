using System.Buffers.Binary;
using System.Numerics;
using OneIndex.Kernels;

namespace OneIndex.Vectors;

/// <summary>
/// Encoders and the distance estimate of the compressed vector codecs.
/// <para>
/// <b>Int8 and Int4</b> use one quantization interval per vector, chosen by optimized scalar quantization (Veasey, "Understanding
/// optimized scalar quantization", Elasticsearch Labs, 2024): a component <c>x</c> becomes the code
/// <c>round((2^n - 1)(clamp(x, a, b) - a) / (b - a))</c>, the interval <c>[a, b]</c> starts from the vector's mean and standard
/// deviation (a normal-distribution optimum per bit count) and is refined by coordinate descent on an anisotropic error that
/// weights the error parallel to the vector by <c>1 / lambda</c> more than the perpendicular one (lambda = 0.1).
/// The dot product of two quantized vectors then needs only an integer dot product of the codes and three cached numbers per vector
/// (the interval's lower end and step, and the sum of the codes), by expanding
/// <c>(a_y + s_y Y)(a_x + s_x X)</c> over the dimensions.
/// </para>
/// <para>
/// <b>OneBit</b> keeps the sign of every component (set when the component is above zero) and compares by Hamming distance.
/// </para>
/// All arithmetic that decides stored bytes is IEEE double or integer arithmetic in a fixed order, so the files written on x64 and
/// arm64 are identical.
/// </summary>
internal static class Quantizers
{
    /// <summary>The correction factors after the codes of a scalar-quantized record: lower end, step, sum of codes, squared length.</summary>
    public const int CorrectionBytes = 16;

    // The interval half-width in standard deviations that minimises the expected squared error of a normal variable, by bit count
    // (Veasey, table of initial intervals: 1 bit 0.798, 2 bits 1.493, 3 bits 2.051, 4 bits 2.514, 7 bits 3.611). 8 bits use the 7-bit value.
    private static double HalfWidth(int bits) => bits switch
    {
        1 => 0.798,
        2 => 1.493,
        3 => 2.051,
        4 => 2.514,
        _ => 3.611,
    };

    private const double Lambda = 0.1;
    private const int Rounds = 5;

    /// <summary>
    /// Quantizes a vector to <paramref name="bits"/>-bit codes with an optimized interval.
    /// </summary>
    /// <param name="x">The vector; finite.</param>
    /// <param name="bits">4 or 8.</param>
    /// <param name="codes">Receives one code per component, 0 to 2^bits - 1; at least <c>x.Length</c> long.</param>
    /// <param name="lower">The interval's lower end <c>a</c>.</param>
    /// <param name="step">The distance between neighbouring codes, <c>(b - a) / (2^bits - 1)</c>; 0 for a constant vector.</param>
    /// <returns>The sum of the codes.</returns>
    public static int Osq(ReadOnlySpan<float> x, int bits, Span<byte> codes, out float lower, out float step)
    {
        int n = x.Length;
        int levels = (1 << bits) - 1;
        double min = double.MaxValue, max = double.MinValue, sum = 0, sumSquares = 0;
        foreach (float f in x)
        {
            double v = f;
            min = Math.Min(min, v);
            max = Math.Max(max, v);
            sum += v;
            sumSquares += v * v;
        }

        if (n == 0 || !(max > min))
        {
            codes[..n].Clear();
            lower = n == 0 ? 0 : (float)min;
            step = 0;
            return 0;
        }

        double mean = sum / n;
        double variance = Math.Max(0, (sumSquares / n) - (mean * mean));
        double sigma = Math.Sqrt(variance);
        double a = Math.Max(mean - (HalfWidth(bits) * sigma), min);
        double b = Math.Min(mean + (HalfWidth(bits) * sigma), max);
        if (!(b > a))
        {
            a = min;
            b = max;
        }

        double error = Assign(x, levels, a, b, codes, sumSquares, out double s1, out double s2, out double t11, out double t12, out double t22);
        for (int round = 0; round < Rounds && sumSquares > 0; round++)
        {
            // Normal equations of the convex quadratic in (a, b) for fixed codes (Veasey, "Refining the quantization interval").
            double wp = (1 - Lambda) / sumSquares;
            double m11 = (wp * s1 * s1) + (Lambda * t11);
            double m12 = (wp * s1 * s2) + (Lambda * t12);
            double m22 = (wp * s2 * s2) + (Lambda * t22);
            double det = (m11 * m22) - (m12 * m12);
            if (!(Math.Abs(det) > 1e-12 * Math.Max(1e-300, Math.Abs(m11 * m22))))
            {
                break;
            }

            double na = ((s1 * m22) - (s2 * m12)) / det;
            double nb = ((s2 * m11) - (s1 * m12)) / det;
            if (!(nb > na) || !double.IsFinite(na) || !double.IsFinite(nb))
            {
                break;
            }

            // Score the new interval on the codes it produces; keep the better of old and new.
            double newError = ErrorOf(x, levels, na, nb, sumSquares);
            if (newError >= error)
            {
                break;
            }

            a = na;
            b = nb;
            error = Assign(x, levels, a, b, codes, sumSquares, out s1, out s2, out t11, out t12, out t22);
        }

        // Assign (above) left the codes of the last accepted interval in `codes`.
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            total += codes[i];
        }

        lower = (float)a;
        step = (float)((b - a) / levels);
        return total;
    }

    private static int Code(float value, double a, double b, double scale, int levels)
    {
        double v = Math.Min(Math.Max(value, a), b);
        int q = (int)Math.Floor(((v - a) * scale) + 0.5);
        return q < 0 ? 0 : q > levels ? levels : q;
    }

    // Assigns codes for the interval and returns the anisotropic error with the sums the normal equations need.
    private static double Assign(ReadOnlySpan<float> x, int levels, double a, double b, Span<byte> codes, double sumSquares,
        out double s1, out double s2, out double t11, out double t12, out double t22)
    {
        double scale = levels / (b - a);
        s1 = s2 = t11 = t12 = t22 = 0;
        double dotErr = 0, sqErr = 0;
        double step = (b - a) / levels;
        for (int i = 0; i < x.Length; i++)
        {
            int q = Code(x[i], a, b, scale, levels);
            codes[i] = (byte)q;
            double s = (double)q / levels;
            double xi = x[i];
            s1 += xi * (1 - s);
            s2 += xi * s;
            t11 += (1 - s) * (1 - s);
            t12 += (1 - s) * s;
            t22 += s * s;
            double diff = (a + (step * q)) - xi;
            dotErr += xi * diff;
            sqErr += diff * diff;
        }

        return sumSquares > 0 ? ((1 - Lambda) / sumSquares * dotErr * dotErr) + (Lambda * sqErr) : sqErr;
    }

    private static double ErrorOf(ReadOnlySpan<float> x, int levels, double a, double b, double sumSquares)
    {
        double scale = levels / (b - a);
        double step = (b - a) / levels;
        double dotErr = 0, sqErr = 0;
        for (int i = 0; i < x.Length; i++)
        {
            int q = Code(x[i], a, b, scale, levels);
            double xi = x[i];
            double diff = (a + (step * q)) - xi;
            dotErr += xi * diff;
            sqErr += diff * diff;
        }

        return sumSquares > 0 ? ((1 - Lambda) / sumSquares * dotErr * dotErr) + (Lambda * sqErr) : sqErr;
    }

    /// <summary>Writes the compressed record of a vector.</summary>
    /// <param name="codec">The codec; not <see cref="VectorQuantization.None"/>.</param>
    /// <param name="vector">The vector as indexed (scaled to length 1 for cosine).</param>
    /// <param name="record">Exactly <see cref="VectorQuantizer.RecordBytes"/> bytes.</param>
    /// <param name="scratch">A buffer of at least <c>vector.Length</c> bytes.</param>
    public static void Encode(VectorQuantization codec, ReadOnlySpan<float> vector, Span<byte> record, Span<byte> scratch)
    {
        int d = vector.Length;
        if (codec == VectorQuantization.OneBit)
        {
            record.Clear();
            for (int i = 0; i < d; i++)
            {
                if (vector[i] > 0)
                {
                    record[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return;
        }

        float norm = VectorKernels.SumSquares(vector);
        if (codec == VectorQuantization.Scalar8)
        {
            int sum = Osq(vector, 8, scratch, out float lower, out float step);
            // Codes are stored as signed bytes (code - 128) so the signed-byte dot kernel applies; the interval's lower end moves to match.
            for (int i = 0; i < d; i++)
            {
                record[i] = (byte)(sbyte)(scratch[i] - 128);
            }

            WriteCorrections(record[d..], (float)(lower + (128.0 * step)), step, sum - (128 * d), norm);
            return;
        }

        int packed = (d + 1) / 2;
        int total = Osq(vector, 4, scratch, out float l4, out float s4);
        for (int j = 0; j < packed; j++)
        {
            int hi = j + packed < d ? scratch[j + packed] : 0;
            record[j] = (byte)(scratch[j] | (hi << 4));
        }

        WriteCorrections(record[packed..], l4, s4, total, norm);
    }

    private static void WriteCorrections(Span<byte> at, float lower, float step, int codeSum, float norm)
    {
        BinaryPrimitives.WriteSingleLittleEndian(at, lower);
        BinaryPrimitives.WriteSingleLittleEndian(at[4..], step);
        BinaryPrimitives.WriteInt32LittleEndian(at[8..], codeSum);
        BinaryPrimitives.WriteSingleLittleEndian(at[12..], norm);
    }

    /// <summary>Whether a scalar record's correction factors are plausible; used when a block is loaded.</summary>
    public static bool CorrectionsValid(ReadOnlySpan<byte> at, int dimensions, int bits)
    {
        float lower = BinaryPrimitives.ReadSingleLittleEndian(at);
        float step = BinaryPrimitives.ReadSingleLittleEndian(at[4..]);
        int sum = BinaryPrimitives.ReadInt32LittleEndian(at[8..]);
        float norm = BinaryPrimitives.ReadSingleLittleEndian(at[12..]);
        long max = (long)dimensions * ((1 << bits) - 1);
        long min = bits == 8 ? -128L * dimensions : 0;
        long limit = bits == 8 ? 127L * dimensions : max;
        return float.IsFinite(lower) && float.IsFinite(step) && step >= 0 && float.IsFinite(norm) && norm >= 0 && sum >= min && sum <= limit;
    }
}

/// <summary>The compressed records of one vector field, numbered from 0 (their ordinal).</summary>
internal abstract class QuantizedValues
{
    public abstract int Count { get; }

    /// <summary>The record of a vector. The span stays valid for the life of the object.</summary>
    public abstract ReadOnlySpan<byte> Get(int ordinal);
}

/// <summary>Records held in one array.</summary>
internal sealed class ArrayQuantizedValues(byte[] data, int recordBytes) : QuantizedValues
{
    public override int Count => data.Length / recordBytes;

    public override ReadOnlySpan<byte> Get(int ordinal) => data.AsSpan(checked(ordinal * recordBytes), recordBytes);

    /// <summary>Compresses every vector of a set.</summary>
    public static ArrayQuantizedValues Build(VectorValues values, VectorQuantization codec)
    {
        int recordBytes = VectorQuantizer.RecordBytes(codec, values.Dimensions);
        var data = new byte[checked(values.Count * recordBytes)];
        var scratch = new byte[values.Dimensions];
        for (int i = 0; i < values.Count; i++)
        {
            Quantizers.Encode(codec, values.Get(i), data.AsSpan(i * recordBytes, recordBytes), scratch);
        }

        return new ArrayQuantizedValues(data, recordBytes);
    }
}

/// <summary>
/// Distances from a query to compressed vectors: the seam between the graph search and a codec. The query is compressed once in
/// <see cref="Reset"/>; <see cref="ToQuery"/> then costs one integer kernel call per node.
/// </summary>
internal sealed class QuantizedNodeScorer : NodeScorer
{
    private QuantizedValues _values = null!;
    private VectorQuantization _codec;
    private DistanceKind _kind;
    private int _dimensions;
    private int _packed;
    private sbyte[] _query = [];     // int8: d codes; int4: the low half then the high half, each `packed` long
    private byte[] _queryBits = [];  // one bit: the query's sign bits, padded to whole 64-bit words
    private byte[] _scratch = [];
    private double _queryLower;
    private double _queryStep;
    private double _querySum;
    private double _queryNorm;

    /// <summary>Points the scorer at a set of records and compresses the query for the codec.</summary>
    /// <param name="values">The compressed records.</param>
    /// <param name="codec">Their codec.</param>
    /// <param name="kind">How closeness becomes a distance.</param>
    /// <param name="query">The query as the float vectors were indexed (scaled to length 1 for cosine).</param>
    public void Reset(QuantizedValues values, VectorQuantization codec, DistanceKind kind, ReadOnlySpan<float> query)
    {
        _values = values;
        _codec = codec;
        _kind = kind;
        _dimensions = query.Length;
        if (codec == VectorQuantization.OneBit)
        {
            int bytes = (query.Length + 63) / 64 * 8;
            if (_queryBits.Length < bytes)
            {
                _queryBits = new byte[bytes];
            }

            Span<byte> bits = _queryBits.AsSpan(0, bytes);
            bits.Clear();
            for (int i = 0; i < query.Length; i++)
            {
                if (query[i] > 0)
                {
                    bits[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            return;
        }

        if (_scratch.Length < query.Length)
        {
            _scratch = new byte[query.Length];
        }

        // The query is compressed to 8 bits whatever the document codec: the extra precision costs one wider kernel input and is
        // where most of the quantization error of an asymmetric comparison would otherwise come from.
        int sum = Quantizers.Osq(query, 8, _scratch, out float lower, out float step);
        int length = codec == VectorQuantization.Scalar8 ? query.Length : 2 * ((query.Length + 1) / 2);
        _packed = (query.Length + 1) / 2;
        if (_query.Length < length)
        {
            _query = new sbyte[length];
        }

        Array.Clear(_query, 0, length);
        for (int i = 0; i < query.Length; i++)
        {
            _query[i] = (sbyte)(_scratch[i] - 128);
        }

        _queryLower = lower + (128.0 * step);
        _queryStep = step;
        _querySum = sum - (128.0 * query.Length);
        _queryNorm = VectorKernels.SumSquares(query);
    }

    /// <inheritdoc/>
    public override float ToQuery(int ordinal)
    {
        ReadOnlySpan<byte> record = _values.Get(ordinal);
        if (_codec == VectorQuantization.OneBit)
        {
            return QuantizedKernels.Hamming(record, _queryBits.AsSpan(0, record.Length));
        }

        int d = _dimensions;
        int codesLength = _codec == VectorQuantization.Scalar8 ? d : _packed;
        ReadOnlySpan<byte> corrections = record[codesLength..];
        double lower = BinaryPrimitives.ReadSingleLittleEndian(corrections);
        double step = BinaryPrimitives.ReadSingleLittleEndian(corrections[4..]);
        double sum = BinaryPrimitives.ReadInt32LittleEndian(corrections[8..]);
        double norm = BinaryPrimitives.ReadSingleLittleEndian(corrections[12..]);
        int dot = _codec == VectorQuantization.Scalar8
            ? QuantizedKernels.DotInt8(record[..d], _query.AsSpan(0, d))
            : QuantizedKernels.DotInt4(record[..codesLength], _query.AsSpan(0, codesLength), _query.AsSpan(codesLength, codesLength));

        // Expand (a_x + s_x X)(a_y + s_y Y) over the d components.
        double estimate = (d * lower * _queryLower) + (lower * _queryStep * _querySum) + (_queryLower * step * sum) + (step * _queryStep * dot);
        return _kind == DistanceKind.NegativeDot ? (float)-estimate : (float)(norm + _queryNorm - (2 * estimate));
    }

    /// <inheritdoc/>
    public override float Between(int a, int b) => throw new NotSupportedException("Compressed vectors are searched, never built from.");
}
