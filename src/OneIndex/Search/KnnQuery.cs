using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex.Search;

/// <summary>
/// The <see cref="K"/> documents whose vector in <see cref="Field"/> is nearest to <see cref="Vector"/>, across the whole index. A
/// match scores its similarity (see <see cref="VectorSimilarity"/>) times the boost, so it can be combined with text queries in a
/// <see cref="BooleanQuery"/> as a clause. The <see cref="Filter"/> restricts which documents may be neighbours before the search
/// picks them, so a page of <see cref="K"/> neighbours is full whenever <see cref="K"/> documents pass the filter; a filter clause
/// beside the knn clause in a boolean query would instead cut the K neighbours after the fact.
/// </summary>
/// <param name="Field">A vector field that is indexed.</param>
/// <param name="Vector">The query vector: as long as the field's dimensions and finite. Cosine fields scale it to length 1.</param>
/// <param name="K">How many neighbours to find, 1 to <see cref="MaxK"/>.</param>
public sealed record KnnQuery(string Field, float[] Vector, int K) : Query
{
    /// <summary>The largest <see cref="K"/> and <see cref="Ef"/>.</summary>
    public const int MaxK = 10_000;

    /// <summary>
    /// How many candidates the graph search keeps while it looks, at least <see cref="K"/>. Larger finds the true neighbours more often and
    /// costs time. 0 (default) uses <c>max(K, 100)</c>. Ignored by exact search. On a field with <see cref="Vectors.VectorIndexKind.Ivf"/> it is instead
    /// the number of lists each segment reads, 1 or more (0: an eighth of the lists, at least 8); more lists find more of the true neighbours.
    /// </summary>
    public int Ef { get; init; }

    /// <summary>Only documents matching this query may be neighbours. Its scores are ignored. Default none.</summary>
    public Query? Filter { get; init; }

    /// <summary>
    /// Neighbours computed outside the segments, such as by a static index's IVF lists: asked for the best <c>count</c> as (global document id,
    /// score), nearest first. When set, the search takes its candidates from here instead of the segments' vector parts (the filter, deletes
    /// and the boost still apply, and a larger count is asked for when a filter drops too many).
    /// </summary>
    internal Func<int, IReadOnlyList<(int Doc, float Score)>>? CandidateSource { get; init; }

    /// <summary>Compare with every vector instead of walking the graph: exact and slower. Default <see langword="false"/>.</summary>
    public bool Exact { get; init; }

    /// <summary>Compares the vector by value (the generated record equality would compare the array by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(KnnQuery? other)
        => other is not null && Field == other.Field && K == other.K && Ef == other.Ef && Exact == other.Exact && Boost.Equals(other.Boost)
           && Equals(Filter, other.Filter) && ReferenceEquals(CandidateSource, other.CandidateSource) && Vector.AsSpan().SequenceEqual(other.Vector);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Field);
        hash.Add(K);
        hash.Add(Ef);
        hash.Add(Exact);
        hash.Add(Boost);
        hash.Add(Filter);
        foreach (float x in Vector)
        {
            hash.Add(x);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitKnn(this);
}

/// <summary>Runs a <see cref="KnnQuery"/>: finds the neighbours of every segment up front, keeps the best <c>K</c> overall and hands each segment its share as a scorer.</summary>
internal sealed class KnnWeight : Weight
{
    private readonly string _description;
    private readonly float[]?[] _scores;
    private readonly int[]?[] _docs;

    private KnnWeight(string description, int[]?[] docs, float[]?[] scores)
    {
        _description = description;
        _docs = docs;
        _scores = scores;
    }

    /// <summary>Prepares the query: validates it, searches every segment and selects the global top K.</summary>
    public static Weight Create(SearchContext context, KnnQuery q, Weight? filter)
    {
        if (!float.IsFinite(q.Boost) || q.Boost < 0f)
        {
            throw new QueryException($"A query boost must be finite and not negative, got {q.Boost}.");
        }

        if (!context.Schema.TryGetField(q.Field, out FieldDefinition? def))
        {
            throw new QueryException($"The schema has no field '{q.Field}'.");
        }

        if (def.Type != FieldType.Vector || !def.Indexed)
        {
            throw new QueryException($"A knn query needs an indexed vector field; '{q.Field}' is {(def.Type == FieldType.Vector ? "not indexed" : def.Type.ToString())}.");
        }

        if (q.Vector is null || q.Vector.Length != def.Dimensions)
        {
            throw new QueryException($"The query vector has {q.Vector?.Length ?? 0} dimensions; field '{q.Field}' has {def.Dimensions}.");
        }

        foreach (float x in q.Vector)
        {
            if (!float.IsFinite(x))
            {
                throw new QueryException("The query vector holds a NaN or an infinite value.");
            }
        }

        bool clustered = def.VectorIndex == VectorIndexKind.Ivf;
        if (q.K < 1 || q.K > KnnQuery.MaxK || q.Ef < 0 || q.Ef > KnnQuery.MaxK || (!clustered && q.Ef != 0 && q.Ef < q.K))
        {
            throw new QueryException(clustered
                ? $"A knn query needs K from 1 to {KnnQuery.MaxK} and Ef (the lists to read on an IVF field) 0 or from 1 to {KnnQuery.MaxK}; got K {q.K}, Ef {q.Ef}."
                : $"A knn query needs K from 1 to {KnnQuery.MaxK} and Ef 0 or from K to {KnnQuery.MaxK}; got K {q.K}, Ef {q.Ef}.");
        }

        var perLeaf = new List<(int Leaf, int Doc, float Score)>();
        var hits = new VectorHit[q.K];
        if (q.CandidateSource is { } source)
        {
            perLeaf = FromCandidates(context, q, filter, source);
        }

        foreach (LeafContext leaf in q.CandidateSource is null ? context.Leaves : Array.Empty<LeafContext>())
        {
            if (leaf.Reader.GetVectors(q.Field) is not { } reader)
            {
                continue;
            }

            ulong[]? accepted = null;
            if (filter is not null)
            {
                accepted = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
                Scorer? s = filter.GetScorer(leaf);
                if (s is null)
                {
                    continue;
                }

                for (int d = s.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = s.NextDoc())
                {
                    accepted[d >> 6] |= 1UL << (d & 63);
                }
            }

            int n = reader.Search(q.Vector, q.K, q.Ef, leaf.Reader.LiveDocs, accepted, q.Exact, hits);
            for (int i = 0; i < n; i++)
            {
                perLeaf.Add((leaf.Ordinal, hits[i].Doc, hits[i].Score * q.Boost));
            }
        }

        // The overall best K: highest score first; equal scores go to the earlier segment, then the smaller document.
        perLeaf.Sort(static (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0)
            {
                return c;
            }

            c = a.Leaf.CompareTo(b.Leaf);
            return c != 0 ? c : a.Doc.CompareTo(b.Doc);
        });
        if (perLeaf.Count > q.K)
        {
            perLeaf.RemoveRange(q.K, perLeaf.Count - q.K);
        }

