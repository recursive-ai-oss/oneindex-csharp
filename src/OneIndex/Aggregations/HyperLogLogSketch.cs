using System.Numerics;

namespace OneIndex.Aggregations;

/// <summary>
/// HyperLogLog++ (Heule, Nunkesser and Hall, "HyperLogLog in Practice", EDBT 2013): a 64-bit hash, so no large-range correction;
/// a sparse representation that keeps the distinct hashes themselves while few (exact counts for small sets); and dense registers
/// after that. Two deliberate differences from the paper, see docs/aggregations.md: the dense estimate uses the closed-form improved
/// estimator of Ertl (2017) instead of the paper's empirical bias tables, and the sparse form holds whole hashes rather than
/// packed 25-bit codes. Merging two sketches of the same precision gives the sketch of the union.
/// </summary>
internal sealed class HyperLogLogSketch
{
    private readonly int _p;
    private readonly int _m;
    private HashSet<ulong>? _sparse;
    private byte[]? _registers;

    public HyperLogLogSketch(int precision)
    {
        if (precision is < 4 or > 18)
        {
            throw new ArgumentOutOfRangeException(nameof(precision), "Precision must be from 4 to 18.");
        }

        _p = precision;
        _m = 1 << precision;
        _sparse = [];
    }

    public int Precision => _p;

    // Sparse entries cost 8 bytes each; switch when they would outweigh the m one-byte registers.
    private int SparseLimit => _m >> 3;

    /// <summary>A well-mixed 64-bit hash of an integer (the finalizer of SplitMix64, Steele, Lea and Flood 2014).</summary>
    public static ulong Hash(long value)
    {
        ulong z = (ulong)value + 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>A well-mixed 64-bit hash of a byte string: 8 bytes at a time, then the SplitMix64 finalizer.</summary>
    public static ulong Hash(ReadOnlySpan<byte> bytes)
    {
        ulong h = 0xCBF29CE484222325UL ^ (ulong)bytes.Length;
        int i = 0;
        for (; i + 8 <= bytes.Length; i += 8)
        {
            h = BitOperations.RotateLeft((h ^ System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..])) * 0x9FB21C651E98DF25UL, 29);
        }

        ulong tail = 0;
        for (int shift = 0; i < bytes.Length; i++, shift += 8)
        {
            tail |= (ulong)bytes[i] << shift;
        }

        h = BitOperations.RotateLeft((h ^ tail) * 0x9FB21C651E98DF25UL, 29);
        return Hash((long)h);
    }

    public void AddHash(ulong hash)
    {
        if (_sparse is { } sparse)
        {
            sparse.Add(hash);
            if (sparse.Count > SparseLimit)
            {
                ToDense();
            }

            return;
        }

        SetRegister(hash);
    }

    public void Merge(HyperLogLogSketch other)
    {
        if (other._p != _p)
        {
            throw new ArgumentException("Sketches of different precision cannot be merged.", nameof(other));
        }

        if (other._sparse is { } os)
        {
            foreach (ulong h in os)
            {
                AddHash(h);
            }

            return;
        }

        if (_sparse is not null)
        {
            ToDense();
        }

        byte[] mine = _registers!;
        byte[] theirs = other._registers!;
        for (int i = 0; i < mine.Length; i++)
        {
            if (theirs[i] > mine[i])
            {
                mine[i] = theirs[i];
            }
        }
    }

    public long Estimate()
    {
        if (_sparse is { } sparse)
        {
            return sparse.Count;
        }

        // Ertl 2017, Algorithm 6: the estimate from the histogram C[k] of register values, k = 0..q+1.
        int q = 64 - _p;
        Span<int> c = stackalloc int[q + 2]; // at most 66 entries
        foreach (byte r in _registers!)
        {
            c[r]++;
        }

        double m = _m;
        double z = m * Tau(1.0 - (c[q + 1] / m));
        for (int k = q; k >= 1; k--)
        {
            z = 0.5 * (z + c[k]);
        }

        z += m * Sigma(c[0] / m);
        const double AlphaInfinity = 0.5 / 0.6931471805599453; // 1 / (2 ln 2)
        return (long)Math.Round(AlphaInfinity * m * m / z);
    }

    private void ToDense()
    {
        HashSet<ulong> sparse = _sparse!;
        _sparse = null;
        _registers = new byte[_m];
        foreach (ulong h in sparse)
        {
            SetRegister(h);
        }
    }

    private void SetRegister(ulong hash)
    {
        // The top p bits pick the register; the rank is the position of the first 1 bit among the other 64 - p bits (1-based,
        // 64 - p + 1 when they are all zero).
        int index = (int)(hash >> (64 - _p));
        ulong rest = hash << _p;
        byte rank = rest == 0 ? (byte)(64 - _p + 1) : (byte)(BitOperations.LeadingZeroCount(rest) + 1);
        if (rank > _registers![index])
        {
            _registers[index] = rank;
        }
    }

    private static double Sigma(double x)
    {
        if (x == 1.0)
        {
            return double.PositiveInfinity;
        }

        double y = 1.0;
        double z = x;
        double previous;
        do
        {
            x *= x;
            previous = z;
            z += x * y;
            y += y;
        }
        while (z != previous);
        return z;
    }

    private static double Tau(double x)
    {
        if (x is 0.0 or 1.0)
        {
            return 0.0;
        }

        double y = 1.0;
        double z = 1.0 - x;
        double previous;
        do
        {
            x = Math.Sqrt(x);
            previous = z;
            y *= 0.5;
            z -= (1.0 - x) * (1.0 - x) * y;
        }
        while (z != previous);
        return z / 3.0;
    }
}
