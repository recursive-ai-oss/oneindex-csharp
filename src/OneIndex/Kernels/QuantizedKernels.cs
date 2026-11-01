using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace OneIndex.Kernels;

/// <summary>
/// Kernels over compressed vectors: the dot product of a packed 4-bit vector with a signed-byte query, and the Hamming distance of
/// two bit vectors. Integer sums are exact, so every width agrees with the scalar path bit for bit. Like the float kernels, this
/// file depends on nothing else in the engine.
/// </summary>
/// <remarks>
/// <b>Bounds.</b> Each entry point checks the three lengths once. The vector paths then read 16 or 32 bytes at a time while at least
/// that many remain, and the scalar loop finishes the tail, so no read passes the end of a span.
/// </remarks>
internal static class QuantizedKernels
{
    /// <summary>
    /// The dot product of a 4-bit vector with a signed-byte vector. The packed vector holds <c>h</c> bytes: the low nibble of byte
    /// <c>j</c> is component <c>j</c> and the high nibble is component <c>h + j</c>, so the query comes as its two halves.
    /// </summary>
    /// <param name="isa">The width; unsupported wide widths fall back to narrower ones.</param>
    /// <param name="packed">The packed vector, <c>h</c> bytes.</param>
    /// <param name="queryLow">Components 0 to h-1 of the query, <c>h</c> bytes.</param>
    /// <param name="queryHigh">Components h to 2h-1 of the query (zero where the vector is shorter), <c>h</c> bytes.</param>
    /// <returns>The sum of products; at most 65,535 bytes keeps it from overflowing.</returns>
    /// <exception cref="ArgumentException">The lengths differ or are over 65,535.</exception>
    public static int DotInt4(KernelIsa isa, ReadOnlySpan<byte> packed, ReadOnlySpan<sbyte> queryLow, ReadOnlySpan<sbyte> queryHigh)
    {
        if (packed.Length != queryLow.Length || packed.Length != queryHigh.Length || packed.Length > 65_535)
        {
            ThrowLength();
        }

        if (isa >= KernelIsa.Vector256 && Vector256.IsHardwareAccelerated)
        {
            return DotInt4V256(packed, queryLow, queryHigh);
        }

        if (isa >= KernelIsa.Vector128 && Vector128.IsHardwareAccelerated)
        {
            return DotInt4V128(packed, queryLow, queryHigh);
        }

        return DotInt4Tail(packed, queryLow, queryHigh, 0);
    }

    /// <summary>The dot product on the widest width this machine accelerates.</summary>
    public static int DotInt4(ReadOnlySpan<byte> packed, ReadOnlySpan<sbyte> queryLow, ReadOnlySpan<sbyte> queryHigh)
        => DotInt4(VectorKernels.Best, packed, queryLow, queryHigh);

    /// <summary>The number of differing bits of two equally long bit vectors whose length is a multiple of 8 bytes.</summary>
    /// <param name="a">The first bit vector.</param>
    /// <param name="b">The second bit vector.</param>
    /// <returns>The Hamming distance.</returns>
    /// <exception cref="ArgumentException">The lengths differ or are not a multiple of 8.</exception>
    public static int Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || (a.Length & 7) != 0)
        {
            ThrowLength();
        }

        int sum = 0;
        for (int i = 0; i < a.Length; i += 8)
        {
            sum += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(a[i..]) ^ BinaryPrimitives.ReadUInt64LittleEndian(b[i..]));
        }

        return sum;
    }

    private static int DotInt4Tail(ReadOnlySpan<byte> packed, ReadOnlySpan<sbyte> low, ReadOnlySpan<sbyte> high, int from)
    {
        int sum = 0;
        for (int i = from; i < packed.Length; i++)
        {
            int b = packed[i];
            sum += (b & 15) * low[i] + (b >> 4) * high[i];
        }

        return sum;
    }

    private static int DotInt4V128(ReadOnlySpan<byte> packed, ReadOnlySpan<sbyte> low, ReadOnlySpan<sbyte> high)
    {
        ref byte rp = ref MemoryMarshal.GetReference(packed);
        ref sbyte rl = ref MemoryMarshal.GetReference(low);
        ref sbyte rh = ref MemoryMarshal.GetReference(high);
        Vector128<int> acc = Vector128<int>.Zero;
        Vector128<byte> mask = Vector128.Create((byte)15);
        int i = 0;
        for (; i + 16 <= packed.Length; i += 16)
        {
            Vector128<byte> p = Vector128.LoadUnsafe(ref rp, (nuint)i);
            Vector128<sbyte> lo = (p & mask).AsSByte();
            Vector128<sbyte> hi = Vector128.ShiftRightLogical(p, 4).AsSByte();
            Vector128<sbyte> ql = Vector128.LoadUnsafe(ref rl, (nuint)i);
            Vector128<sbyte> qh = Vector128.LoadUnsafe(ref rh, (nuint)i);
            // A nibble (0 to 15) times a signed byte is at most 1920 in magnitude, so the products fit 16 bits.
            Vector128<short> a0 = Vector128.WidenLower(lo) * Vector128.WidenLower(ql) + Vector128.WidenLower(hi) * Vector128.WidenLower(qh);
            Vector128<short> a1 = Vector128.WidenUpper(lo) * Vector128.WidenUpper(ql) + Vector128.WidenUpper(hi) * Vector128.WidenUpper(qh);
            acc += Vector128.WidenLower(a0) + Vector128.WidenUpper(a0) + Vector128.WidenLower(a1) + Vector128.WidenUpper(a1);
        }

        return Vector128.Sum(acc) + DotInt4Tail(packed, low, high, i);
    }

    private static int DotInt4V256(ReadOnlySpan<byte> packed, ReadOnlySpan<sbyte> low, ReadOnlySpan<sbyte> high)
    {
        ref byte rp = ref MemoryMarshal.GetReference(packed);
        ref sbyte rl = ref MemoryMarshal.GetReference(low);
        ref sbyte rh = ref MemoryMarshal.GetReference(high);
        Vector256<int> acc = Vector256<int>.Zero;
        Vector256<byte> mask = Vector256.Create((byte)15);
        int i = 0;
        for (; i + 32 <= packed.Length; i += 32)
        {
            Vector256<byte> p = Vector256.LoadUnsafe(ref rp, (nuint)i);
            Vector256<sbyte> lo = (p & mask).AsSByte();
            Vector256<sbyte> hi = Vector256.ShiftRightLogical(p, 4).AsSByte();
            Vector256<sbyte> ql = Vector256.LoadUnsafe(ref rl, (nuint)i);
            Vector256<sbyte> qh = Vector256.LoadUnsafe(ref rh, (nuint)i);
            Vector256<short> a0 = Vector256.WidenLower(lo) * Vector256.WidenLower(ql) + Vector256.WidenLower(hi) * Vector256.WidenLower(qh);
            Vector256<short> a1 = Vector256.WidenUpper(lo) * Vector256.WidenUpper(ql) + Vector256.WidenUpper(hi) * Vector256.WidenUpper(qh);
            acc += Vector256.WidenLower(a0) + Vector256.WidenUpper(a0) + Vector256.WidenLower(a1) + Vector256.WidenUpper(a1);
        }

        int sum = Vector256.Sum(acc);
        // The 128-bit loop takes the next 16 bytes and the scalar loop the rest.
        return i + 16 <= packed.Length
            ? sum + DotInt4V128(packed[i..], low[i..], high[i..])
            : sum + DotInt4Tail(packed, low, high, i);
    }

    private static void ThrowLength() => throw new ArgumentException("The vectors must have the same length, within the kernel's limit.");
}
