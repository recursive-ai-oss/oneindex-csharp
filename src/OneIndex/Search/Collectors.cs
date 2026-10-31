namespace OneIndex.Search;

/// <summary>A matching document with its score. The id is global: the segment's offset plus the local id.</summary>
/// <param name="DocId">The global document id.</param>
/// <param name="Score">The score.</param>
public readonly record struct ScoreDoc(int DocId, float Score);

/// <summary>The best documents of a search.</summary>
/// <param name="TotalHits">How many documents matched; a lower bound unless <paramref name="TotalHitsExact"/>.</param>
/// <param name="TotalHitsExact">Whether <paramref name="TotalHits"/> is the exact count.</param>
/// <param name="Hits">The best documents, best first: higher score first, equal scores by lower document id.</param>
public sealed record TopDocs(long TotalHits, bool TotalHitsExact, IReadOnlyList<ScoreDoc> Hits);

/// <summary>
/// Keeps the <c>count</c> best documents. Ordering is total and deterministic: higher score first, and for equal scores the lower
/// global document id first, so the same snapshot always gives the same page. NaN scores count as the lowest possible. After
/// the heap fills, the collector tells the scorer the lowest score still competitive. Collection allocates nothing.
/// </summary>
public sealed class TopScoreCollector : Collector
{
    private readonly float[] _scores;
    private readonly int[] _docs;
    private int _size;
    private long _totalHits;

    private readonly bool _countExactly;
    private bool _thresholdSent;

    /// <summary>Creates a collector that counts every match.</summary>
    /// <param name="count">How many documents to keep, at least 1.</param>
    public TopScoreCollector(int count)
        : this(count, true)
    {
    }

    /// <summary>Creates a collector.</summary>
    /// <param name="count">How many documents to keep, at least 1.</param>
    /// <param name="countExactly">Whether every match must be counted. When <see langword="false"/> the scorer may skip documents that cannot enter the top <paramref name="count"/>.</param>
    public TopScoreCollector(int count, bool countExactly)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        _scores = new float[count];
        _docs = new int[count];
        _countExactly = countExactly;
    }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => _countExactly ? ScoreMode.Complete : ScoreMode.TopScores;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf) => new Leaf(this, leaf.DocBase);

    /// <summary>The documents kept so far, best first.</summary>
    /// <returns>The result; the total is exact because every match is visited.</returns>
    public TopDocs GetTopDocs()
    {
        var hits = new ScoreDoc[_size];
        for (int i = 0; i < _size; i++)
        {
            hits[i] = new ScoreDoc(_docs[i], _scores[i]);
        }

        Array.Sort(hits, static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : a.DocId.CompareTo(b.DocId);
        });
        // A scorer was only told to skip once the heap had filled; before that nothing can have been skipped.
        return new TopDocs(_totalHits, _countExactly || !_thresholdSent, hits);
    }

    // Heap order: the root is the worst kept document (lowest score; for equal scores, the highest document id).
    private static bool Worse(float scoreA, int docA, float scoreB, int docB) => scoreA < scoreB || (scoreA == scoreB && docA > docB);

    private void Offer(float score, int doc)
    {
        _totalHits++;
        if (_size < _scores.Length)
        {
            int i = _size++;
            _scores[i] = score;
            _docs[i] = doc;
            while (i > 0)
            {
                int parent = (i - 1) >> 1;
                if (!Worse(_scores[i], _docs[i], _scores[parent], _docs[parent]))
                {
                    break;
                }

                Swap(i, parent);
                i = parent;
            }

            return;
        }

        if (!Worse(_scores[0], _docs[0], score, doc))
        {
            return;
        }

        _scores[0] = score;
        _docs[0] = doc;
        int n = _size;
        int k = 0;
        while (true)
        {
            int left = (2 * k) + 1;
            if (left >= n)
            {
                break;
            }

            int worst = left;
            int right = left + 1;
            if (right < n && Worse(_scores[right], _docs[right], _scores[left], _docs[left]))
            {
                worst = right;
            }

            if (!Worse(_scores[worst], _docs[worst], _scores[k], _docs[k]))
            {
                break;
            }

            Swap(k, worst);
            k = worst;
        }
    }

    private void Swap(int a, int b)
    {
        (_scores[a], _scores[b]) = (_scores[b], _scores[a]);
        (_docs[a], _docs[b]) = (_docs[b], _docs[a]);
    }

    private sealed class Leaf(TopScoreCollector owner, int docBase) : LeafCollector
    {
        private Scorer? _scorer;
        private float _threshold = float.NegativeInfinity;

        public override void SetScorer(Scorer scorer) => _scorer = scorer;

        public override void Collect(int docId)
        {
            float score = _scorer!.Score();
            if (float.IsNaN(score))
            {
                score = float.NegativeInfinity;
            }

            owner.Offer(score, docBase + docId);
            if (owner._size == owner._scores.Length && owner._scores[0] > _threshold)
            {
                _threshold = owner._scores[0];
                if (!owner._countExactly)
                {
                    owner._thresholdSent = true;
                    _scorer.SetMinCompetitiveScore(_threshold);
                }
            }
        }
    }
}

/// <summary>Counts matching live documents. It needs no scores.</summary>
public sealed class CountCollector : Collector
{
    /// <summary>The number of documents collected.</summary>
    public long Count { get; private set; }

    /// <inheritdoc/>
    public override ScoreMode ScoreMode => ScoreMode.CompleteNoScores;

    /// <inheritdoc/>
    public override LeafCollector GetLeafCollector(LeafContext leaf) => new Leaf(this);

    private sealed class Leaf(CountCollector owner) : LeafCollector
    {
        public override void SetScorer(Scorer scorer)
        {
        }

        public override void Collect(int docId) => owner.Count++;
    }
}
