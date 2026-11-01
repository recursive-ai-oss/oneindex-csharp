namespace OneIndex.Vectors;

/// <summary>
/// Builds a graph by inserting vectors one at a time in ordinal order (Malkov and Yashunin algorithm 1, INSERT). Neighbours are chosen
/// with the heuristic of algorithm 4 (SELECT-NEIGHBORS-HEURISTIC, without extended candidates, keeping pruned connections to fill
/// up to M). The build is single-threaded and uses no clock or random generator: the same vectors in the same order always give the
/// same graph, bit for bit, on every machine (the distance kernels are exact across instruction sets).
/// </summary>
internal sealed class HnswBuilder : HnswGraph
{
    // The deepest layer a node can reach; with M = 16 a node gets to layer 16 once in 16^16 draws.
    private const int MaxLevelCap = 24;

    /// <summary>
    /// Whether neighbour selection tops a node's links up to M with the candidates the heuristic pruned (the paper's keepPrunedConnections).
    /// Off by default: it makes the build slower and, on the embedding sets measured, not the search faster at equal recall
    /// (docs/vectors.md). The benchmark runner switches it on to measure.
    /// </summary>
    internal static bool KeepPruned { get; set; }

    private readonly VectorValues _values;
    private readonly DistanceKind _kind;
    private readonly int _m;
    private readonly int _m0;
    private readonly int _efConstruction;
    private readonly ulong _seed;
    private readonly byte[] _levels;
    private readonly int[] _layer0;           // per node: a count, then up to 2 M links
    private readonly int[]?[] _upper;         // per node of level > 0: level * (M + 1) ints, a count then up to M links per layer
    private readonly FloatNodeScorer _scorer;
    private readonly Candidate[] _found;
    private readonly Candidate[] _pool;
    private readonly int[] _chosen;
    private readonly int[] _shrunk;
    private readonly int[] _discarded;
    private int _entry = -1;
    private int _maxLevel;
    private int _inserted;
    private int _seededStart;
    private int _seededEnd;

    /// <summary>Creates a builder for all vectors of a set.</summary>
    /// <param name="values">The vectors, numbered from 0. Already scaled for the metric (unit length for cosine).</param>
    /// <param name="kind">The distance to build with.</param>
    /// <param name="m">M, the links per node above layer 0; layer 0 allows 2 M.</param>
    /// <param name="efConstruction">The size of the candidate list during insertion.</param>
    /// <param name="seed">Seeds the layer assignment.</param>
    public HnswBuilder(VectorValues values, DistanceKind kind, int m, int efConstruction, ulong seed)
    {
        _values = values;
        _kind = kind;
        _m = m;
        _m0 = 2 * m;
        _efConstruction = Math.Max(efConstruction, m);
        _seed = seed;
        int n = values.Count;
        _levels = new byte[n];
        _layer0 = new int[checked(n * (_m0 + 1))];
        _upper = new int[n][];
        _scorer = new FloatNodeScorer(kind);
        _found = new Candidate[_efConstruction + 1];
        _pool = new Candidate[_m0 + 2];
        _chosen = new int[_m0 + 1];
        _shrunk = new int[_m0 + 1];
        _discarded = new int[_efConstruction + _m0 + 2];
    }

    /// <inheritdoc/>
    public override int Count => _values.Count;

    /// <inheritdoc/>
    public override int EntryPoint => _entry;

    /// <inheritdoc/>
    public override int MaxLevel => _maxLevel;

    /// <inheritdoc/>
    public override int MaxConnections => _m;

    /// <summary>How many vectors have been inserted so far.</summary>
    public int Inserted => _inserted;

    /// <inheritdoc/>
    public override int Level(int node) => _levels[node];

    /// <inheritdoc/>
    public override ReadOnlySpan<int> Neighbors(int node, int level)
    {
        if (level == 0)
        {
            int at = node * (_m0 + 1);
            return _layer0.AsSpan(at + 1, _layer0[at]);
        }

        int[]? up = _upper[node];
        if (up is null || level > _levels[node])
        {
            return default;
        }

        int slot = (level - 1) * (_m + 1);
        return up.AsSpan(slot + 1, up[slot]);
    }

