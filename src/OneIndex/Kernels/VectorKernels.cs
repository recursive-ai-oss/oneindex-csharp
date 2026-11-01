using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneIndex.Kernels;

/// <summary>The instruction width a kernel runs with. Every width gives the same result bit for bit.</summary>
internal enum KernelIsa : byte
{
    /// <summary>Plain loops with sixty-four accumulators.</summary>
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
/// added to accumulator lane <c>i mod 64</c>, in index order, with a separate multiply and add (never a fused multiply-add, whose
/// single rounding would differ from machine to machine), and the sixty-four lanes are then folded by halves: lane <c>j</c> with
/// lane <c>j+32</c>, then <c>j+16</c>, <c>j+8</c>, <c>j+4</c>, <c>j+2</c> and <c>j+1</c>. A 512-bit path keeps four registers of
/// those lanes, a 256-bit path eight, a 128-bit path sixteen, the scalar path sixty-four variables. Several independent accumulators
/// are what lets the processor overlap the additions, which is why there are sixty-four lanes and not sixteen. A vector whose
/// length is not a multiple of 64 is padded with zeros for its last block, which adds exactly zero. The paths therefore agree
/// exactly, which the tests assert.
/// </para>
/// <para>
/// <b>Special values.</b> The kernels do no flushing or clamping: denormals take part at full precision (the .NET runtime keeps
/// the default floating-point modes on x64 and arm64), infinities follow IEEE 754, and a NaN in either input gives a NaN result
/// (its payload is unspecified). Callers reject non-finite vectors before indexing them.
/// </para>
/// <para>
/// <b>Bounds.</b> Each entry point checks that both spans have the same length once; the inner loops then read through
/// <c>ref</c> offsets that stay inside that length (block <c>k</c> reads elements <c>64k</c> to <c>64k+63</c>, and the last partial
/// block is copied into a 64-element stack buffer). The stack buffers are fixed at 64 floats.
/// </para>
/// <para>
/// The widest hardware-accelerated width is chosen once; <see cref="Vector512.IsHardwareAccelerated"/> is false on processors
/// where the runtime judges 512-bit execution slower, which is the behaviour wanted here.
/// </para>
/// </remarks>
internal static class VectorKernels
{
    private const int Lanes = 64;

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

    // Folds sixteen lane sums by halves; the same tree the vector paths use after reaching sixteen lanes.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fold16(ReadOnlySpan<float> l)
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

    // Folds the sixty-four lane sums: lane j with j+32, then j+16, and the sixteen left with Fold16.
    private static float Fold64(Span<float> l)
    {
        for (int j = 0; j < 32; j++)
        {
            l[j] += l[j + 32];
        }

        for (int j = 0; j < 16; j++)
        {
            l[j] += l[j + 16];
        }

        return Fold16(l);
    }

    // Four lanes to one: lane 0 with 2, lane 1 with 3, then the two.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Fold(Vector128<float> t4)
    {
        float v0 = t4.GetElement(0) + t4.GetElement(2);
        float v1 = t4.GetElement(1) + t4.GetElement(3);
        return v0 + v1;
    }

    private static float DotScalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> lane = stackalloc float[Lanes]; // fixed 64 floats
        lane.Clear();
        for (int i = 0; i < a.Length; i++)
        {
            lane[i & 63] += a[i] * b[i];
        }

        return Fold64(lane);
    }

    private static float L2Scalar(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        Span<float> lane = stackalloc float[Lanes]; // fixed 64 floats
        lane.Clear();
        for (int i = 0; i < a.Length; i++)
        {
            float d = a[i] - b[i];
            lane[i & 63] += d * d;
        }

        return Fold64(lane);
    }

