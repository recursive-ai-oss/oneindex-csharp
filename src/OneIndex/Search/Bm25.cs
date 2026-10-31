namespace OneIndex.Search;

/// <summary>
/// The two free parameters of BM25 and BM25F (Robertson and Zaragoza 2009, sections 3.4.4 and 3.4.5): <see cref="K1"/> sets how fast the
/// score saturates as a term repeats, <see cref="B"/> how strongly a field longer than average is discounted.
/// </summary>
/// <param name="K1">Term-frequency saturation, finite and not negative. 0 makes the score binary.</param>
/// <param name="B">Length normalization from 0 (none) to 1 (full).</param>
public readonly record struct Bm25Parameters(float K1, float B)
{
    /// <summary>
    /// k1 = 0.9 and b = 0.4, the engine's default. These are the values of the BEIR lexical baseline, and on the four BEIR gate sets they
    /// score within a point or two of it while 1.2 / 0.75 (Lucene's defaults) is better on ArguAna and worse elsewhere (docs/scoring.md,
    /// results table); the paper gives no rule and says only that 0.5 &lt; b &lt; 0.8 and 1.2 &lt; k1 &lt; 2 are often good (section 3.5).
    /// </summary>
    public static Bm25Parameters Default => new(0.9f, 0.4f);

    /// <summary>Checks the ranges.</summary>
    /// <exception cref="QueryException">A parameter is out of range.</exception>
    public void Validate()
    {
        if (!float.IsFinite(K1) || K1 < 0f)
        {
            throw new QueryException($"BM25 k1 must be finite and not negative, got {K1}.");
        }

        if (!(B >= 0f && B <= 1f))
        {
            throw new QueryException($"BM25 b must be between 0 and 1, got {B}.");
        }
    }
}

/// <summary>One field's contribution to a BM25F term: its weight and the constants of its length normalization.</summary>
internal readonly record struct Bm25FField(string Name, float Weight, float C0, float C1, float AverageLength)
{
    public static Bm25FField Create(string name, float weight, FieldStatistics stats, Bm25Parameters p)
    {
        float avg = stats.DocCount > 0 && stats.SumTotalTermFreq > 0 ? (float)((double)stats.SumTotalTermFreq / stats.DocCount) : 1f;
        return new Bm25FField(name, weight, 1f - p.B, p.B / avg, avg);
    }

    /// <summary>The field's weighted, length-normalized frequency (the paper's per-field term of the pseudo-frequency).</summary>
    public float Weighted(float frequency, int fieldLength) => Weight * frequency / Math.Max(C0 + (C1 * fieldLength), Bm25.MinNorm);
}

internal static class Bm25
{
    // Guards the division when b = 1 and a field is empty; a posting always has a field length of at least 1.
    public const float MinNorm = 1e-6f;

    /// <summary>Robertson and Sparck Jones weight in the non-negative form ln(1 + (N - n + 0.5) / (n + 0.5)) (paper section 3.1, equation 3.2, and section 3.1 for the idf form 3.3 it reduces to without relevance information).</summary>
    public static float Idf(long docCount, long docFreq)
    {
        if (docFreq > docCount)
        {
            docCount = docFreq;
        }

        return (float)Math.Log(1.0 + ((docCount - docFreq + 0.5) / (docFreq + 0.5)));
    }

    /// <summary>The saturation of the weighted sum: scale * x / (k1 + x) (paper section 3.6.1: saturation is applied to the total evidence of the streams, equation 3.21).</summary>
    public static float Saturate(float x, float k1, float scale) => scale * (x / (k1 + x));
}

/// <summary>
/// Scores one term across one or more fields with BM25F: per field the frequency is weighted and divided by that field's length
/// normalization, the fields' values are added, and only the sum is saturated (Robertson and Zaragoza 2009, section 3.6, equations 3.19 to 3.21), then
/// multiplied by the term's idf and the query boost. The scorer walks the union of the fields' postings. Block bounds come from
/// the postings impacts: the weighted sum is largest for the largest frequency and shortest field of a block, and the score grows
/// with the sum, so the bound is the score of that combination.
/// </summary>
internal sealed class Bm25FTermScorer : Scorer
{
    private readonly PostingsIterator[] _its;
    private readonly ITextFieldReader[] _readers;
    private readonly Bm25FField[] _fields;
    private readonly BlockImpact[] _shallow;
    private readonly float _k1;
    private readonly float _scale;
    private int _doc = -1;

