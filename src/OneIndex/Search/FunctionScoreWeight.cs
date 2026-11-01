using System.Globalization;

namespace OneIndex.Search;

/// <summary>
/// Runs the functions of a <see cref="FunctionScoreQuery"/> for one segment. The scorer and explain both go through <see cref="TryScore"/>,
/// so an explanation's value is the scorer's value bit for bit.
/// </summary>
internal sealed class FunctionEvaluator
{
    private readonly FunctionScoreQuery _query;
    private readonly ScoreFunction[] _functions;
    private readonly LeafFunction[] _bound;
    private readonly Scorer?[] _filters;
    private readonly bool[] _filterless;
    private readonly float[] _values;
    private readonly float[] _weights;

    public FunctionEvaluator(FunctionScoreQuery query, Weight?[] filterWeights, LeafContext leaf)
    {
        _query = query;
        _functions = [.. query.Functions];
        _bound = new LeafFunction[_functions.Length];
        _filters = new Scorer?[_functions.Length];
        _filterless = new bool[_functions.Length];
        for (int i = 0; i < _functions.Length; i++)
        {
            _bound[i] = _functions[i].BindLeaf(leaf);
            _filterless[i] = filterWeights[i] is null;
            _filters[i] = filterWeights[i]?.GetScorer(leaf);
        }

        _values = new float[_functions.Length];
        _weights = new float[_functions.Length];
    }

    /// <summary>
    /// Scores a document. Documents must be offered in increasing order (the filters advance). Returns <see langword="false"/> when
    /// <see cref="FunctionScoreQuery.MinScore"/> drops the document. <paramref name="trace"/>, when given, receives one explanation per function that applied.
    /// </summary>
    public bool TryScore(int docId, float innerScore, out float score, List<Explanation>? trace = null)
    {
        int n = 0;
        for (int i = 0; i < _functions.Length; i++)
        {
            if (!_filterless[i] && !Matches(_filters[i], docId))
            {
                continue;
            }

            if (!_bound[i].TryGet(docId, out double raw))
            {
                continue;
            }

            float weight = _functions[i].Weight;
            float value = (float)(raw * weight);
            if (!float.IsFinite(value))
            {
                continue;
            }

            _values[n] = value;
            _weights[n] = weight;
            n++;
            trace?.Add(new Explanation(value, $"{_functions[i].Describe()}: {Fmt((float)raw)} ({_bound[i].Explain(docId, raw)}) × weight {Fmt(weight)}"));
        }

        score = Combine(_query, innerScore, n, out _);
        if (_query.MinScore is { } min && score < min)
        {
            return false;
        }

        return true;
    }

    private float Combine(FunctionScoreQuery q, float innerScore, int n, out float functionScore)
    {
        if (n == 0)
        {
            functionScore = float.NaN;
            return Finite(innerScore * q.Boost);
        }

        float combined;
        switch (q.ScoreMode)
        {
            case FunctionScoreMode.Multiply:
                combined = 1f;
                for (int i = 0; i < n; i++)
                {
                    combined *= _values[i];
                }

                break;
            case FunctionScoreMode.Sum:
                combined = 0f;
                for (int i = 0; i < n; i++)
                {
                    combined += _values[i];
                }

                break;
            case FunctionScoreMode.Average:
                float sum = 0f;
                float weights = 0f;
                for (int i = 0; i < n; i++)
                {
                    sum += _values[i];
                    weights += _weights[i];
                }

                combined = weights == 0f ? 0f : sum / weights;
                break;
            case FunctionScoreMode.First:
                combined = _values[0];
                break;
            case FunctionScoreMode.Max:
                combined = _values[0];
                for (int i = 1; i < n; i++)
                {
                    combined = Math.Max(combined, _values[i]);
                }

                break;
            default:
                combined = _values[0];
                for (int i = 1; i < n; i++)
                {
                    combined = Math.Min(combined, _values[i]);
                }

                break;
        }

        combined = Math.Min(Finite(combined), q.MaxBoost);
        functionScore = combined;
        float result = q.BoostMode switch
        {
            FunctionBoostMode.Multiply => innerScore * combined,
            FunctionBoostMode.Replace => combined,
            FunctionBoostMode.Sum => innerScore + combined,
            FunctionBoostMode.Average => (innerScore + combined) / 2f,
            FunctionBoostMode.Max => Math.Max(innerScore, combined),
            _ => Math.Min(innerScore, combined),
        };
        return Finite(result * q.Boost);
    }

