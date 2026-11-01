using BenchmarkDotNet.Attributes;
using OneIndex.Kernels;

namespace OneIndex.Bench;

/// <summary>Distance kernels on each instruction width the machine has, against the scalar path. One call compares two vectors.</summary>
[MemoryDiagnoser]
public class KernelBenchmarks
{
    private float[] _a = [];
    private float[] _b = [];
    private sbyte[] _ia = [];
    private sbyte[] _ib = [];

    [Params(384, 768)]
    public int Dimensions { get; set; }

    // 0 scalar, 1 Vector128, 2 Vector256, 3 Vector512 (the internal width enum, which a public benchmark class cannot expose).
    [Params(0, 1, 2, 3)]
    public int Width { get; set; }

    private KernelIsa Isa => (KernelIsa)Width;

    [GlobalSetup]
    public void Setup()
    {
        if (!VectorKernels.IsSupported(Isa))
        {
            throw new InvalidOperationException("unsupported on this machine");
        }

        var rng = new Random(1);
        _a = new float[Dimensions];
        _b = new float[Dimensions];
        _ia = new sbyte[Dimensions];
        _ib = new sbyte[Dimensions];
        for (int i = 0; i < Dimensions; i++)
        {
            _a[i] = (float)rng.NextDouble() - 0.5f;
            _b[i] = (float)rng.NextDouble() - 0.5f;
            _ia[i] = (sbyte)rng.Next(-127, 128);
            _ib[i] = (sbyte)rng.Next(-127, 128);
        }
    }

    [Benchmark]
    public float Dot() => VectorKernels.Dot(Isa, _a, _b);

    [Benchmark]
    public float SquaredL2() => VectorKernels.SquaredL2(Isa, _a, _b);

    [Benchmark]
    public int DotInt8() => VectorKernels.DotInt8(Isa, _ia, _ib);
}