    public Bm25FTermScorer(PostingsIterator[] iterators, ITextFieldReader[] readers, Bm25FField[] fields, float k1, float scale)
    {
        _its = iterators;
        _readers = readers;
        _fields = fields;
        _k1 = k1;
        _scale = scale;
        _shallow = new BlockImpact[iterators.Length];
        for (int i = 0; i < iterators.Length; i++)
        {
            _shallow[i] = iterators[i].TermImpact;
        }
    }

    public override int DocId => _doc;

    public override long Cost
    {
        get
        {
            long c = 0;
            foreach (PostingsIterator it in _its)
            {
                c += it.Cost;
            }

            return c;
        }
    }

    public override float MaxScore
    {
        get
        {
            float x = 0f;
            for (int i = 0; i < _its.Length; i++)
            {
                BlockImpact b = _its[i].TermImpact;
                if (b.MaxFrequency > 0)
                {
                    x += _fields[i].Weighted(b.MaxFrequency, b.MinFieldLength);
                }
            }

            return x > 0f ? Bm25.Saturate(x, _k1, _scale) : 0f;
        }
    }

    public override int NextDoc()
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (_its.Length == 1)
        {
            return _doc = _its[0].NextDoc();
        }

        int min = NoMoreDocs;
        foreach (PostingsIterator it in _its)
        {
            int d = it.DocId == _doc ? it.NextDoc() : it.DocId;
            if (d < min)
            {
                min = d;
            }
        }

        return _doc = min;
    }

    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        if (_its.Length == 1)
        {
            return _doc = _its[0].Advance(target);
        }

        int min = NoMoreDocs;
        foreach (PostingsIterator it in _its)
        {
            int d = it.DocId < target ? it.Advance(target) : it.DocId;
            if (d < min)
            {
                min = d;
            }
        }

        return _doc = min;
    }

    public override float Score()
    {
        float x = 0f;
        for (int i = 0; i < _its.Length; i++)
        {
            PostingsIterator it = _its[i];
            if (it.DocId == _doc)
            {
                x += _fields[i].Weighted(it.Frequency, _readers[i].GetFieldLength(_doc));
            }
        }

        return Bm25.Saturate(x, _k1, _scale);
    }

    public override int AdvanceShallow(int target)
    {
        int end = NoMoreDocs;
        for (int i = 0; i < _its.Length; i++)
        {
            PostingsIterator it = _its[i];
            if (it.DocId == NoMoreDocs)
            {
                _shallow[i] = default;
                continue;
            }

            // A list already past the target has nothing between the target and its current document.
            _shallow[i] = it.ShallowAdvance(Math.Max(target, Math.Max(it.DocId, 0)));
            if (_shallow[i].LastDocId < end)
            {
                end = _shallow[i].LastDocId;
            }
        }

        return end;
    }

    public override float GetMaxScore(int upTo)
    {
        float x = 0f;
        for (int i = 0; i < _shallow.Length; i++)
        {
            BlockImpact b = _shallow[i];
            if (b.MaxFrequency > 0)
            {
                x += _fields[i].Weighted(b.MaxFrequency, b.MinFieldLength);
            }
        }

        return x > 0f ? Bm25.Saturate(x, _k1, _scale) : 0f;
    }
}

/// <summary>
/// Documents where the terms of a phrase occur in order within a slop. The phrase frequency is the sum, over the positions of the
/// first term, of 1 / (1 + gap) for the nearest in-order chain of the other terms, where the gap is the number of positions the
/// chain is stretched past an exact phrase and must not exceed the slop. That frequency is scored as the frequency of one
/// pseudo-term in the field, with the sum of the terms' idf values.
/// </summary>
internal sealed class PhraseScorer : Scorer
{
    private readonly PostingsIterator[] _its;
    private readonly int[] _byCost;
    private readonly ITextFieldReader _reader;
    private readonly Bm25FField _field;
    private readonly int _slop;
    private readonly float _k1;
    private readonly float _scale;
    private readonly float _maxScore;
    private readonly int[][] _positions;
    private readonly int[] _counts;
    private readonly int[] _pointers;
    private int _doc = -1;
    private float _frequency;

