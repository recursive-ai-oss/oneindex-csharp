using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>How the two ranked lists of a <see cref="HybridQuery"/> become one score. Formulas: docs/hybrid.md.</summary>
public enum FusionMethod
{
    /// <summary>
    /// Reciprocal rank fusion (Cormack, Clarke and Buettcher, SIGIR 2009): each list gives a document <c>weight / (RankConstant + rank)</c>, ranks counted from 1, and the
    /// scores of the lists add. It uses only ranks, so it needs no calibration between the text and the vector scores. The default.
    /// </summary>
    ReciprocalRank = 0,

    /// <summary>
    /// Weighted linear fusion: each list's scores are min-max normalised over its window to 0..1 and combined as
    /// <c>(1 - SemanticRatio) * text + SemanticRatio * vector</c>. A document missing from a list contributes 0 for it.
    /// </summary>
    Linear = 1,
}

/// <summary>
/// Runs a text query and a vector query and fuses their ranked lists. Each arm contributes its best <see cref="Window"/> documents (the text arm by
/// relevance score, the vector arm by similarity); a document in either window is a match, and its score is the fusion of its ranks or scores
/// in the two (docs/hybrid.md). Filters belong inside both arms: put them in the text query and in <c>Vector.Filter</c>.
/// </summary>
/// <param name="Text">The text arm: any query. Its score ranks the arm.</param>
/// <param name="Vector">The vector arm. Its <c>K</c> is replaced by <see cref="Window"/>, and a nonzero <c>Ef</c> below it is raised to it.</param>
public sealed record HybridQuery(Query Text, KnnQuery Vector) : Query
{
    /// <summary>The default <see cref="RankConstant"/>, 10 (the paper's 60 gave a worse mean on the relevance sets; docs/hybrid.md).</summary>
    public const int DefaultRankConstant = 10;

    /// <summary>The default <see cref="SemanticRatio"/>, 0.75 (docs/hybrid.md).</summary>
    public const double DefaultSemanticRatio = 0.75;

    /// <summary>The largest <see cref="Window"/>.</summary>
    public const int MaxWindow = KnnQuery.MaxK;

    /// <summary>How the lists combine. Default <see cref="FusionMethod.ReciprocalRank"/>.</summary>
    public FusionMethod Fusion { get; init; }

    /// <summary>The constant added to a rank in reciprocal rank fusion, 1 to 1,000. Default <see cref="DefaultRankConstant"/> (10); a larger constant flattens the difference between ranks (the paper used 60).</summary>
    public int RankConstant { get; init; } = DefaultRankConstant;

    /// <summary>
    /// How much the vector arm counts, from 0 to 1 (Meilisearch's <c>semanticRatio</c>). Default <see cref="DefaultSemanticRatio"/> (0.75). 0.5 makes both arms
    /// count the same, which is plain reciprocal rank fusion. 0 runs the text arm alone and 1 the vector arm alone, without the other arm's cost.
    /// </summary>
    public double SemanticRatio { get; init; } = DefaultSemanticRatio;

    /// <summary>How many documents each arm contributes, 1 to <see cref="MaxWindow"/>. Default 100.</summary>
    public int Window { get; init; } = 100;

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitHybrid(this);
}

/// <summary>Builds the weight of a <see cref="HybridQuery"/> from the weights of its arms.</summary>
internal sealed class HybridWeight : Weight
{
    private readonly int[]?[] _docs;
    private readonly float[]?[] _scores;
    private readonly (int Text, int Vector)[]?[] _ranks; // 0 = not in that arm's window
    private readonly string _description;

    private HybridWeight(int[]?[] docs, float[]?[] scores, (int, int)[]?[] ranks, string description)
    {
        _docs = docs;
        _scores = scores;
        _ranks = ranks;
        _description = description;
    }

