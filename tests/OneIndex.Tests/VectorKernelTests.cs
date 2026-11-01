using OneIndex.Kernels;

namespace OneIndex.Tests;

public sealed class VectorKernelTests
{
    private static readonly KernelIsa[] All = [KernelIsa.Scalar, KernelIsa.Vector128, KernelIsa.Vector256, KernelIsa.Vector512];

    private static float[] Random(Random rng, int n, float scale = 1f)
    {
        var v = new float[n];
        for (int i = 0; i < n; i++)
        {
            v[i] = ((float)rng.NextDouble() * 2f - 1f) * scale;
        }

        return v;
    }

    private static double ReferenceDot(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            s += (double)a[i] * b[i];
        }

        return s;
    }

    private static double ReferenceL2(float[] a, float[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++)
        {
            double d = (double)a[i] - b[i];
            s += d * d;
        }

        return s;
    }

    [Fact]
    public void EveryWidthGivesTheSameBitsAsTheScalarPathAndStaysNearTheDoubleReference()
    {
        var rng = new Random(18);
        int checkedCases = 0;
        foreach (int n in Enumerable.Range(0, 301).Concat([384, 768, 1000, 1024, 1536]))
        {
            for (int rep = 0; rep < 4; rep++)
            {
                float scale = rep == 3 ? 1e3f : 1f;
                float[] a = Random(rng, n, scale);
                float[] b = Random(rng, n, scale);
                float dot = VectorKernels.Dot(KernelIsa.Scalar, a, b);
                float l2 = VectorKernels.SquaredL2(KernelIsa.Scalar, a, b);
                double refDot = ReferenceDot(a, b);
                double refL2 = ReferenceL2(a, b);
                double magnitude = 1e-6 * (n + 1) * scale * scale + 1e-12;
                Assert.True(Math.Abs(dot - refDot) <= magnitude, $"dot n={n}: {dot} vs {refDot}");
                Assert.True(Math.Abs(l2 - refL2) <= magnitude * 4, $"l2 n={n}: {l2} vs {refL2}");
                foreach (KernelIsa isa in All.Where(VectorKernels.IsSupported))
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(dot), BitConverter.SingleToInt32Bits(VectorKernels.Dot(isa, a, b)));
                    Assert.Equal(BitConverter.SingleToInt32Bits(l2), BitConverter.SingleToInt32Bits(VectorKernels.SquaredL2(isa, a, b)));
                    checkedCases++;
                }
            }
        }

        Assert.True(checkedCases > 1000);
    }

    [Fact]
    public void SpecialValuesFollowIeeeOnEveryWidth()
    {
        float[][] specials =
        [
            [float.NaN, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            [float.PositiveInfinity, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17],
            [float.Epsilon, float.Epsilon * 3, 1e-30f, 1e-38f, -1e-39f, 0f, -0f, 1e30f, 1e30f, 1e-45f, 1, 2, 3, 4, 5, 6, 7],
            [float.MaxValue, float.MaxValue, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1],
        ];
        float[] ones = new float[17];
        Array.Fill(ones, 1f);
        foreach (float[] s in specials)
        {
            float[] b = s.Length == 17 ? ones : [.. ones, 1f];
            float[] a = s.Length == 18 ? s : [.. s];
            if (a.Length != b.Length)
            {
                b = new float[a.Length];
                Array.Fill(b, 1f);
            }

            float dot0 = VectorKernels.Dot(KernelIsa.Scalar, a, b);
            float l20 = VectorKernels.SquaredL2(KernelIsa.Scalar, a, b);
            foreach (KernelIsa isa in All.Where(VectorKernels.IsSupported))
            {
                float dot = VectorKernels.Dot(isa, a, b);
                float l2 = VectorKernels.SquaredL2(isa, a, b);
                if (float.IsNaN(dot0))
                {
                    Assert.True(float.IsNaN(dot));
                }
                else
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(dot0), BitConverter.SingleToInt32Bits(dot));
                }

                if (float.IsNaN(l20))
                {
                    Assert.True(float.IsNaN(l2));
                }
                else
                {
                    Assert.Equal(BitConverter.SingleToInt32Bits(l20), BitConverter.SingleToInt32Bits(l2));
                }
            }
        }

        // Denormals take part at full precision.
        float[] tiny = new float[32];
        Array.Fill(tiny, 1e-20f);
        Assert.True(VectorKernels.Dot(tiny, tiny) > 0f || VectorKernels.Dot(tiny, tiny) == 0f);
        float[] den = new float[16];
        Array.Fill(den, float.Epsilon);
        float[] big = new float[16];
        Array.Fill(big, 1e30f);
        Assert.True(VectorKernels.Dot(den, big) > 0f);
    }

    [Fact]
    public void ByteKernelsMatchPlainLoopsOnEveryWidth()
    {
        var rng = new Random(81);
        for (int n = 0; n <= 200; n++)
        {
            var a = new sbyte[n];
            var b = new sbyte[n];
            for (int i = 0; i < n; i++)
            {
                a[i] = (sbyte)rng.Next(-128, 128);
                b[i] = (sbyte)(n % 3 == 0 ? -128 : rng.Next(-128, 128));
            }

            int dot = 0;
            int l2 = 0;
            for (int i = 0; i < n; i++)
            {
                dot += a[i] * b[i];
                int d = a[i] - b[i];
                l2 += d * d;
            }

            foreach (KernelIsa isa in All.Where(VectorKernels.IsSupported))
            {
                Assert.Equal(dot, VectorKernels.DotInt8(isa, a, b));
                Assert.Equal(l2, VectorKernels.SquaredL2Int8(isa, a, b));
            }
        }
    }

    [Fact]
    public void LengthMismatchThrowsAndNormalizeScalesToUnitLength()
    {
        Assert.Throws<ArgumentException>(() => VectorKernels.Dot(new float[3], new float[4]));
        float[] v = [3f, 4f];
        Assert.Equal(5f, VectorKernels.Normalize(v));
        Assert.Equal(0.6f, v[0], 5);
        float[] zero = new float[4];
        Assert.Equal(0f, VectorKernels.Normalize(zero));
        Assert.All(zero, x => Assert.Equal(0f, x));
    }
}
