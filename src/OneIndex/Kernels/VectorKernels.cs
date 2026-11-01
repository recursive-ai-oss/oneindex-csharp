using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneIndex.Kernels;

/// <summary>The instruction width a kernel runs with. Every width gives the same result bit for bit.</summary>
internal enum KernelIsa : byte
{
    /// <summary>Plain loops with sixteen accumulators.</summary>
    Scalar = 0,

    /// <summary>128-bit vectors: SSE, AdvSimd, WebAssembly SIMD.</summary>
    Vector128 = 1,

    /// <summary>256-bit vectors: AVX2.</summary>
    Vector256 = 2,

    /// <summary>512-bit vectors: AVX-512.</summary>
    Vector512 = 3,
}

/// <summary>
/// Distance kernels over contiguous vectors: dot product, squared Euclidean distance and sums of squares for 32-bit floats, and
/// integer versions for signed bytes. This namespace depends on nothing else in the engine, so it can be lifted as it stands.
/// </summary>
/// <remarks>
/// <para>
/// <b>One result on every machine.</b> A float sum depends on the order of its additions, and a graph built from distances is
/// only reproducible if the distances are. So the order is fixed by the kernel, not by the instruction set: element <c>i</c> is
/// added to accumulator lane <c>i mod 16</c>, in index order, with a separate multiply and add (never a fused multiply-add, whose
/// single rounding would differ from machine to machine), and the sixteen lanes are then folded by halves: lane <c>j</c> with
/// lane <c>j+8</c>, then <c>j+4</c>, <c>j+2</c> and <c>j+1</c>. A 512-bit register is those sixteen lanes, a 256-bit path keeps two
/// registers, a 128-bit path four, the scalar path sixteen variables. A vector whose length is not a multiple of 16 is padded
/// with zeros for its last block, which adds exactly zero. The paths therefore agree exactly, which the tests assert.
/// </para>
/// <para>
/// <b>Special values.</b> The kernels do no flushing or clamping: denormals take part at full precision (the .NET runtime keeps
/// the default floating-point modes on x64 and arm64), infinities follow IEEE 754, and a NaN in either input gives a NaN result
/// (its payload is unspecified). Callers reject non-finite vectors before indexing them.
/// </para>
/// <para>
/// <b>Bounds.</b> Each entry point checks that both spans have the same length once; the inner loops then read through
/// <c>ref</c> offsets that stay inside that length (the loop condition is <c>i + 16 &lt;= length</c> and the last partial block
/// is copied into a 16-element stack buffer). The stack buffers are fixed at 16 floats.
/// </para>
/// <para>
/// The widest hardware-accelerated width is chosen once; <see cref="Vector512.IsHardwareAccelerated"/> is false on processors
/// where the runtime judges 512-bit execution slower, which is the behaviour wanted here.
/// </para>
/// </remarks>
internal static class VectorKernels
{
    private const int Lanes = 16;

    /// <summary>The instruction width the unqualified entry points use on this machine.</summary>
    public static readonly KernelIsa Best =
        Vector512.IsHardwareAccelerated ? KernelIsa.Vector512
        : Vector256.IsHardwareAccelerated ? KernelIsa.Vector256
        : Vector128.IsHardwareAccelerated ? KernelIsa.Vector128
        : KernelIsa.Scalar;

    /// <summary>Whether a width can run on this machine.</summary>
    /// <param name="isa">The width.</param>
    /// <returns><see langword="true"/> when the hardware accelerates it.</returns>
    public static bool IsSupported(KernelIsa isa) => isa switch
    {
        KernelIsa.Scalar => true,
        KernelIsa.Vector128 => Vector128.IsHardwareAccelerated,
        KernelIsa.Vector256 => Vector256.IsHardwareAccelerated,
        _ => Vector512.IsHardwareAccelerated,
    };

    /// <summary>The dot product of two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The sum of the products, in the order the type's remarks describe.</returns>
    /// <exception cref="ArgumentException">The lengths differ.</exception>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => Dot(Best, a, b);