    /// <summary>The explanation tree of a document whose inner explanation is <paramref name="inner"/>; <see langword="null"/> when the document is dropped.</summary>
    public Explanation? Explain(int docId, Explanation inner)
    {
        var trace = new List<Explanation>();
        if (!TryScore(docId, inner.Value, out float score, trace))
        {
            return null;
        }

        string boost = Fmt(_query.Boost);
        if (trace.Count == 0)
        {
            return new Explanation(score, $"function score: no function applies, query score kept, boost {boost}", inner);
        }

        // Recompute the combined function score for display (same code path, same numbers).
        _ = Combine(_query, inner.Value, trace.Count, out float functionScore);
        var functions = new Explanation(
            functionScore,
            $"function score, {FunctionNames.ScoreModes[(int)_query.ScoreMode]} of {trace.Count} function(s), capped at {Fmt(_query.MaxBoost)}",
            [.. trace]);
        return new Explanation(score, $"function score: query score {Fmt(inner.Value)} and function score {Fmt(functionScore)} by {FunctionNames.BoostModes[(int)_query.BoostMode]}, boost {boost}", inner, functions);
    }

    private static bool Matches(Scorer? filter, int docId)
    {
        if (filter is null)
        {
            return false;
        }

        int d = filter.DocId;
        if (d < docId)
        {
            d = filter.Advance(docId);
        }

        return d == docId;
    }

    // Scores stay finite whatever the functions do.
    private static float Finite(float x) => float.IsNaN(x) ? 0f : float.IsPositiveInfinity(x) ? float.MaxValue : float.IsNegativeInfinity(x) ? 0f : x;

    private static string Fmt(float x) => x.ToString("G9", CultureInfo.InvariantCulture);
}

internal sealed class FunctionScoreWeight : Weight
{
    private readonly FunctionScoreQuery _query;
    private readonly Weight _inner;
    private readonly Weight?[] _filters;

    public FunctionScoreWeight(FunctionScoreQuery query, Weight inner, Weight?[] filters)
    {
        _query = query;
        _inner = inner;
        _filters = filters;
    }

    public override Scorer? GetScorer(LeafContext leaf)
    {
        Scorer? inner = _inner.GetScorer(leaf);
        return inner is null ? null : new FunctionScoreScorer(inner, new FunctionEvaluator(_query, _filters, leaf), _query.MinScore is not null);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        Explanation? inner = _inner.Explain(leaf, docId);
        return inner is null ? null : new FunctionEvaluator(_query, _filters, leaf).Explain(docId, inner);
    }
}

internal sealed class FunctionScoreScorer : Scorer
{
    private readonly Scorer _inner;
    private readonly FunctionEvaluator _evaluator;
    private readonly bool _dropping;
    private int _scoredDoc = -1;
    private float _score;

    public FunctionScoreScorer(Scorer inner, FunctionEvaluator evaluator, bool dropping)
    {
        _inner = inner;
        _evaluator = evaluator;
        _dropping = dropping;
    }

    public override int DocId => _inner.DocId;

    public override long Cost => _inner.Cost;

    public override int NextDoc() => Settle(_inner.NextDoc());

    public override int Advance(int target) => Settle(_inner.Advance(target));

    public override float Score()
    {
        int doc = _inner.DocId;
        if (_scoredDoc != doc)
        {
            _ = _evaluator.TryScore(doc, _inner.Score(), out _score);
            _scoredDoc = doc;
        }

        return _score;
    }

    // With a minimum score, documents under it are skipped here, so the iterator only ever shows documents that match.
    private int Settle(int doc)
    {
        if (!_dropping)
        {
            return doc;
        }

        while (doc != NoMoreDocs)
        {
            if (_evaluator.TryScore(doc, _inner.Score(), out _score))
            {
                _scoredDoc = doc;
                return doc;
            }

            doc = _inner.NextDoc();
        }

        return doc;
    }
}
