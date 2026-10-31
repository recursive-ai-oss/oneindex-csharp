namespace OneIndex.Search;

/// <summary>
/// A term's score from its frequency in a document and the document's field length. It must not decrease as the frequency grows or
/// as the field length shrinks, and must not be negative: that monotonicity is what lets the largest frequency and shortest field of a
/// block bound every score in it (Ding and Suel 2011, §3).
/// </summary>
/// <param name="frequency">The term frequency in the document, at least 1.</param>
/// <param name="fieldLength">The number of terms in the document's field.</param>
/// <returns>The score contribution.</returns>
public delegate float TermScoreFunction(int frequency, int fieldLength);

/// <summary>
/// Scores one term's postings with a <see cref="TermScoreFunction"/> and bounds the score of a run of documents from the block
/// impacts the postings carry, so a block-max WAND disjunction can skip blocks that cannot reach the competitive threshold. The
/// scoring formula itself (BM25F and friends) belongs to the caller; this class only connects postings to the bound.
/// </summary>
public sealed class PostingsScorer : Scorer
{
    private readonly PostingsIterator _postings;
    private readonly ITextFieldReader _field;
    private readonly TermScoreFunction _score;
    private BlockImpact _shallow;

    /// <summary>Creates a scorer.</summary>
    /// <param name="postings">The term's postings, requested with frequencies; not shared with another scorer.</param>
    /// <param name="field">The field the postings belong to, for field lengths.</param>
    /// <param name="score">The scoring function, monotone as described at <see cref="TermScoreFunction"/>.</param>
    public PostingsScorer(PostingsIterator postings, ITextFieldReader field, TermScoreFunction score)
    {
        _postings = postings ?? throw new ArgumentNullException(nameof(postings));
        _field = field ?? throw new ArgumentNullException(nameof(field));
        _score = score ?? throw new ArgumentNullException(nameof(score));
        _shallow = postings.TermImpact;
    }

    /// <inheritdoc/>
    public override int DocId => _postings.DocId;

    /// <inheritdoc/>
    public override long Cost => _postings.Cost;

    /// <inheritdoc/>
    public override float MaxScore => Bound(_postings.TermImpact);

    /// <inheritdoc/>
    public override int NextDoc() => _postings.NextDoc();

    /// <inheritdoc/>
    public override int Advance(int target) => _postings.Advance(target);

    /// <inheritdoc/>
    public override float Score() => _score(_postings.Frequency, _field.GetFieldLength(_postings.DocId));

    /// <inheritdoc/>
    public override int AdvanceShallow(int target)
    {
        _shallow = _postings.ShallowAdvance(target);
        return _shallow.LastDocId;
    }

    /// <inheritdoc/>
    public override float GetMaxScore(int upTo) => Bound(_shallow);

    private float Bound(BlockImpact impact) => impact.LastDocId == NoMoreDocs && impact.MaxFrequency == 0 ? 0f : _score(impact.MaxFrequency, impact.MinFieldLength);
}

/// <summary>
/// The disjunction of scorers, evaluated with block-max WAND (Broder et al. 2003, WAND; Ding and Suel 2011, block-max): once the
/// collector reports a threshold with <see cref="SetMinCompetitiveScore"/>, documents whose best possible score is below it are
/// never decoded or scored. Clauses are kept ordered by their current document; the pivot is the first clause at which the sum of
/// the clauses' score bounds reaches the threshold, and before scoring a pivot document the bounds of the blocks that contain it
/// are summed, and the whole run of documents up to the nearest block end is skipped if they fall short. With no threshold
/// (the default) it is a plain disjunction that returns every matching document. The score of a document is the sum of its
/// clauses' scores, added in clause order, so it equals an exhaustive evaluation exactly.
/// </summary>
public sealed class BlockMaxWandScorer : Scorer
{
    // Bounds are sums of floats added in a different order than the real score, so pruning leaves a relative margin.
    private const double Slack = 1e-5;
    private readonly Scorer[] _clauses;
    private readonly int[] _docs;
    private readonly int[] _order;
    private readonly float[] _maxScores;
    private readonly long _cost;
    private float _minScore;
    private int _current = -1;
    private bool _started;

    /// <summary>Creates the disjunction.</summary>
    /// <param name="clauses">The clauses; each must report <see cref="Scorer.MaxScore"/> and implement <see cref="Scorer.AdvanceShallow"/> and <see cref="Scorer.GetMaxScore"/> to be pruned (a clause that does not simply is never skipped past).</param>
    public BlockMaxWandScorer(IReadOnlyList<Scorer> clauses)
    {
        ArgumentNullException.ThrowIfNull(clauses);
        _clauses = [.. clauses];
        _docs = new int[_clauses.Length];
        Array.Fill(_docs, -1); // so that Advance before the first NextDoc positions every clause
        _order = new int[_clauses.Length];
        _maxScores = new float[_clauses.Length];
        for (int i = 0; i < _clauses.Length; i++)
        {
            _maxScores[i] = _clauses[i].MaxScore;
            _cost += _clauses[i].Cost;
        }
    }

    /// <summary>Whether block bounds are used to skip runs of documents. Default <see langword="true"/>; false leaves only the whole-list bounds of plain WAND.</summary>
    public bool UseBlockMax { get; init; } = true;

    /// <summary>How many documents the iterator has returned, that is, how many a collector scored.</summary>
    public long DocumentsReturned { get; private set; }

    /// <inheritdoc/>
    public override int DocId => _current;