    /// <summary>The squared Euclidean distance between two vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The sum of squared differences.</returns>
    /// <exception cref="ArgumentException">The lengths differ.</exception>
    public static float SquaredL2(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => SquaredL2(Best, a, b);

    /// <summary>The sum of squares of a vector (its squared length).</summary>
    /// <param name="a">The vector.</param>
    /// <returns>The sum of squares.</returns>
    public static float SumSquares(ReadOnlySpan<float> a) => Dot(Best, a, a);

    /// <summary>Scales a vector to length 1 in place. A vector of zero length or with a non-finite length is left alone.</summary>
    /// <param name="vector">The vector.</param>
    /// <returns>The length before scaling.</returns>
    public static float Normalize(Span<float> vector)
    {
        float norm = MathF.Sqrt(SumSquares(vector));
        if (norm > 0f && float.IsFinite(norm))
        {
            float inverse = 1f / norm;
            for (int i = 0; i < vector.Length; i++)
            {
                vector[i] *= inverse;
            }
        }

        return norm;
    }

    /// <summary>The dot product on a chosen width (tests and benchmarks).</summary>
    /// <param name="isa">The width; must be supported.</param>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The dot product.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(KernelIsa isa, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            ThrowLength();
        }

        return isa switch
        {
            KernelIsa.Vector512 => DotV512(a, b),
            KernelIsa.Vector256 => DotV256(a, b),
            KernelIsa.Vector128 => DotV128(a, b),
            _ => DotScalar(a, b),
        };
    }

    /// <summary>The squared Euclidean distance on a chosen width (tests and benchmarks).</summary>
    /// <param name="isa">The width; must be supported.</param>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The squared distance.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float SquaredL2(KernelIsa isa, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            ThrowLength();
        }

        return isa switch
        {
            KernelIsa.Vector512 => L2V512(a, b),
            KernelIsa.Vector256 => L2V256(a, b),
            KernelIsa.Vector128 => L2V128(a, b),
            _ => L2Scalar(a, b),
        };
    }

    /// <summary>The dot product of two signed-byte vectors. Integer sums are exact, so every width agrees.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The sum of the products. At most 131,071 elements keep it from overflowing; longer vectors throw.</returns>
    /// <exception cref="ArgumentException">The lengths differ, or the vectors are longer than 131,071 elements.</exception>
    public static int DotInt8(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => DotInt8(Best, a, b);

    /// <summary>The squared Euclidean distance of two signed-byte vectors.</summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector; the same length.</param>
    /// <returns>The sum of squared differences; the vectors may be at most 32,768 elements long.</returns>
    /// <exception cref="ArgumentException">The lengths differ, or the vectors are too long.</exception>
    public static int SquaredL2Int8(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b) => SquaredL2Int8(Best, a, b);

    /// <summary>The signed-byte dot product on a chosen width.</summary>
    /// <param name="isa">The width.</param>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The dot product.</returns>
    public static int DotInt8(KernelIsa isa, ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        if (a.Length != b.Length || a.Length > 131_071)
        {
            ThrowLength();
        }

        if (isa >= KernelIsa.Vector256 && Vector256.IsHardwareAccelerated)
        {
            return DotI8V256(a, b);
        }

        if (isa >= KernelIsa.Vector128 && Vector128.IsHardwareAccelerated)
        {
            return DotI8V128(a, b);
        }

        int sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    /// <summary>The signed-byte squared distance on a chosen width.</summary>
    /// <param name="isa">The width.</param>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>The squared distance.</returns>
    public static int SquaredL2Int8(KernelIsa isa, ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        if (a.Length != b.Length || a.Length > 32_768)
        {
            ThrowLength();
        }

        if (isa >= KernelIsa.Vector256 && Vector256.IsHardwareAccelerated)
        {
            return L2I8V256(a, b);
        }

        int sum = 0;
        for (int i = 0; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }

        return sum;
    }

    // Folds sixteen lane sums by halves; the same tree the vector paths use.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fold(ReadOnlySpan<float> l)
    {
        float t0 = l[0] + l[8];
        float t1 = l[1] + l[9];
        float t2 = l[2] + l[10];
        float t3 = l[3] + l[11];
        float t4 = l[4] + l[12];
        float t5 = l[5] + l[13];
        float t6 = l[6] + l[14];
        float t7 = l[7] + l[15];
        float u0 = t0 + t4;
        float u1 = t1 + t5;
        float u2 = t2 + t6;
        float u3 = t3 + t7;
        float v0 = u0 + u2;
        float v1 = u1 + u3;
        return v0 + v1;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fold(Vector128<float> t4)
    {
        // t4 holds u0..u3.
        float v0 = t4.GetElement(0) + t4.GetElement(2);
        float v1 = t4.GetElement(1) + t4.GetElement(3);
        return v0 + v1;
    }

    private static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> lane = stackalloc float[Lanes]; // fixed 16 floats
        lane.Clear();
        for (int i = 0; i < a.Length; i++)
        {
            lane[i & 15] += a[i] * b[i];
        }

        return Fold(lane);
    }

    private static float L2Scalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> lane = stackalloc float[Lanes]; // fixed 16 floats
        lane.Clear();
        for (int i = 0; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            lane[i & 15] += d * d;
        }

        return Fold(lane);
    }

    private static float DotV128(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector128<float> s0 = Vector128<float>.Zero, s1 = s0, s2 = s0, s3 = s0;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            s0 += Vector128.LoadUnsafe(ref ra, o) * Vector128.LoadUnsafe(ref rb, o);
            s1 += Vector128.LoadUnsafe(ref ra, o + 4) * Vector128.LoadUnsafe(ref rb, o + 4);
            s2 += Vector128.LoadUnsafe(ref ra, o + 8) * Vector128.LoadUnsafe(ref rb, o + 8);
            s3 += Vector128.LoadUnsafe(ref ra, o + 12) * Vector128.LoadUnsafe(ref rb, o + 12);
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats: the padded last block
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            s0 += Vector128.Create<float>(ta) * Vector128.Create<float>(tb);
            s1 += Vector128.Create<float>(ta[4..]) * Vector128.Create<float>(tb[4..]);
            s2 += Vector128.Create<float>(ta[8..]) * Vector128.Create<float>(tb[8..]);
            s3 += Vector128.Create<float>(ta[12..]) * Vector128.Create<float>(tb[12..]);
        }

        return Fold((s0 + s2) + (s1 + s3));
    }

    private static float L2V128(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector128<float> s0 = Vector128<float>.Zero, s1 = s0, s2 = s0, s3 = s0;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            Vector128<float> d0 = Vector128.LoadUnsafe(ref ra, o) - Vector128.LoadUnsafe(ref rb, o);
            Vector128<float> d1 = Vector128.LoadUnsafe(ref ra, o + 4) - Vector128.LoadUnsafe(ref rb, o + 4);
            Vector128<float> d2 = Vector128.LoadUnsafe(ref ra, o + 8) - Vector128.LoadUnsafe(ref rb, o + 8);
            Vector128<float> d3 = Vector128.LoadUnsafe(ref ra, o + 12) - Vector128.LoadUnsafe(ref rb, o + 12);
            s0 += d0 * d0;
            s1 += d1 * d1;
            s2 += d2 * d2;
            s3 += d3 * d3;
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            Vector128<float> d0 = Vector128.Create<float>(ta) - Vector128.Create<float>(tb);
            Vector128<float> d1 = Vector128.Create<float>(ta[4..]) - Vector128.Create<float>(tb[4..]);
            Vector128<float> d2 = Vector128.Create<float>(ta[8..]) - Vector128.Create<float>(tb[8..]);
            Vector128<float> d3 = Vector128.Create<float>(ta[12..]) - Vector128.Create<float>(tb[12..]);
            s0 += d0 * d0;
            s1 += d1 * d1;
            s2 += d2 * d2;
            s3 += d3 * d3;
        }

        return Fold((s0 + s2) + (s1 + s3));
    }

    private static float DotV256(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector256<float> s0 = Vector256<float>.Zero, s1 = s0;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            s0 += Vector256.LoadUnsafe(ref ra, o) * Vector256.LoadUnsafe(ref rb, o);
            s1 += Vector256.LoadUnsafe(ref ra, o + 8) * Vector256.LoadUnsafe(ref rb, o + 8);
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            s0 += Vector256.Create<float>(ta) * Vector256.Create<float>(tb);
            s1 += Vector256.Create<float>(ta[8..]) * Vector256.Create<float>(tb[8..]);
        }

        Vector256<float> t8 = s0 + s1;
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float L2V256(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector256<float> s0 = Vector256<float>.Zero, s1 = s0;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            Vector256<float> d0 = Vector256.LoadUnsafe(ref ra, o) - Vector256.LoadUnsafe(ref rb, o);
            Vector256<float> d1 = Vector256.LoadUnsafe(ref ra, o + 8) - Vector256.LoadUnsafe(ref rb, o + 8);
            s0 += d0 * d0;
            s1 += d1 * d1;
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            Vector256<float> d0 = Vector256.Create<float>(ta) - Vector256.Create<float>(tb);
            Vector256<float> d1 = Vector256.Create<float>(ta[8..]) - Vector256.Create<float>(tb[8..]);
            s0 += d0 * d0;
            s1 += d1 * d1;
        }

        Vector256<float> t8 = s0 + s1;
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float DotV512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector512<float> s = Vector512<float>.Zero;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            s += Vector512.LoadUnsafe(ref ra, o) * Vector512.LoadUnsafe(ref rb, o);
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            s += Vector512.Create<float>(ta) * Vector512.Create<float>(tb);
        }

        Vector256<float> t8 = s.GetLower() + s.GetUpper();
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float L2V512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector512<float> s = Vector512<float>.Zero;
        int i = 0;
        for (; i + Lanes <= a.Length; i += Lanes)
        {
            nuint o = (uint)i;
            Vector512<float> d = Vector512.LoadUnsafe(ref ra, o) - Vector512.LoadUnsafe(ref rb, o);
            s += d * d;
        }

        if (i < a.Length)
        {
            Span<float> ta = stackalloc float[Lanes]; // fixed 16 floats
            Span<float> tb = stackalloc float[Lanes];
            ta.Clear();
            tb.Clear();
            a[i..].CopyTo(ta);
            b[i..].CopyTo(tb);
            Vector512<float> d = Vector512.Create<float>(ta) - Vector512.Create<float>(tb);
            s += d * d;
        }

        Vector256<float> t8 = s.GetLower() + s.GetUpper();
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    // Integer kernels: widen to 16 bits, multiply (a product of two signed bytes is at most 16384, which fits), widen to 32 bits.
    private static int DotI8V128(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        ref sbyte ra = ref MemoryMarshal.GetReference(a);
        ref sbyte rb = ref MemoryMarshal.GetReference(b);
        Vector128<int> acc = Vector128<int>.Zero;
        int i = 0;
        for (; i + 16 <= a.Length; i += 16)
        {
            Vector128<sbyte> x = Vector128.LoadUnsafe(ref ra, (nuint)i);
            Vector128<sbyte> y = Vector128.LoadUnsafe(ref rb, (nuint)i);
            Vector128<short> lo = Vector128.WidenLower(x) * Vector128.WidenLower(y);
            Vector128<short> hi = Vector128.WidenUpper(x) * Vector128.WidenUpper(y);
            acc += Vector128.WidenLower(lo) + Vector128.WidenUpper(lo) + Vector128.WidenLower(hi) + Vector128.WidenUpper(hi);
        }

        int sum = Vector128.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static int DotI8V256(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        ref sbyte ra = ref MemoryMarshal.GetReference(a);
        ref sbyte rb = ref MemoryMarshal.GetReference(b);
        Vector256<int> acc = Vector256<int>.Zero;
        int i = 0;
        for (; i + 32 <= a.Length; i += 32)
        {
            Vector256<sbyte> x = Vector256.LoadUnsafe(ref ra, (nuint)i);
            Vector256<sbyte> y = Vector256.LoadUnsafe(ref rb, (nuint)i);
            Vector256<short> lo = Vector256.WidenLower(x) * Vector256.WidenLower(y);
            Vector256<short> hi = Vector256.WidenUpper(x) * Vector256.WidenUpper(y);
            acc += Vector256.WidenLower(lo) + Vector256.WidenUpper(lo) + Vector256.WidenLower(hi) + Vector256.WidenUpper(hi);
        }

        int sum = Vector256.Sum(acc);
        for (; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }

    private static int L2I8V256(ReadOnlySpan<sbyte> a, ReadOnlySpan<sbyte> b)
    {
        ref sbyte ra = ref MemoryMarshal.GetReference(a);
        ref sbyte rb = ref MemoryMarshal.GetReference(b);
        Vector256<int> acc = Vector256<int>.Zero;
        int i = 0;
        for (; i + 32 <= a.Length; i += 32)
        {
            Vector256<sbyte> x = Vector256.LoadUnsafe(ref ra, (nuint)i);
            Vector256<sbyte> y = Vector256.LoadUnsafe(ref rb, (nuint)i);
            // A difference of two signed bytes is at most 255 in magnitude; its square (65025) does not fit a signed short, so square in 32 bits.
            Vector256<short> dl = Vector256.WidenLower(x) - Vector256.WidenLower(y);
            Vector256<short> dh = Vector256.WidenUpper(x) - Vector256.WidenUpper(y);
            Vector256<int> a0 = Vector256.WidenLower(dl);
            Vector256<int> a1 = Vector256.WidenUpper(dl);
            Vector256<int> a2 = Vector256.WidenLower(dh);
            Vector256<int> a3 = Vector256.WidenUpper(dh);
            acc += (a0 * a0) + (a1 * a1) + (a2 * a2) + (a3 * a3);
        }

        int sum = Vector256.Sum(acc);
        for (; i < a.Length; i++)
        {
            int d = a[i] - b[i];
            sum += d * d;
        }

        return sum;
    }

    private static void ThrowLength() => throw new ArgumentException("The vectors must have the same length, within the kernel's limit.");
}
