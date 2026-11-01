using System.Runtime.CompilerServices;
using OneIndex.Kernels;

namespace OneIndex.Vectors;

/// <summary>A node with its distance to whatever is being searched for. Ordered by distance, then by node, so that equal distances break the same way every time.</summary>
internal readonly struct Candidate(float key, int id)
{
    public readonly float Key = key;

    public readonly int Id = id;
}

/// <summary>A binary heap of <see cref="Candidate"/>: a min-heap (nearest on top) or a max-heap (farthest on top). Reused between searches; it never shrinks.</summary>
internal sealed class CandidateHeap(bool farthestOnTop)
{
    private Candidate[] _items = new Candidate[64];
    private int _count;

    public int Count => _count;

    public Candidate Peek => _items[0];

    public void Clear() => _count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Before(in Candidate x, in Candidate y)
        => farthestOnTop
            ? (x.Key > y.Key || (x.Key == y.Key && x.Id > y.Id))
            : (x.Key < y.Key || (x.Key == y.Key && x.Id < y.Id));

    public void Push(Candidate c)
    {
        if (_count == _items.Length)
        {
            Array.Resize(ref _items, _count * 2);
        }

        int i = _count++;
        while (i > 0)
        {
            int parent = (i - 1) >> 1;
            if (!Before(c, _items[parent]))
            {
                break;
            }

            _items[i] = _items[parent];
            i = parent;
        }

        _items[i] = c;
    }

    public Candidate Pop()
    {
        Candidate top = _items[0];
        Candidate last = _items[--_count];
        if (_count > 0)
        {
            int i = 0;
            while (true)
            {
                int child = (i << 1) + 1;
                if (child >= _count)
                {
                    break;
                }

                if (child + 1 < _count && Before(_items[child + 1], _items[child]))
                {
                    child++;
                }

                if (!Before(_items[child], last))
                {
                    break;
                }

                _items[i] = _items[child];
                i = child;
            }

            _items[i] = last;
        }

        return top;
    }
}

/// <summary>Whether a vector may appear in a result: not deleted, and inside the caller's filter. Takes the vector's ordinal within the field.</summary>
internal interface IOrdinalFilter
{
    bool Accepts(int ordinal);
}

/// <summary>The vectors of one field in one segment, numbered from 0 (their ordinal). Reading does not allocate once the data is cached.</summary>
internal abstract class VectorValues
{
    public abstract int Count { get; }

    public abstract int Dimensions { get; }

    /// <summary>The vector. The span stays valid for the life of the object and must not be written to.</summary>
    public abstract ReadOnlySpan<float> Get(int ordinal);
}

/// <summary>Vectors held in one array, <c>Dimensions</c> floats each.</summary>
internal sealed class ArrayVectorValues(float[] data, int dimensions) : VectorValues
{
    public override int Count => data.Length / dimensions;

    public override int Dimensions => dimensions;

    public override ReadOnlySpan<float> Get(int ordinal) => data.AsSpan(checked(ordinal * dimensions), dimensions);
}

/// <summary>The two measures the index computes; cosine is the first on vectors scaled to length 1.</summary>
internal enum DistanceKind : byte
{
    /// <summary>Minus the dot product, so that smaller is closer for every measure.</summary>
    NegativeDot = 0,

    /// <summary>Squared Euclidean distance.</summary>
    SquaredL2 = 1,
}

/// <summary>
/// Measures distances from a query to indexed nodes and between two indexed nodes. This is the seam a quantized representation plugs
/// into: a codec supplies its own scorer over compressed data, and the graph code above never sees the difference.
/// </summary>
internal abstract class NodeScorer
{
    /// <summary>The distance from the current query to a node.</summary>
    public abstract float ToQuery(int ordinal);

    /// <summary>The distance between two nodes.</summary>
    public abstract float Between(int a, int b);
}

/// <summary>Full-precision scoring over a <see cref="VectorValues"/>.</summary>
internal sealed class FloatNodeScorer : NodeScorer
{
    private readonly DistanceKind _kind;
    private VectorValues _values = null!;
    private float[] _query = [];
    private int _length;

    public FloatNodeScorer(DistanceKind kind) => _kind = kind;