    /// <summary>The documents of one arm, best first, as (leaf, document, score).</summary>
    internal static List<(int Leaf, int Doc, float Score)> TopOf(Weight weight, SearchContext context, int window)
    {
        // A heap whose root is the worst kept hit; equal scores keep the earlier document.
        var heap = new PriorityQueue<(int Leaf, int Doc, float Score), (int Leaf, int Doc, float Score)>(Comparer<(int Leaf, int Doc, float Score)>.Create(static (a, b) =>
        {
            int c = a.Score.CompareTo(b.Score);
            if (c != 0)
            {
                return c;
            }

            c = b.Leaf.CompareTo(a.Leaf);
            return c != 0 ? c : b.Doc.CompareTo(a.Doc);
        }));
        foreach (LeafContext leaf in context.Leaves)
        {
            Scorer? scorer = weight.GetScorer(leaf);
            if (scorer is null)
            {
                continue;
            }

            ILiveDocs? live = leaf.Reader.LiveDocs;
            for (int doc = scorer.NextDoc(); doc != DocIdSetIterator.NoMoreDocs; doc = scorer.NextDoc())
            {
                if (live is not null && !live.IsLive(doc))
                {
                    continue;
                }

                float score = scorer.Score();
                if (float.IsNaN(score))
                {
                    continue;
                }

                var hit = (leaf.Ordinal, doc, score);
                if (heap.Count < window)
                {
                    heap.Enqueue(hit, hit);
                }
                else if (heap.TryPeek(out (int Leaf, int Doc, float Score) worst, out _) && Better(hit, worst))
                {
                    heap.DequeueEnqueue(hit, hit);
                }
            }
        }

        var list = new List<(int, int, float)>(heap.Count);
        while (heap.TryDequeue(out (int Leaf, int Doc, float Score) h, out _))
        {
            list.Add(h);
        }

        list.Reverse();
        return list;
    }

    private static bool Better((int Leaf, int Doc, float Score) a, (int Leaf, int Doc, float Score) b)
        => a.Score > b.Score || (a.Score == b.Score && (a.Leaf < b.Leaf || (a.Leaf == b.Leaf && a.Doc < b.Doc)));

    /// <summary>Fuses two ranked lists (best first) into scores per document: the arithmetic of docs/hybrid.md, shared with the tests.</summary>
    internal static Dictionary<(int Leaf, int Doc), (float Score, int TextRank, int VectorRank)> Fuse(
        IReadOnlyList<(int Leaf, int Doc, float Score)> text, IReadOnlyList<(int Leaf, int Doc, float Score)> vector,
        FusionMethod method, int rankConstant, double semanticRatio, float boost)
    {
        var fused = new Dictionary<(int, int), (float, int, int)>();
        double textWeight = method == FusionMethod.ReciprocalRank ? 2.0 * (1.0 - semanticRatio) : 1.0 - semanticRatio;
        double vectorWeight = method == FusionMethod.ReciprocalRank ? 2.0 * semanticRatio : semanticRatio;
        var acc = new Dictionary<(int, int), (double Score, int T, int V)>();
        Add(acc, text, textWeight, method, rankConstant, isText: true);
        Add(acc, vector, vectorWeight, method, rankConstant, isText: false);
        foreach (KeyValuePair<(int, int), (double Score, int T, int V)> kv in acc)
        {
            fused[kv.Key] = ((float)(kv.Value.Score * boost), kv.Value.T, kv.Value.V);
        }

        return fused;
    }

    private static void Add(Dictionary<(int, int), (double Score, int T, int V)> acc, IReadOnlyList<(int Leaf, int Doc, float Score)> list,
        double weight, FusionMethod method, int rankConstant, bool isText)
    {
        if (weight <= 0 || list.Count == 0)
        {
            return;
        }

        double min = double.MaxValue, max = double.MinValue;
        foreach ((int _, int _, float s) in list)
        {
            min = Math.Min(min, s);
            max = Math.Max(max, s);
        }

        for (int i = 0; i < list.Count; i++)
        {
            double part = method == FusionMethod.ReciprocalRank
                ? weight / (rankConstant + i + 1)
                : weight * (max > min ? (list[i].Score - min) / (max - min) : 1.0);
            (int leaf, int doc, _) = list[i];
            acc.TryGetValue((leaf, doc), out (double Score, int T, int V) cur);
            acc[(leaf, doc)] = (cur.Score + part, isText ? i + 1 : cur.T, isText ? cur.V : i + 1);
        }
    }

