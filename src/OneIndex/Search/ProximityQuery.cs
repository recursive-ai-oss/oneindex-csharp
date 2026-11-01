using System.Globalization;

namespace OneIndex.Search;

/// <summary>
/// Adds a term-proximity bonus to another query's score: documents where the query's words occur close together score higher
/// (Büttcher, Clarke and Lushman, SIGIR 2006; formula in docs/scoring.md). The bonus is
/// <c>Weight * max over fields of sum over words of min(1, idf) * acc * (k1 + 1) / (acc + K)</c>, where K is the BM25 length normalisation of that field and <c>acc</c> accumulates idf(other) / distance² for
/// every pair of adjacent occurrences of two different words. It is at most <c>Weight * (k1 + 1) * sum of min(1, idf)</c>, which is the bound
/// block-max WAND uses, so top-k results equal exhaustive scoring.
/// </summary>
/// <param name="Inner">The query that matches and scores; the bonus is added to its score for documents that it matches.</param>
/// <param name="Words">The single-word queries (<see cref="TermQuery"/>, <see cref="MultiFieldTermQuery"/> or <see cref="TypoTermQuery"/>) whose occurrences are compared. Fewer than two words add no bonus; a repeated word counts once and at most the 32 rarest words are compared.</param>
public sealed record ProximityQuery(Query Inner, IReadOnlyList<Query> Words) : Query
{
    /// <summary>The bonus' weight relative to word scores, 0 to 4. Default 0.1. 0 adds nothing.</summary>
    public float Weight { get; init; } = Proximity.DefaultWeight;

    /// <summary>Compares the words one by one (the generated record equality would compare the list by reference).</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(ProximityQuery? other)
        => other is not null && Boost.Equals(other.Boost) && Weight.Equals(other.Weight) && Inner.Equals(other.Inner) && Words.SequenceEqual(other.Words);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Inner);
        hash.Add(Weight);
        hash.Add(Boost);
        foreach (Query w in Words)
        {
            hash.Add(w);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitProximity(this);
}

internal sealed class ProximityWeight : Weight
{
    /// <summary>The most words one proximity bonus compares; the rarest are kept.</summary>
    public const int MaxWords = 32;

    private readonly Weight _inner;
    private readonly SearchContext _context;
    private readonly WordFields _fields;
    private readonly QueryWord[] _words;
    private readonly float[] _idf;
    private readonly float[] _cap; // min(1, idf): the paper limits a word's weight in the bonus to 1
    private readonly float[] _averageLength; // per field
    private readonly float _k1;
    private readonly float _b;
    private readonly float _weight;
    private readonly float _boost;
    private readonly float _maxBonus;

    public ProximityWeight(Weight inner, SearchContext context, WordFields fields, QueryWord[] words, float k1, float b, float weight, float boost)
    {
        _inner = inner;
        _context = context;
        _fields = fields;
        _words = words;
        _k1 = k1;
        _b = b;
        _weight = weight;
        _boost = boost;
        _idf = new float[words.Length];
        _cap = new float[words.Length];
        float sum = 0f;
        for (int i = 0; i < words.Length; i++)
        {
            _idf[i] = words[i].Idf;
            _cap[i] = Math.Min(1f, _idf[i]);
            sum += _cap[i];
        }

        _averageLength = new float[fields.Count];
        for (int f = 0; f < _averageLength.Length; f++)
        {
            FieldStatistics stats = context.GetFieldStatistics(fields.Names[f]);
            _averageLength[f] = stats.DocCount > 0 && stats.SumTotalTermFreq > 0 ? (float)((double)stats.SumTotalTermFreq / stats.DocCount) : 1f;
        }

        // acc * (k1 + 1) / (acc + K) < k1 + 1 for every word, so the bonus stays under weight * (k1 + 1) * sum of min(1, idf).
        _maxBonus = weight * (k1 + 1f) * sum;
    }