    /// <summary>Points the scorer at a vector set and a query (copied, so the caller may reuse its buffer).</summary>
    public void Reset(VectorValues values, ReadOnlySpan<float> query)
    {
        _values = values;
        if (_query.Length < query.Length)
        {
            _query = new float[query.Length];
        }

        query.CopyTo(_query);
        _length = query.Length;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Distance(DistanceKind kind, ReadOnlySpan<float> a, ReadOnlySpan<float> b)
        => kind == DistanceKind.NegativeDot ? -VectorKernels.Dot(a, b) : VectorKernels.SquaredL2(a, b);

    public override float ToQuery(int ordinal) => Distance(_kind, _query.AsSpan(0, _length), _values.Get(ordinal));

    public override float Between(int a, int b) => Distance(_kind, _values.Get(a), _values.Get(b));
}

/// <summary>Per-thread working memory for searches: the visited marks and the two heaps. Reused, so a warm search allocates nothing.</summary>
internal sealed class HnswScratch
{
    [ThreadStatic]
    private static HnswScratch? s_current;

    private int[] _visited = [];
    private int _stamp;

    public readonly CandidateHeap Candidates = new(farthestOnTop: false);

    public readonly CandidateHeap Results = new(farthestOnTop: true);

    public static HnswScratch Current => s_current ??= new HnswScratch();

    private float[] _query = [];
    private FloatNodeScorer? _dot;
    private FloatNodeScorer? _l2;

    /// <summary>A reusable buffer of exactly <paramref name="length"/> floats' worth of space (it may be longer).</summary>
    public float[] QueryBuffer(int length)
    {
        if (_query.Length < length)
        {
            _query = new float[length];
        }

        return _query;
    }

    /// <summary>The thread's scorer for a distance kind.</summary>
    public FloatNodeScorer Scorer(DistanceKind kind) => kind == DistanceKind.NegativeDot ? (_dot ??= new FloatNodeScorer(kind)) : (_l2 ??= new FloatNodeScorer(kind));

    /// <summary>Forgets every visit and makes room for <paramref name="nodes"/> nodes.</summary>
    public void BeginSearch(int nodes)
    {
        if (_visited.Length < nodes)
        {
            _visited = new int[Math.Max(nodes, _visited.Length * 2)];
            _stamp = 0;
        }

        if (++_stamp == int.MaxValue)
        {
            Array.Clear(_visited);
            _stamp = 1;
        }

        Candidates.Clear();
        Results.Clear();
    }

    /// <summary>Marks a node visited; <see langword="false"/> if it already was.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Visit(int node)
    {
        if (_visited[node] == _stamp)
        {
            return false;
        }

        _visited[node] = _stamp;
        return true;
    }
}

/// <summary>
/// A hierarchical navigable small-world graph, read-only view. Layer 0 holds every node; layer <c>l</c> holds the nodes whose level
/// is at least <c>l</c>, and a node in layer <c>l</c> has at most <see cref="MaxConnections"/> links (twice that in layer 0).
/// Malkov and Yashunin, "Efficient and robust approximate nearest neighbor search using Hierarchical Navigable Small World graphs",
/// IEEE TPAMI 42(4), 2020, section 4.
/// </summary>
internal abstract class HnswGraph
{
    /// <summary>The number of nodes.</summary>
    public abstract int Count { get; }

    /// <summary>The node searches start from, on the top layer; -1 when the graph is empty.</summary>
    public abstract int EntryPoint { get; }

    /// <summary>The highest layer in use.</summary>
    public abstract int MaxLevel { get; }

    /// <summary>M: the most links a node has above layer 0.</summary>
    public abstract int MaxConnections { get; }

    /// <summary>The highest layer a node is in.</summary>
    public abstract int Level(int node);

    /// <summary>The links of a node on a layer; empty if the node is not in that layer. Valid until the graph changes.</summary>
    public abstract ReadOnlySpan<int> Neighbors(int node, int level);