    public static HybridWeight Create(SearchContext context, HybridQuery q, Weight textWeight, Func<KnnQuery, Weight> vectorWeight)
    {
        if (!float.IsFinite(q.Boost) || q.Boost < 0f)
        {
            throw new QueryException($"A query boost must be finite and not negative, got {q.Boost}.");
        }

        if (q.Window < 1 || q.Window > HybridQuery.MaxWindow)
        {
            throw new QueryException($"A hybrid window must be from 1 to {HybridQuery.MaxWindow}, got {q.Window}.");
        }

        if (q.RankConstant < 1 || q.RankConstant > 1000)
        {
            throw new QueryException($"A hybrid rank constant must be from 1 to 1000, got {q.RankConstant}.");
        }

        if (!(q.SemanticRatio >= 0.0 && q.SemanticRatio <= 1.0))
        {
            throw new QueryException($"A hybrid semantic ratio must be from 0 to 1, got {q.SemanticRatio}.");
        }

        List<(int Leaf, int Doc, float Score)> text = q.SemanticRatio < 1.0 ? TopOf(textWeight, context, q.Window) : [];
        var vector = new List<(int Leaf, int Doc, float Score)>();
        if (q.SemanticRatio > 0.0)
        {
            KnnQuery knn = q.Vector with { K = q.Window, Ef = q.Vector.Ef != 0 && q.Vector.Ef < q.Window ? q.Window : q.Vector.Ef };
            vector = TopOf(vectorWeight(knn), context, q.Window);
        }

        Dictionary<(int Leaf, int Doc), (float Score, int TextRank, int VectorRank)> fused = Fuse(text, vector, q.Fusion, q.RankConstant, q.SemanticRatio, q.Boost);
        var all = new List<(int Leaf, int Doc, float Score, int T, int V)>(fused.Count);
        foreach (KeyValuePair<(int Leaf, int Doc), (float Score, int TextRank, int VectorRank)> kv in fused)
        {
            all.Add((kv.Key.Leaf, kv.Key.Doc, kv.Value.Score, kv.Value.TextRank, kv.Value.VectorRank));
        }

        all.Sort(static (a, b) =>
        {
            int c = a.Leaf.CompareTo(b.Leaf);
            return c != 0 ? c : a.Doc.CompareTo(b.Doc);
        });
        var docs = new int[]?[context.Leaves.Count];
        var scores = new float[]?[context.Leaves.Count];
        var ranks = new (int, int)[]?[context.Leaves.Count];
        int at = 0;
        while (at < all.Count)
        {
            int leaf = all[at].Leaf;
            int end = at;
            while (end < all.Count && all[end].Leaf == leaf)
            {
                end++;
            }

            docs[leaf] = new int[end - at];
            scores[leaf] = new float[end - at];
            ranks[leaf] = new (int, int)[end - at];
            for (int i = at; i < end; i++)
            {
                docs[leaf]![i - at] = all[i].Doc;
                scores[leaf]![i - at] = all[i].Score;
                ranks[leaf]![i - at] = (all[i].T, all[i].V);
            }

            at = end;
        }

        string how = q.Fusion == FusionMethod.ReciprocalRank ? $"reciprocal rank fusion (constant {q.RankConstant})" : "linear fusion";
        return new HybridWeight(docs, scores, ranks, $"hybrid, {how}, semantic ratio {q.SemanticRatio:0.###}, window {q.Window}");
    }

    public override Scorer? GetScorer(LeafContext leaf) => _docs[leaf.Ordinal] is { } d ? new ArrayScorer(d, _scores[leaf.Ordinal]!) : null;

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        int[]? d = _docs[leaf.Ordinal];
        int i = d is null ? -1 : Array.BinarySearch(d, docId);
        if (i < 0)
        {
            return null;
        }

        (int t, int v) = _ranks[leaf.Ordinal]![i];
        string where = $"text rank {(t == 0 ? "none" : t.ToString(System.Globalization.CultureInfo.InvariantCulture))}, vector rank {(v == 0 ? "none" : v.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
        return new Explanation(_scores[leaf.Ordinal]![i], $"{_description}: {where}");
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
