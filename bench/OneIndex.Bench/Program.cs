using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using OneIndex.Index;

namespace OneIndex.Bench;

internal static class Program
{
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

/// <summary>A starting benchmark: the checksum every index file carries. Real suites are added with the features they measure.</summary>
[MemoryDiagnoser]
public class ChecksumBenchmark
{
    private readonly byte[] _data = new byte[1 << 20];

    [GlobalSetup]
    public void Setup() => new Random(1).NextBytes(_data);

    [Benchmark]
    public uint Crc32C1MiB() => Crc32C.Compute(_data);
}