    /// <summary>The most links a node has on layer 0 (2 M, paper section 4.1, M_max0).</summary>
    public int MaxConnections0 => MaxConnections * 2;
}

/// <summary>Graph search, Malkov and Yashunin algorithms 2 (SEARCH-LAYER) and 5 (K-NN-SEARCH).</summary>
internal static class HnswSearch
{
    /// <summary>
    /// Finds up to <paramref name="k"/> nearest accepted nodes. The upper layers are walked greedily (ef = 1); layer 0 keeps the
    /// <paramref name="ef"/> best accepted nodes found. Nodes the filter rejects are still walked through, so deleted or filtered-out
    /// nodes never cut the graph; they just do not become results.
    /// </summary>
    /// <param name="graph">The graph.</param>
    /// <param name="scorer">Distances from the query.</param>
    /// <param name="k">How many results to return.</param>
    /// <param name="ef">The size of the layer-0 result list (at least <paramref name="k"/>).</param>
    /// <param name="accept">Which nodes may be results; <see langword="null"/> accepts all.</param>
    /// <param name="visitLimit">Stop after scoring this many nodes; 0 means no limit.</param>
    /// <param name="output">Receives the results, nearest first; at least <paramref name="k"/> long.</param>
    /// <param name="complete"><see langword="false"/> if the visit limit stopped the search.</param>
    /// <returns>How many results were written.</returns>
    public static int Search(HnswGraph graph, NodeScorer scorer, int k, int ef, IOrdinalFilter? accept, long visitLimit, Span<Candidate> output, out bool complete)
    {
        complete = true;
        if (graph.Count == 0 || k <= 0)
        {
            return 0;
        }

        int entry = graph.EntryPoint;
        float entryDistance = scorer.ToQuery(entry);
        for (int level = graph.MaxLevel; level > 0; level--)
        {
            Greedy(graph, scorer, level, ref entry, ref entryDistance);
        }

        HnswScratch scratch = HnswScratch.Current;
        scratch.BeginSearch(graph.Count);
        scratch.Visit(entry);
        var start = new Candidate(entryDistance, entry);
        scratch.Candidates.Push(start);
        if (accept is null || accept.Accepts(entry))
        {
            scratch.Results.Push(start);
        }

        long visited = 1;
        complete = Layer(graph, scorer, 0, Math.Max(ef, k), accept, visitLimit, scratch, ref visited);
        return Drain(scratch.Results, k, output);
    }

    /// <summary>Walks one layer towards the query by always moving to a nearer neighbour (SEARCH-LAYER with ef = 1).</summary>
    public static void Greedy(HnswGraph graph, NodeScorer scorer, int level, ref int node, ref float distance)
    {
        bool improved = true;
        while (improved)
        {
            improved = false;
            foreach (int nb in graph.Neighbors(node, level))
            {
                float d = scorer.ToQuery(nb);
                if (d < distance)
                {
                    distance = d;
                    node = nb;
                    improved = true;
                }
            }
        }
    }

    /// <summary>
    /// Algorithm 2 on one layer. The scratch must hold the entry candidates and results already; on return its result heap holds the
    /// <paramref name="ef"/> nearest accepted nodes found.
    /// </summary>
    /// <returns><see langword="false"/> if the visit limit stopped it.</returns>
    public static bool Layer(HnswGraph graph, NodeScorer scorer, int level, int ef, IOrdinalFilter? accept, long visitLimit, HnswScratch scratch, ref long visited)
    {
        CandidateHeap candidates = scratch.Candidates;
        CandidateHeap results = scratch.Results;
        while (candidates.Count > 0)
        {
            Candidate c = candidates.Pop();
            if (results.Count >= ef && c.Key > results.Peek.Key)
            {
                break;
            }

            foreach (int nb in graph.Neighbors(c.Id, level))
            {
                if (!scratch.Visit(nb))
                {
                    continue;
                }

                float d = scorer.ToQuery(nb);
                visited++;
                if (results.Count < ef || d < results.Peek.Key)
                {
                    var cand = new Candidate(d, nb);
                    candidates.Push(cand);
                    if (accept is null || accept.Accepts(nb))
                    {
                        results.Push(cand);
                        if (results.Count > ef)
                        {
                            results.Pop();
                        }
                    }
                }
            }

            if (visitLimit > 0 && visited > visitLimit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Empties a result heap into <paramref name="output"/>, nearest first, keeping the best <paramref name="k"/>.</summary>
    public static int Drain(CandidateHeap results, int k, Span<Candidate> output)
    {
        while (results.Count > k)
        {
            results.Pop();
        }

        int n = results.Count;
        for (int i = n - 1; i >= 0; i--)
        {
            output[i] = results.Pop();
        }

        return n;
    }
}