    /// <summary>
    /// Starts from an existing graph over the first vectors of this set instead of from nothing: its nodes, levels and links are copied
    /// with every node number moved up by <paramref name="offset"/>, and <see cref="InsertAll"/> then adds only the other vectors. This
    /// is how a merge reuses the largest source segment's graph (Malkov and Yashunin section 4: the graph is built incrementally, so an
    /// existing graph is a valid state to continue from). It must be called before any insertion, with a graph of the same M.
    /// </summary>
    /// <param name="source">The graph to continue from; not empty.</param>
    /// <param name="offset">The number of its first node in this set; the source's nodes occupy <c>offset .. offset + source.Count - 1</c>.</param>
    public void SeedFrom(HnswGraph source, int offset)
    {
        if (_inserted != 0 || _seededEnd != 0 || source.MaxConnections != _m || source.Count == 0 || offset < 0 || offset + source.Count > _values.Count)
        {
            throw new ArgumentException("The graph cannot seed this builder.", nameof(source));
        }

        for (int node = 0; node < source.Count; node++)
        {
            int target = offset + node;
            int level = source.Level(node);
            _levels[target] = (byte)level;
            ReadOnlySpan<int> links = source.Neighbors(node, 0);
            int at = target * (_m0 + 1);
            _layer0[at] = links.Length;
            for (int i = 0; i < links.Length; i++)
            {
                _layer0[at + 1 + i] = links[i] + offset;
            }

            if (level > 0)
            {
                int[] up = _upper[target] = new int[level * (_m + 1)];
                for (int l = 1; l <= level; l++)
                {
                    ReadOnlySpan<int> upLinks = source.Neighbors(node, l);
                    int slot = (l - 1) * (_m + 1);
                    up[slot] = upLinks.Length;
                    for (int i = 0; i < upLinks.Length; i++)
                    {
                        up[slot + 1 + i] = upLinks[i] + offset;
                    }
                }
            }
        }

        _entry = source.EntryPoint + offset;
        _maxLevel = source.MaxLevel;
        _seededStart = offset;
        _seededEnd = offset + source.Count;
        _inserted = source.Count;
    }