    public override Scorer? GetScorer(LeafContext leaf)
    {
        Scorer? inner = _inner.GetScorer(leaf);
        if (inner is null)
        {
            return null;
        }

        Scorer scorer = new ProximityScorer(inner, new LeafWordMatcher(leaf, _words, _fields, positions: true), this);
        return _boost == 1f ? scorer : new ScaledScorer(scorer, _boost);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        Explanation? inner = _inner.Explain(leaf, docId);
        if (inner is null)
        {
            return null;
        }

        var matcher = new LeafWordMatcher(leaf, _words, _fields, positions: true);
        matcher.Load(docId);
        float bonus = Bonus(matcher, out int bestField);
        var details = new List<Explanation> { inner };
        var parts = new List<Explanation>();
        if (bestField >= 0)
        {
            Span<float> acc = stackalloc float[MaxWords]; // bounded by MaxWords
            Span<int> cursor = stackalloc int[acc.Length];
            _ = Proximity.FieldBonus(matcher, bestField, _idf, _cap, _k1, Norm(matcher, bestField), acc, cursor);
            for (int w = 0; w < _words.Length; w++)
            {
                if (acc[w] > 0f)
                {
                    parts.Add(new Explanation(_cap[w] * ((_k1 + 1f) * acc[w] / (acc[w] + Norm(matcher, bestField))), $"word '{_words[w].Text}': min(1, idf) {Fmt(_cap[w])} * acc * (k1 + 1) / (acc + K), acc = {Fmt(acc[w])}, K = {Fmt(Norm(matcher, bestField))}"));
                }
            }
        }

        string where = bestField >= 0 ? $" in field {_fields.Names[bestField]}" : string.Empty;
        details.Add(new Explanation(bonus, $"proximity{where}: weight {Fmt(_weight)} * sum over words of min(1, idf) * acc * (k1 + 1) / (acc + K), acc = sum of idf(other word) / distance² over adjacent pairs", [.. parts]));
        float total = inner.Value + bonus;
        Explanation sum = new(total, "sum of the text score and the proximity bonus", [.. details]);
        if (_boost == 1f)
        {
            return sum;
        }

        return new Explanation(total * _boost, $"proximity query: score * boost {Fmt(_boost)}", [sum]);
    }

    /// <summary>The bonus of the document the matcher has loaded.</summary>
    public float Bonus(LeafWordMatcher matcher, out int bestField)
    {
        bestField = -1;
        if (_words.Length < 2 || _weight == 0f || matcher.MatchedWords < 2)
        {
            return 0f; // one word alone has nothing to be near: positions are not even read
        }

        matcher.EnsurePositions();

        Span<float> acc = stackalloc float[MaxWords]; // bounded by MaxWords
        Span<int> cursor = stackalloc int[acc.Length];
        float best = 0f;
        for (int f = 0; f < _fields.Count; f++)
        {
            float b = Proximity.FieldBonus(matcher, f, _idf, _cap, _k1, Norm(matcher, f), acc, cursor);
            if (b > best)
            {
                best = b;
                bestField = f;
            }
        }

        return best * _weight;
    }

    // K = k1 * (1 - b + b * length / average length) of the field in the loaded document.
    private float Norm(LeafWordMatcher matcher, int field) => _k1 * (1f - _b + (_b * matcher.FieldLength(field) / _averageLength[field]));

    public float MaxBonus => _maxBonus;

    internal static string Fmt(float v) => v.ToString("G9", CultureInfo.InvariantCulture);
}

internal sealed class ProximityScorer : Scorer
{
    private readonly Scorer _inner;
    private readonly LeafWordMatcher _matcher;
    private readonly ProximityWeight _weight;
    private int _scored = -1;
    private float _score;

    public ProximityScorer(Scorer inner, LeafWordMatcher matcher, ProximityWeight weight)
    {
        _inner = inner;
        _matcher = matcher;
        _weight = weight;
    }

    public override int DocId => _inner.DocId;

    public override long Cost => _inner.Cost;

    public override float MaxScore => _inner.MaxScore + _weight.MaxBonus;

    public override int NextDoc() => _inner.NextDoc();

    public override int Advance(int target) => _inner.Advance(target);

    public override int AdvanceShallow(int target) => _inner.AdvanceShallow(target);

    public override float GetMaxScore(int upTo) => _inner.GetMaxScore(upTo) + _weight.MaxBonus;

    // A document must beat the threshold with the best bonus it could have, so the inner scorer may skip what cannot reach it.
    public override void SetMinCompetitiveScore(float minScore)
    {
        float inner = minScore - _weight.MaxBonus;
        if (inner > 0f)
        {
            _inner.SetMinCompetitiveScore(inner);
        }
    }

    public override float Score()
    {
        int doc = _inner.DocId;
        if (_scored != doc)
        {
            float text = _inner.Score();
            _matcher.Load(doc);
            _score = text + _weight.Bonus(_matcher, out _);
            _scored = doc;
        }

        return _score;
    }
}