        perLeaf.Sort(static (a, b) =>
        {
            int c = a.Leaf.CompareTo(b.Leaf);
            return c != 0 ? c : a.Doc.CompareTo(b.Doc);
        });
        var docs = new int[]?[context.Leaves.Count];
        var scores = new float[]?[context.Leaves.Count];
        int at = 0;
        while (at < perLeaf.Count)
        {
            int leaf = perLeaf[at].Leaf;
            int end = at;
            while (end < perLeaf.Count && perLeaf[end].Leaf == leaf)
            {
                end++;
            }

            docs[leaf] = new int[end - at];
            scores[leaf] = new float[end - at];
            for (int i = at; i < end; i++)
            {
                docs[leaf]![i - at] = perLeaf[i].Doc;
                scores[leaf]![i - at] = perLeaf[i].Score;
            }

            at = end;
        }

        return new KnnWeight($"knn on {q.Field} (k={q.K}, {def.Metric})", docs, scores);
    }

    // Candidates from outside the segments: keep the live ones that pass the filter. A filter can drop most of what was asked for, so ask for more
    // (four times as many, up to MaxK) while fewer than K survive and the source still returned everything it was asked for.
    private static List<(int Leaf, int Doc, float Score)> FromCandidates(SearchContext context, KnnQuery q, Weight? filter, Func<int, IReadOnlyList<(int Doc, float Score)>> source)
    {
        IReadOnlyList<LeafContext> leaves = context.Leaves;
        var accepted = new ulong[]?[leaves.Count];
        var skip = new bool[leaves.Count];
        if (filter is not null)
        {
            foreach (LeafContext leaf in leaves)
            {
                accepted[leaf.Ordinal] = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
                Scorer? s = filter.GetScorer(leaf);
                if (s is null)
                {
                    skip[leaf.Ordinal] = true;
                    continue;
                }

                for (int d = s.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = s.NextDoc())
                {
                    accepted[leaf.Ordinal]![d >> 6] |= 1UL << (d & 63);
                }
            }
        }

        int ask = filter is null ? q.K : Math.Min(KnnQuery.MaxK, q.K * 4);
        while (true)
        {
            IReadOnlyList<(int Doc, float Score)> found = source(ask);
            var kept = new List<(int Leaf, int Doc, float Score)>(found.Count);
            foreach ((int global, float score) in found)
            {
                int lo = 0;
                int hi = leaves.Count - 1;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) >> 1;
                    if (leaves[mid].DocBase <= global)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                if (leaves.Count == 0)
                {
                    break;
                }

                LeafContext leaf = leaves[lo];
                int local = global - leaf.DocBase;
                if (local < 0 || local >= leaf.Reader.MaxDoc || skip[leaf.Ordinal] || (leaf.Reader.LiveDocs is { } live && !live.IsLive(local)))
                {
                    continue;
                }

                if (accepted[leaf.Ordinal] is { } bits && (bits[local >> 6] & (1UL << (local & 63))) == 0)
                {
                    continue;
                }

                kept.Add((leaf.Ordinal, local, score * q.Boost));
            }

            if (kept.Count >= q.K || found.Count < ask || ask >= KnnQuery.MaxK)
            {
                return kept;
            }

            ask = Math.Min(KnnQuery.MaxK, ask * 4);
        }
    }

    /// <inheritdoc/>
    public override Scorer? GetScorer(LeafContext leaf) => _docs[leaf.Ordinal] is { } d ? new ArrayScorer(d, _scores[leaf.Ordinal]!) : null;

    /// <inheritdoc/>
    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        int[]? d = _docs[leaf.Ordinal];
        int i = d is null ? -1 : Array.BinarySearch(d, docId);
        return i < 0 ? null : new Explanation(_scores[leaf.Ordinal]![i], $"{_description}: similarity times boost");
    }

    private sealed class ArrayScorer(int[] docs, float[] scores) : Scorer
    {
        private int _index = -1;

        public override int DocId => _index < 0 ? -1 : _index >= docs.Length ? NoMoreDocs : docs[_index];

        public override long Cost => docs.Length;

        public override float Score() => scores[_index];

        public override int NextDoc()
        {
            _index++;
            if (_index >= docs.Length)
            {
                _index = docs.Length;
                return NoMoreDocs;
            }

            return docs[_index];
        }

        public override int Advance(int target)
        {
            int doc = DocId;
            while (doc < target && doc != NoMoreDocs)
            {
                doc = NextDoc();
            }

            return doc;
        }
    }
}