    /// <inheritdoc/>
    public override long Cost => _cost;

    /// <inheritdoc/>
    public override float MaxScore
    {
        get
        {
            double sum = 0;
            foreach (float m in _maxScores)
            {
                sum += m;
            }

            return (float)sum;
        }
    }

    /// <inheritdoc/>
    public override int AdvanceShallow(int target)
    {
        // The run every clause can bound together ends at the earliest block end; a clause already past the target bounds from where it is.
        int end = NoMoreDocs;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_docs[i] == NoMoreDocs)
            {
                continue;
            }

            int e = _clauses[i].AdvanceShallow(Math.Max(target, _docs[i]));
            if (e < end)
            {
                end = e;
            }
        }

        return end;
    }

    /// <inheritdoc/>
    public override float GetMaxScore(int upTo)
    {
        float sum = 0f;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_docs[i] != NoMoreDocs)
            {
                sum += _clauses[i].GetMaxScore(upTo);
            }
        }

        return sum;
    }

    /// <inheritdoc/>
    public override void SetMinCompetitiveScore(float minScore)
    {
        if (minScore > _minScore)
        {
            _minScore = minScore;
        }
    }

    /// <inheritdoc/>
    public override float Score()
    {
        float sum = 0f;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_docs[i] == _current)
            {
                sum += _clauses[i].Score();
            }
        }

        return sum;
    }

    /// <inheritdoc/>
    public override int NextDoc()
    {
        if (_current == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (!_started)
        {
            _started = true;
            for (int i = 0; i < _clauses.Length; i++)
            {
                _docs[i] = _clauses[i].NextDoc();
            }
        }
        else
        {
            for (int i = 0; i < _clauses.Length; i++)
            {
                if (_docs[i] == _current)
                {
                    _docs[i] = _clauses[i].NextDoc();
                }
            }
        }

        return Find();
    }

    /// <inheritdoc/>
    public override int Advance(int target)
    {
        if (_current == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (target <= _current)
        {
            target = _current + 1;
        }

        _started = true;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_docs[i] < target)
            {
                _docs[i] = _clauses[i].Advance(target);
            }
        }

        return Find();
    }

    private bool Pruned(double bound) => bound * (1 + Slack) < _minScore;

    private int Find()
    {
        int n;
        while (true)
        {
            n = 0;
            for (int i = 0; i < _clauses.Length; i++)
            {
                if (_docs[i] != NoMoreDocs)
                {
                    // Insertion sort by current document: the order changes little between rounds.
                    int k = n++;
                    while (k > 0 && _docs[_order[k - 1]] > _docs[i])
                    {
                        _order[k] = _order[k - 1];
                        k--;
                    }

                    _order[k] = i;
                }
            }

            if (n == 0)
            {
                return Finish();
            }

            // WAND pivot: the first clause where the clauses up to it could together reach the threshold.
            double accumulated = 0;
            int pivot = -1;
            for (int k = 0; k < n; k++)
            {
                accumulated += _maxScores[_order[k]];
                if (!Pruned(accumulated))
                {
                    pivot = k;
                    break;
                }
            }

            if (pivot < 0)
            {
                // Even every remaining clause together cannot reach the threshold: nothing left is competitive.
                return Finish();
            }

            int pivotDoc = _docs[_order[pivot]];
            while (pivot + 1 < n && _docs[_order[pivot + 1]] == pivotDoc)
            {
                pivot++;
            }

            int firstAtPivot = 0;
            while (_docs[_order[firstAtPivot]] != pivotDoc)
            {
                firstAtPivot++;
            }

            if (firstAtPivot > 0)
            {
                // Clauses before the pivot cannot reach the threshold without it, so nothing below the pivot document is competitive.
                for (int k = 0; k < firstAtPivot; k++)
                {
                    int c = _order[k];
                    _docs[c] = _clauses[c].Advance(pivotDoc);
                }

                continue;
            }

            if (UseBlockMax && _minScore > 0)
            {
                double blockSum = 0;
                int blockEnd = NoMoreDocs;
                for (int k = 0; k <= pivot; k++)
                {
                    Scorer clause = _clauses[_order[k]];
                    int end = clause.AdvanceShallow(pivotDoc);
                    blockSum += clause.GetMaxScore(pivotDoc);
                    if (end < blockEnd)
                    {
                        blockEnd = end;
                    }
                }

                if (Pruned(blockSum))
                {
                    // No document from the pivot to the end of the nearest block can reach the threshold with these clauses alone;
                    // the next candidate is past that block or at the next clause's document, whichever comes first.
                    int target = blockEnd == NoMoreDocs ? NoMoreDocs : blockEnd + 1;
                    if (pivot + 1 < n && _docs[_order[pivot + 1]] < target)
                    {
                        target = _docs[_order[pivot + 1]];
                    }

                    if (target == NoMoreDocs)
                    {
                        for (int k = 0; k <= pivot; k++)
                        {
                            _docs[_order[k]] = NoMoreDocs;
                        }

                        continue;
                    }

                    for (int k = 0; k <= pivot; k++)
                    {
                        int c = _order[k];
                        if (_docs[c] < target)
                        {
                            _docs[c] = _clauses[c].Advance(target);
                        }
                    }

                    continue;
                }
            }

            _current = pivotDoc;
            DocumentsReturned++;
            return pivotDoc;
        }
    }

    private int Finish()
    {
        _current = NoMoreDocs;
        return NoMoreDocs;
    }
}