    private static float DotV128(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector128<float> s0 = Vector128<float>.Zero, s1 = s0, s2 = s0, s3 = s0, s4 = s0, s5 = s0, s6 = s0, s7 = s0, s8 = s0, s9 = s0, s10 = s0, s11 = s0, s12 = s0, s13 = s0, s14 = s0, s15 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            s0 += Vector128.LoadUnsafe(ref xa, 0) * Vector128.LoadUnsafe(ref xb, 0);
            s1 += Vector128.LoadUnsafe(ref xa, 4) * Vector128.LoadUnsafe(ref xb, 4);
            s2 += Vector128.LoadUnsafe(ref xa, 8) * Vector128.LoadUnsafe(ref xb, 8);
            s3 += Vector128.LoadUnsafe(ref xa, 12) * Vector128.LoadUnsafe(ref xb, 12);
            s4 += Vector128.LoadUnsafe(ref xa, 16) * Vector128.LoadUnsafe(ref xb, 16);
            s5 += Vector128.LoadUnsafe(ref xa, 20) * Vector128.LoadUnsafe(ref xb, 20);
            s6 += Vector128.LoadUnsafe(ref xa, 24) * Vector128.LoadUnsafe(ref xb, 24);
            s7 += Vector128.LoadUnsafe(ref xa, 28) * Vector128.LoadUnsafe(ref xb, 28);
            s8 += Vector128.LoadUnsafe(ref xa, 32) * Vector128.LoadUnsafe(ref xb, 32);
            s9 += Vector128.LoadUnsafe(ref xa, 36) * Vector128.LoadUnsafe(ref xb, 36);
            s10 += Vector128.LoadUnsafe(ref xa, 40) * Vector128.LoadUnsafe(ref xb, 40);
            s11 += Vector128.LoadUnsafe(ref xa, 44) * Vector128.LoadUnsafe(ref xb, 44);
            s12 += Vector128.LoadUnsafe(ref xa, 48) * Vector128.LoadUnsafe(ref xb, 48);
            s13 += Vector128.LoadUnsafe(ref xa, 52) * Vector128.LoadUnsafe(ref xb, 52);
            s14 += Vector128.LoadUnsafe(ref xa, 56) * Vector128.LoadUnsafe(ref xb, 56);
            s15 += Vector128.LoadUnsafe(ref xa, 60) * Vector128.LoadUnsafe(ref xb, 60);
        }

