using System.Globalization;
using BenchmarkDotNet.Attributes;
using OneIndex.Harness;

namespace OneIndex.Bench;

/// <summary>
/// The text query rows over the Wikipedia abstracts, one benchmark per row, for every engine that can run. Each invocation
/// answers the row's whole seeded query set, so BenchmarkDotNet divides by <see cref="Queries"/>. Environment:
/// ONEINDEX_BENCH_CORPUS (abstracts JSON lines, required), ONEINDEX_BENCH_DOCS (default 50000), ONEINDEX_BENCH_WORK (index directory, default under the temp path).
/// Method and machine record: docs/benchmarks.md.
/// </summary>
[MemoryDiagnoser]
public class TextRows
{
    private const int Queries = 100;
    private ITextEngine _engine = null!;
    private Dictionary<RowKind, HarnessQuery[]> _queries = null!;

    /// <summary>The engines that can run now.</summary>
    [ParamsSource(nameof(EngineNames))]
    public string Engine { get; set; } = "";

    /// <summary>Engine names for the parameter source.</summary>
    public static IEnumerable<string> EngineNames() => Engines.Available();

    [GlobalSetup]
    public void Setup()
    {
        var corpus = Environment.GetEnvironmentVariable("ONEINDEX_BENCH_CORPUS") ?? throw new InvalidOperationException("Set ONEINDEX_BENCH_CORPUS to the abstracts JSON-lines file.");
        var count = int.Parse(Environment.GetEnvironmentVariable("ONEINDEX_BENCH_DOCS") ?? "50000", CultureInfo.InvariantCulture);
        var work = Environment.GetEnvironmentVariable("ONEINDEX_BENCH_WORK") ?? Path.Combine(Path.GetTempPath(), "oneindex-bench");
        var dir = Path.Combine(work, Engine + "-textrows");
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        var docs = WikiCorpus.Read(corpus, count);
        _queries = QueryGenerator.Generate(docs, Queries, 1);
        _engine = Engines.Create(Engine);
        _engine.Create(new EngineSettings(dir, AnalysisKind.Standard, IndexProfile.Bench));
        foreach (var d in docs)
        {
            _engine.Add(d);
        }

        _engine.Commit();
        _engine.OpenSearcher();
    }

    [GlobalCleanup]
    public void Cleanup() => _engine.Dispose();

    private long Row(RowKind kind) => Rows.RunPass(_engine, _queries[kind]);

    [Benchmark(OperationsPerInvoke = Queries)] public long Terms1() => Row(RowKind.Terms1);

    [Benchmark(OperationsPerInvoke = Queries)] public long Terms2() => Row(RowKind.Terms2);

    [Benchmark(OperationsPerInvoke = Queries)] public long Terms5() => Row(RowKind.Terms5);

    [Benchmark(OperationsPerInvoke = Queries)] public long Phrase() => Row(RowKind.Phrase);

    [Benchmark(OperationsPerInvoke = Queries)] public long Prefix() => Row(RowKind.Prefix);

    [Benchmark(OperationsPerInvoke = Queries)] public long AsYouType() => Row(RowKind.AsYouType);

    [Benchmark(OperationsPerInvoke = Queries)] public long Fuzzy1() => Row(RowKind.Fuzzy1);

    [Benchmark(OperationsPerInvoke = Queries)] public long Fuzzy2() => Row(RowKind.Fuzzy2);

    [Benchmark(OperationsPerInvoke = Queries)] public long Filtered() => Row(RowKind.Filtered);

    [Benchmark(OperationsPerInvoke = Queries)] public long Facets() => Row(RowKind.Facets);

    [Benchmark(OperationsPerInvoke = Queries)] public long Stats() => Row(RowKind.Stats);

    [Benchmark(OperationsPerInvoke = Queries)] public long Sort() => Row(RowKind.Sort);
}

/// <summary>A starting benchmark: the checksum every index file carries.</summary>
[MemoryDiagnoser]
public class ChecksumBenchmark
{
    private readonly byte[] _data = new byte[1 << 20];

    [GlobalSetup]
    public void Setup() => new Random(1).NextBytes(_data);

    [Benchmark]
    public uint Crc32C1MiB() => OneIndex.Index.Crc32C.Compute(_data);
}
