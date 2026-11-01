using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.VectorBench;

internal static class Engines
{
    public static IEngine Create(string name, int m, int efConstruction, string scratch) => name switch
    {
        "oneindex" => new OneIndexGraph(m, efConstruction),
        "oneindex-segment" => new OneIndexSegment(m, efConstruction, scratch),
#if HNSWNET
        "hnswnet" => new HnswNetEngine(m, efConstruction),
#endif
#if CURIOSITY
        "curiosity" => new CuriosityEngine(m, efConstruction),
#endif
        _ => throw new ArgumentException("engines: oneindex, oneindex-segment, hnswnet, curiosity"),
    };
}

/// <summary>The graph core on its own: the builder and the search over in-memory vectors.</summary>
internal sealed class OneIndexGraph(int m, int efConstruction) : IEngine
{
    private HnswBuilder? _graph;
    private ArrayVectorValues? _values;
    private readonly FloatNodeScorer _scorer = new(DistanceKind.NegativeDot);
    private readonly Candidate[] _out = new Candidate[2048];

    public string Name => "oneindex";

    public long IndexBytes => -1;

    public void Build(float[] data, int count, int dimensions)
    {
        _values = new ArrayVectorValues(data, dimensions);
        _graph = new HnswBuilder(_values, DistanceKind.NegativeDot, m, efConstruction, 42);
        _graph.InsertAll(CancellationToken.None);
    }

    public int Search(float[] query, int k, int ef, int[] ids)
    {
        _scorer.Reset(_values!, query);
        int n = HnswSearch.Search(_graph!, _scorer, k, ef, null, 0, _out, out _);
        for (int i = 0; i < n; i++)
        {
            ids[i] = _out[i].Id;
        }

        return n;
    }

    public void Dispose()
    {
    }
}

/// <summary>The whole engine path: documents through the writer into a segment on disk, searched through the segment reader.</summary>
internal sealed class OneIndexSegment(int m, int efConstruction, string scratch) : IEngine
{
    private string? _dir;
    private IndexWriter? _writer;
    private IndexSnapshot? _snapshot;
    private IVectorFieldReader? _reader;
    private readonly VectorHit[] _hits = new VectorHit[2048];
    private SearchContext? _context;

    public string Name => "oneindex-segment";

    public long IndexBytes { get; private set; } = -1;

    public void Build(float[] data, int count, int dimensions)
    {
        _dir = Path.Combine(scratch, "oneindex-vectorbench-" + Guid.NewGuid().ToString("N"));
        Schema schema = new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = dimensions, HnswM = m, EfConstruction = efConstruction, Stored = false })
            .Build();
        _writer = IndexWriter.Open(new FileSystemStorage(_dir), schema, new IndexWriterOptions { MaxBufferedDocuments = count + 1, AutoMerge = false });
        for (int i = 0; i < count; i++)
        {
            _writer.AddUnique(new Document().Add("id", "d" + i).Add("v", data.AsSpan(i * dimensions, dimensions).ToArray()));
        }

        _writer.Commit();
        _snapshot = _writer.OpenSnapshot();
        _context = SnapshotSearch.CreateContext(_snapshot);
        _reader = _context.Leaves[0].Reader.GetVectors("v");
        IndexBytes = _snapshot.GetSegment(0).Info.Files.Where(f => f.Part is SegmentParts.Vectors or SegmentParts.Graph).Sum(f => f.Length);
    }

    public int Search(float[] query, int k, int ef, int[] ids)
    {
        int n = _reader!.Search(query, k, ef, null, null, false, _hits);
        for (int i = 0; i < n; i++)
        {
            ids[i] = _hits[i].Doc;
        }

        return n;
    }

    public void Dispose()
    {
        _snapshot?.Dispose();
        _writer?.Dispose();
        if (_dir is not null && Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}