        // 64 lanes to 32 to 16 to 8 to 4: lane j with j+32, then j+16, j+8 and j+4.
        Vector128<float> t0 = s0 + s8, t1 = s1 + s9, t2 = s2 + s10, t3 = s3 + s11, t4 = s4 + s12, t5 = s5 + s13, t6 = s6 + s14, t7 = s7 + s15;
        Vector128<float> u0 = t0 + t4, u1 = t1 + t5, u2 = t2 + t6, u3 = t3 + t7;
        return Fold(((u0 + u2) + (u1 + u3)));
    }

    private static float L2V128(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector128<float> s0 = Vector128<float>.Zero, s1 = s0, s2 = s0, s3 = s0, s4 = s0, s5 = s0, s6 = s0, s7 = s0, s8 = s0, s9 = s0, s10 = s0, s11 = s0, s12 = s0, s13 = s0, s14 = s0, s15 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            Vector128<float> d0 = Vector128.LoadUnsafe(ref xa, 0) - Vector128.LoadUnsafe(ref xb, 0);
            Vector128<float> d1 = Vector128.LoadUnsafe(ref xa, 4) - Vector128.LoadUnsafe(ref xb, 4);
            Vector128<float> d2 = Vector128.LoadUnsafe(ref xa, 8) - Vector128.LoadUnsafe(ref xb, 8);
            Vector128<float> d3 = Vector128.LoadUnsafe(ref xa, 12) - Vector128.LoadUnsafe(ref xb, 12);
            Vector128<float> d4 = Vector128.LoadUnsafe(ref xa, 16) - Vector128.LoadUnsafe(ref xb, 16);
            Vector128<float> d5 = Vector128.LoadUnsafe(ref xa, 20) - Vector128.LoadUnsafe(ref xb, 20);
            Vector128<float> d6 = Vector128.LoadUnsafe(ref xa, 24) - Vector128.LoadUnsafe(ref xb, 24);
            Vector128<float> d7 = Vector128.LoadUnsafe(ref xa, 28) - Vector128.LoadUnsafe(ref xb, 28);
            Vector128<float> d8 = Vector128.LoadUnsafe(ref xa, 32) - Vector128.LoadUnsafe(ref xb, 32);
            Vector128<float> d9 = Vector128.LoadUnsafe(ref xa, 36) - Vector128.LoadUnsafe(ref xb, 36);
            Vector128<float> d10 = Vector128.LoadUnsafe(ref xa, 40) - Vector128.LoadUnsafe(ref xb, 40);
            Vector128<float> d11 = Vector128.LoadUnsafe(ref xa, 44) - Vector128.LoadUnsafe(ref xb, 44);
            Vector128<float> d12 = Vector128.LoadUnsafe(ref xa, 48) - Vector128.LoadUnsafe(ref xb, 48);
            Vector128<float> d13 = Vector128.LoadUnsafe(ref xa, 52) - Vector128.LoadUnsafe(ref xb, 52);
            Vector128<float> d14 = Vector128.LoadUnsafe(ref xa, 56) - Vector128.LoadUnsafe(ref xb, 56);
            Vector128<float> d15 = Vector128.LoadUnsafe(ref xa, 60) - Vector128.LoadUnsafe(ref xb, 60);
            s0 += d0 * d0;
            s1 += d1 * d1;
            s2 += d2 * d2;
            s3 += d3 * d3;
            s4 += d4 * d4;
            s5 += d5 * d5;
            s6 += d6 * d6;
            s7 += d7 * d7;
            s8 += d8 * d8;
            s9 += d9 * d9;
            s10 += d10 * d10;
            s11 += d11 * d11;
            s12 += d12 * d12;
            s13 += d13 * d13;
            s14 += d14 * d14;
            s15 += d15 * d15;
        }

        // 64 lanes to 32 to 16 to 8 to 4: lane j with j+32, then j+16, j+8 and j+4.
        Vector128<float> t0 = s0 + s8, t1 = s1 + s9, t2 = s2 + s10, t3 = s3 + s11, t4 = s4 + s12, t5 = s5 + s13, t6 = s6 + s14, t7 = s7 + s15;
        Vector128<float> u0 = t0 + t4, u1 = t1 + t5, u2 = t2 + t6, u3 = t3 + t7;
        return Fold(((u0 + u2) + (u1 + u3)));
    }

    private static float DotV256(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector256<float> s0 = Vector256<float>.Zero, s1 = s0, s2 = s0, s3 = s0, s4 = s0, s5 = s0, s6 = s0, s7 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            s0 += Vector256.LoadUnsafe(ref xa, 0) * Vector256.LoadUnsafe(ref xb, 0);
            s1 += Vector256.LoadUnsafe(ref xa, 8) * Vector256.LoadUnsafe(ref xb, 8);
            s2 += Vector256.LoadUnsafe(ref xa, 16) * Vector256.LoadUnsafe(ref xb, 16);
            s3 += Vector256.LoadUnsafe(ref xa, 24) * Vector256.LoadUnsafe(ref xb, 24);
            s4 += Vector256.LoadUnsafe(ref xa, 32) * Vector256.LoadUnsafe(ref xb, 32);
            s5 += Vector256.LoadUnsafe(ref xa, 40) * Vector256.LoadUnsafe(ref xb, 40);
            s6 += Vector256.LoadUnsafe(ref xa, 48) * Vector256.LoadUnsafe(ref xb, 48);
            s7 += Vector256.LoadUnsafe(ref xa, 56) * Vector256.LoadUnsafe(ref xb, 56);
        }

        // 64 lanes to 32 to 16 to 8: lane j with j+32, then j+16, then j+8.
        Vector256<float> t0 = s0 + s4, t1 = s1 + s5, t2 = s2 + s6, t3 = s3 + s7;
        Vector256<float> t8 = (t0 + t2) + (t1 + t3);
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float L2V256(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector256<float> s0 = Vector256<float>.Zero, s1 = s0, s2 = s0, s3 = s0, s4 = s0, s5 = s0, s6 = s0, s7 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            Vector256<float> d0 = Vector256.LoadUnsafe(ref xa, 0) - Vector256.LoadUnsafe(ref xb, 0);
            Vector256<float> d1 = Vector256.LoadUnsafe(ref xa, 8) - Vector256.LoadUnsafe(ref xb, 8);
            Vector256<float> d2 = Vector256.LoadUnsafe(ref xa, 16) - Vector256.LoadUnsafe(ref xb, 16);
            Vector256<float> d3 = Vector256.LoadUnsafe(ref xa, 24) - Vector256.LoadUnsafe(ref xb, 24);
            Vector256<float> d4 = Vector256.LoadUnsafe(ref xa, 32) - Vector256.LoadUnsafe(ref xb, 32);
            Vector256<float> d5 = Vector256.LoadUnsafe(ref xa, 40) - Vector256.LoadUnsafe(ref xb, 40);
            Vector256<float> d6 = Vector256.LoadUnsafe(ref xa, 48) - Vector256.LoadUnsafe(ref xb, 48);
            Vector256<float> d7 = Vector256.LoadUnsafe(ref xa, 56) - Vector256.LoadUnsafe(ref xb, 56);
            s0 += d0 * d0;
            s1 += d1 * d1;
            s2 += d2 * d2;
            s3 += d3 * d3;
            s4 += d4 * d4;
            s5 += d5 * d5;
            s6 += d6 * d6;
            s7 += d7 * d7;
        }

        // 64 lanes to 32 to 16 to 8: lane j with j+32, then j+16, then j+8.
        Vector256<float> t0 = s0 + s4, t1 = s1 + s5, t2 = s2 + s6, t3 = s3 + s7;
        Vector256<float> t8 = (t0 + t2) + (t1 + t3);
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float DotV512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector512<float> s0 = Vector512<float>.Zero, s1 = s0, s2 = s0, s3 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            s0 += Vector512.LoadUnsafe(ref xa, 0) * Vector512.LoadUnsafe(ref xb, 0);
            s1 += Vector512.LoadUnsafe(ref xa, 16) * Vector512.LoadUnsafe(ref xb, 16);
            s2 += Vector512.LoadUnsafe(ref xa, 32) * Vector512.LoadUnsafe(ref xb, 32);
            s3 += Vector512.LoadUnsafe(ref xa, 48) * Vector512.LoadUnsafe(ref xb, 48);
        }

        Vector512<float> r = (s0 + s2) + (s1 + s3);
        Vector256<float> t8 = r.GetLower() + r.GetUpper();
        return Fold(t8.GetLower() + t8.GetUpper());
    }

    private static float L2V512(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        ref float ra = ref MemoryMarshal.GetReference(a);
        ref float rb = ref MemoryMarshal.GetReference(b);
        Vector512<float> s0 = Vector512<float>.Zero, s1 = s0, s2 = s0, s3 = s0;
        int blocks = (a.Length + 63) >> 6;
        bool partial = (a.Length & 63) != 0;
        Span<float> ta = stackalloc float[64]; // fixed 64 floats: the zero-padded last block
        Span<float> tb = stackalloc float[64];
        if (partial)
        {
            ta.Clear();
            tb.Clear();
            a[((blocks - 1) << 6)..].CopyTo(ta);
            b[((blocks - 1) << 6)..].CopyTo(tb);
        }

        for (int block = 0; block < blocks; block++)
        {
            bool last = partial && block == blocks - 1;
            ref float xa = ref (last ? ref MemoryMarshal.GetReference(ta) : ref Unsafe.Add(ref ra, (nuint)(block << 6)));
            ref float xb = ref (last ? ref MemoryMarshal.GetReference(tb) : ref Unsafe.Add(ref rb, (nuint)(block << 6)));

            Vector512<float> d0 = Vector512.LoadUnsafe(ref xa, 0) - Vector512.LoadUnsafe(ref xb, 0);
            Vector512<float> d1 = Vector512.LoadUnsafe(ref xa, 16) - Vector512.LoadUnsafe(ref xb, 16);
            Vector512<float> d2 = Vector512.LoadUnsafe(ref xa, 32) - Vector512.LoadUnsafe(ref xb, 32);
            Vector512<float> d3 = Vector512.LoadUnsafe(ref xa, 48) - Vector512.LoadUnsafe(ref xb, 48);
            s0 += d0 * d0;
            s1 += d1 * d1;
            s2 += d2 * d2;
            s3 += d3 * d3;
        }

        Vector512<float> r = (s0 + s2) + (s1 + s3);
        Vector256<float> t8 = r.GetLower() + r.GetUpper();
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
