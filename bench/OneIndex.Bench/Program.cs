using BenchmarkDotNet.Running;

namespace OneIndex.Bench;

internal static class Program
{
    // Run with --filter '*TextRows*' for the text rows; the corpus driver (index throughput, commit latency, peak memory) is
    // `oneindex-harness driver`. See docs/benchmarks.md.
    private static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