    /// <summary>Inserts every vector in ordinal order, except the ones a seed graph already holds.</summary>
    /// <param name="cancellationToken">Cancels the build.</param>
    public void InsertAll(CancellationToken cancellationToken)
    {
        for (int i = 0; i < _values.Count; i++)
        {
            if (i >= _seededStart && i < _seededEnd)
            {
                i = _seededEnd - 1;
                continue;
            }

            if ((i & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            Insert(i);
        }
    }

    /// <summary>
    /// The layer of a node: the number of times in a row a draw hits 1 in M. This has the distribution of the paper's
    /// floor(-ln(uniform) * mL) with mL = 1 / ln(M) (section 4.1: P(level >= l) = M^-l) without a logarithm, whose last bit can
    /// differ between platforms and would make the persisted graph machine-dependent.
    /// </summary>
    internal static int LevelFor(ulong seed, int node, int m)
    {
        ulong h = Mix(seed + ((ulong)node * 0x9E3779B97F4A7C15UL));
        int level = 0;
        while (level < MaxLevelCap)
        {
            if (h % (uint)m != 0)
            {
                break;
            }

            level++;
            h /= (uint)m;
            if (h == 0)
            {
                h = Mix(h + seed + (ulong)level);
            }
        }

        return level;
    }

    // SplitMix64 finalizer (Steele, Lea and Flood 2014).
    private static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Inserts one vector; vectors must be inserted in ordinal order starting from 0.</summary>
    public void Insert(int node)
    {
        int level = LevelFor(_seed, node, _m);
        _levels[node] = (byte)level;
        if (level > 0)
        {
            _upper[node] = new int[level * (_m + 1)];
        }

        _inserted++;
        if (_entry < 0)
        {
            _entry = node;
            _maxLevel = level;
            return;
        }

        _scorer.Reset(_values, _values.Get(node));
        int entry = _entry;
        float entryDistance = _scorer.ToQuery(entry);
        for (int l = _maxLevel; l > level; l--)
        {
            HnswSearch.Greedy(this, _scorer, l, ref entry, ref entryDistance);
        }

        HnswScratch scratch = HnswScratch.Current;
        var entries = new Candidate(entryDistance, entry);
        int entryCount = 1;
        Span<Candidate> found = _found;
        for (int l = Math.Min(level, _maxLevel); l >= 0; l--)
        {
            scratch.BeginSearch(_values.Count);
            if (entryCount == 1)
            {
                scratch.Visit(entries.Id);
                scratch.Candidates.Push(entries);
                scratch.Results.Push(entries);
            }
            else
            {
                for (int i = 0; i < entryCount; i++)
                {
                    scratch.Visit(found[i].Id);
                    scratch.Candidates.Push(found[i]);
                    scratch.Results.Push(found[i]);
                }
            }

            long visited = 0;
            HnswSearch.Layer(this, _scorer, l, _efConstruction, null, 0, scratch, ref visited);
            entryCount = HnswSearch.Drain(scratch.Results, _efConstruction, found);
            int chosen = SelectNeighbors(found[..entryCount], _m, _chosen, node);
            SetLinks(node, l, _chosen.AsSpan(0, chosen));
            for (int i = 0; i < chosen; i++)
            {
                AddLink(_chosen[i], node, l);
            }
        }

        if (level > _maxLevel)
        {
            _entry = node;
            _maxLevel = level;
        }
    }

    private void SetLinks(int node, int level, ReadOnlySpan<int> links)
    {
        if (level == 0)
        {
            int at = node * (_m0 + 1);
            _layer0[at] = links.Length;
            links.CopyTo(_layer0.AsSpan(at + 1));
        }
        else
        {
            int[] up = _upper[node]!;
            int slot = (level - 1) * (_m + 1);
            up[slot] = links.Length;
            links.CopyTo(up.AsSpan(slot + 1));
        }
    }

    // Adds node to the links of owner on a layer; when full, the owner's links plus the new one are pruned with the heuristic
    // (paper section 4, "shrinking" the connection lists of neighbours, algorithm 1 lines 13-17).
    private void AddLink(int owner, int node, int level)
    {
        int cap = level == 0 ? _m0 : _m;
        int[] store = level == 0 ? _layer0 : _upper[owner]!;
        int slot = level == 0 ? owner * (_m0 + 1) : (level - 1) * (_m + 1);
        int count = store[slot];
        if (count < cap)
        {
            store[slot + 1 + count] = node;
            store[slot] = count + 1;
            return;
        }

        Span<Candidate> pool = _pool;
        for (int i = 0; i < count; i++)
        {
            int nb = store[slot + 1 + i];
            pool[i] = new Candidate(_scorer.Between(owner, nb), nb);
        }

        pool[count] = new Candidate(_scorer.Between(owner, node), node);
        pool[..(count + 1)].Sort(static (a, b) => a.Key < b.Key ? -1 : a.Key > b.Key ? 1 : a.Id.CompareTo(b.Id));
        int kept = SelectNeighbors(pool[..(count + 1)], cap, _shrunk, owner);
        store[slot] = kept;
        _shrunk.AsSpan(0, kept).CopyTo(store.AsSpan(slot + 1));
    }

    // Algorithm 4. candidates are sorted nearest first, their keys being the distance to the base node. A candidate is kept only if it
    // is nearer to the base than to every neighbour already kept, which spreads the links around the base instead of clustering them.
    private int SelectNeighbors(ReadOnlySpan<Candidate> candidates, int m, Span<int> chosen, int exclude)
    {
        int kept = 0;
        int dropped = 0;
        foreach (Candidate c in candidates)
        {
            if (kept >= m)
            {
                break;
            }

            if (c.Id == exclude)
            {
                continue;
            }

            bool good = true;
            for (int j = 0; j < kept; j++)
            {
                if (Pair(c.Id, chosen[j]) < c.Key)
                {
                    good = false;
                    break;
                }
            }

            if (good)
            {
                chosen[kept++] = c.Id;
            }
            else
            {
                _discarded[dropped++] = c.Id;
            }
        }

        // keepPrunedConnections: top up with the nearest of the pruned ones.
        for (int i = 0; KeepPruned && i < dropped && kept < m; i++)
        {
            chosen[kept++] = _discarded[i];
        }

        return kept;
    }

    private float Pair(int a, int b) => FloatNodeScorer.Distance(_kind, _values.Get(a), _values.Get(b));
}