    public PhraseScorer(PostingsIterator[] iterators, ITextFieldReader reader, Bm25FField field, int slop, float k1, float scale)
    {
        _its = iterators;
        _reader = reader;
        _field = field;
        _slop = slop;
        _k1 = k1;
        _scale = scale;
        _byCost = [.. Enumerable.Range(0, iterators.Length).OrderBy(i => iterators[i].Cost).ThenBy(i => i)];
        _positions = new int[iterators.Length][];
        for (int i = 0; i < _positions.Length; i++)
        {
            _positions[i] = new int[8];
        }

        _counts = new int[iterators.Length];
        _pointers = new int[iterators.Length];

        // Exact phrases occur at most as often as their rarest term; a sloppy phrase is counted once per position of the first term.
        int maxFreq = int.MaxValue;
        int minLen = 0;
        for (int i = 0; i < iterators.Length; i++)
        {
            BlockImpact t = iterators[i].TermImpact;
            minLen = Math.Max(minLen, t.MinFieldLength);
            if (slop == 0 || i == 0)
            {
                maxFreq = Math.Min(maxFreq, t.MaxFrequency);
            }
        }

        _maxScore = maxFreq <= 0 ? 0f : Bm25.Saturate(_field.Weighted(maxFreq, minLen), k1, scale);
    }

    public override int DocId => _doc;

    public override long Cost => _its[_byCost[0]].Cost;

    public override float MaxScore => _maxScore;

    public override float GetMaxScore(int upTo) => _maxScore;

    public float Frequency => _frequency;

    public override int NextDoc() => _doc == NoMoreDocs ? NoMoreDocs : Find(_its[_byCost[0]].NextDoc());

    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        PostingsIterator lead = _its[_byCost[0]];
        return Find(lead.DocId < target ? lead.Advance(target) : lead.DocId);
    }

    public override float Score() => Bm25.Saturate(_field.Weighted(_frequency, _reader.GetFieldLength(_doc)), _k1, _scale);

    private int Find(int doc)
    {
        PostingsIterator lead = _its[_byCost[0]];
        while (true)
        {
            if (doc == NoMoreDocs)
            {
                return _doc = NoMoreDocs;
            }

            bool agreed = true;
            for (int k = 1; k < _byCost.Length; k++)
            {
                PostingsIterator it = _its[_byCost[k]];
                int d = it.DocId < doc ? it.Advance(doc) : it.DocId;
                if (d != doc)
                {
                    doc = d == NoMoreDocs ? NoMoreDocs : lead.Advance(d);
                    agreed = false;
                    break;
                }
            }

            if (!agreed)
            {
                continue;
            }

            _frequency = CountMatches();
            if (_frequency > 0f)
            {
                return _doc = doc;
            }

            doc = lead.NextDoc();
        }
    }

    private float CountMatches()
    {
        int n = _its.Length;
        for (int t = 0; t < n; t++)
        {
            PostingsIterator it = _its[t];
            int f = it.Frequency;
            if (_positions[t].Length < f)
            {
                _positions[t] = new int[Math.Max(f, _positions[t].Length * 2)];
            }

            int[] buffer = _positions[t];
            for (int j = 0; j < f; j++)
            {
                buffer[j] = it.NextPosition();
            }

            _counts[t] = f;
            _pointers[t] = 0;
        }

        float total = 0f;
        int[] first = _positions[0];
        for (int a = 0; a < _counts[0]; a++)
        {
            int start = first[a];
            int current = start;
            bool complete = true;
            for (int t = 1; t < n; t++)
            {
                int[] p = _positions[t];
                int ptr = _pointers[t];
                while (ptr < _counts[t] && p[ptr] <= current)
                {
                    ptr++;
                }

                _pointers[t] = ptr;
                if (ptr == _counts[t])
                {
                    complete = false;
                    break;
                }

                current = p[ptr];
            }

            if (!complete)
            {
                // A later start only pushes the chain further right: no more matches in this document.
                break;
            }

            int gap = current - start - (n - 1);
            if (gap <= _slop)
            {
                total += 1f / (1 + gap);
            }
        }

        return total;
    }
}
