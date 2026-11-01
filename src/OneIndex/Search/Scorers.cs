using System.Numerics;

namespace OneIndex.Search;

// Small scorers that combine or wrap others. Each keeps the contract of Scorer: scores are sums added in a fixed order (the order
// of the clauses), so an exhaustive evaluation and a pruned one produce bit-identical scores for the same document.

/// <summary>Multiplies another scorer's scores by a boost. The boost may be 0 (every score is then 0).</summary>
internal sealed class ScaledScorer : Scorer
{
    private readonly Scorer _inner;
    private readonly float _boost;

    public ScaledScorer(Scorer inner, float boost)
    {
        _inner = inner;
        _boost = boost;
    }

    public override int DocId => _inner.DocId;

    public override long Cost => _inner.Cost;

    public override float MaxScore => _boost == 0f ? 0f : _inner.MaxScore * _boost;

    public override int NextDoc() => _inner.NextDoc();

    public override int Advance(int target) => _inner.Advance(target);

    public override float Score() => _inner.Score() * _boost;

    public override int AdvanceShallow(int target) => _inner.AdvanceShallow(target);

    public override float GetMaxScore(int upTo) => _boost == 0f ? 0f : _inner.GetMaxScore(upTo) * _boost;

    public override void SetMinCompetitiveScore(float minScore)
    {
        if (_boost > 0f)
        {
            _inner.SetMinCompetitiveScore(minScore / _boost);
        }
    }
}

/// <summary>Gives every document of an iterator the same score (filters, ranges, match-all).</summary>
internal sealed class ConstantScorer : Scorer
{
    private readonly DocIdSetIterator _it;
    private readonly float _score;
    private bool _finished;

    public ConstantScorer(DocIdSetIterator it, float score)
    {
        _it = it;
        _score = score;
    }

    public override int DocId => _finished ? NoMoreDocs : _it.DocId;

    public override long Cost => _it.Cost;

    public override float MaxScore => _score;

    public override int NextDoc() => _finished ? NoMoreDocs : _it.NextDoc();

    public override int Advance(int target) => _finished ? NoMoreDocs : _it.Advance(target);

    // Every document scores the same, so once nothing at this score can compete the rest are all skipped.
    public override void SetMinCompetitiveScore(float minScore)
    {
        if (minScore > _score)
        {
            _finished = true;
        }
    }

    public override float Score() => _score;

    public override float GetMaxScore(int upTo) => _score;
}

/// <summary>Every document id from 0 up to a bound.</summary>
internal sealed class AllDocsIterator : DocIdSetIterator
{
    private readonly int _maxDoc;
    private int _doc = -1;

    public AllDocsIterator(int maxDoc) => _maxDoc = maxDoc;

    public override int DocId => _doc;

    public override long Cost => _maxDoc;

    public override int NextDoc() => _doc = _doc + 1 >= _maxDoc ? NoMoreDocs : _doc + 1;

    public override int Advance(int target) => _doc = target >= _maxDoc ? NoMoreDocs : target;
}

/// <summary>The documents whose bit is set, in increasing order.</summary>
internal sealed class BitSetIterator : DocIdSetIterator
{
    private readonly ulong[] _words;
    private readonly long _cost;
    private int _doc = -1;

    public BitSetIterator(ulong[] words)
    {
        _words = words;
        long c = 0;
        foreach (ulong w in words)
        {
            c += BitOperations.PopCount(w);
        }

        _cost = c;
    }

    public override int DocId => _doc;

    public override long Cost => _cost;

    public override int NextDoc() => Advance(_doc + 1);

    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
        }

        int w = target >> 6;
        if (w >= _words.Length)
        {
            return _doc = NoMoreDocs;
        }

        ulong bits = _words[w] & (ulong.MaxValue << (target & 63));
        while (true)
        {
            if (bits != 0)
            {
                return _doc = (w << 6) + BitOperations.TrailingZeroCount(bits);
            }

            if (++w >= _words.Length)
            {
                return _doc = NoMoreDocs;
            }

            bits = _words[w];
        }
    }
}

/// <summary>Documents that every clause matches; clauses marked as scoring add their scores, the others (filters) only restrict.</summary>
internal sealed class ConjunctionScorer : Scorer
{
    private readonly Scorer[] _clauses;
    private readonly bool[] _scoring;
    private readonly Scorer[] _byCost;
    private int _doc = -1;

    public ConjunctionScorer(Scorer[] clauses, bool[] scoring)
    {
        _clauses = clauses;
        _scoring = scoring;
        _byCost = [.. clauses];
        Array.Sort(_byCost, static (a, b) => a.Cost.CompareTo(b.Cost));
    }

    public override int DocId => _doc;

    public override long Cost => _byCost[0].Cost;

    public override float MaxScore
    {
        get
        {
            float sum = 0f;
            for (int i = 0; i < _clauses.Length; i++)
            {
                if (_scoring[i])
                {
                    sum += _clauses[i].MaxScore;
                }
            }

            return sum;
        }
    }

    public override float GetMaxScore(int upTo) => MaxScore;

    public override int NextDoc() => _doc == NoMoreDocs ? NoMoreDocs : Align(_byCost[0].NextDoc());

    public override int Advance(int target) => _doc == NoMoreDocs ? NoMoreDocs : Align(_byCost[0].DocId < target ? _byCost[0].Advance(target) : _byCost[0].DocId);

    public override float Score()
    {
        float sum = 0f;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_scoring[i])
            {
                sum += _clauses[i].Score();
            }
        }

        return sum;
    }

    // Leapfrog: the cheapest clause proposes, the others advance to it; a clause that overshoots becomes the proposal.
    private int Align(int doc)
    {
        while (true)
        {
            if (doc == NoMoreDocs)
            {
                return _doc = NoMoreDocs;
            }

            bool agreed = true;
            for (int i = 1; i < _byCost.Length; i++)
            {
                Scorer s = _byCost[i];
                int d = s.DocId < doc ? s.Advance(doc) : s.DocId;
                if (d != doc)
                {
                    doc = d == NoMoreDocs ? NoMoreDocs : _byCost[0].Advance(d);
                    agreed = false;
                    break;
                }
            }

            if (agreed)
            {
                return _doc = doc;
            }
        }
    }
}

/// <summary>Documents the required scorer matches; the optional scorer adds to the score where it matches the same document.</summary>
internal sealed class ReqOptScorer : Scorer
{
    private readonly Scorer _required;
    private readonly Scorer _optional;

    public ReqOptScorer(Scorer required, Scorer optional)
    {
        _required = required;
        _optional = optional;
    }

    public override int DocId => _required.DocId;

    public override long Cost => _required.Cost;

    public override float MaxScore => _required.MaxScore + _optional.MaxScore;

    public override float GetMaxScore(int upTo) => MaxScore;

    public override int NextDoc() => _required.NextDoc();

    public override int Advance(int target) => _required.Advance(target);

    public override float Score()
    {
        int doc = _required.DocId;
        float score = _required.Score();
        if (_optional.DocId < doc)
        {
            _optional.Advance(doc);
        }

        return _optional.DocId == doc ? score + _optional.Score() : score;
    }
}

/// <summary>Documents the inner scorer matches that none of the excluded scorers match.</summary>
internal sealed class ExclusionScorer : Scorer
{
    private readonly Scorer _inner;
    private readonly Scorer[] _excluded;

    public ExclusionScorer(Scorer inner, Scorer[] excluded)
    {
        _inner = inner;
        _excluded = excluded;
    }

    public override int DocId => _inner.DocId;

    public override long Cost => _inner.Cost;

    public override float MaxScore => _inner.MaxScore;

    public override float GetMaxScore(int upTo) => _inner.MaxScore;

    public override float Score() => _inner.Score();

    // Excluded documents are never collected, so a threshold built from collected documents is still safe for the inner scorer.
    public override void SetMinCompetitiveScore(float minScore) => _inner.SetMinCompetitiveScore(minScore);

    public override int NextDoc() => Skip(_inner.NextDoc());

    public override int Advance(int target) => Skip(_inner.Advance(target));

    private int Skip(int doc)
    {
        while (doc != NoMoreDocs && IsExcluded(doc))
        {
            doc = _inner.NextDoc();
        }

        return doc;
    }

    private bool IsExcluded(int doc)
    {
        foreach (Scorer e in _excluded)
        {
            if (e.DocId < doc)
            {
                e.Advance(doc);
            }

            if (e.DocId == doc)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A disjunction that matches only documents at least a given number of its clauses match. Not pruned: the count needs every clause.</summary>
internal sealed class MinShouldMatchScorer : Scorer
{
    private readonly Scorer[] _clauses;
    private readonly int[] _docs;
    private readonly int _min;
    private readonly long _cost;
    private int _doc = -1;
    private bool _started;

    public MinShouldMatchScorer(Scorer[] clauses, int minimum)
    {
        _clauses = clauses;
        _min = minimum;
        _docs = new int[clauses.Length];
        Array.Fill(_docs, -1); // so that Advance before the first NextDoc positions every clause
        foreach (Scorer c in clauses)
        {
            _cost += c.Cost;
        }
    }

    public override int DocId => _doc;

    public override long Cost => _cost;

    public override float MaxScore
    {
        get
        {
            float sum = 0f;
            foreach (Scorer c in _clauses)
            {
                sum += c.MaxScore;
            }

            return sum;
        }
    }

    public override float GetMaxScore(int upTo) => MaxScore;

    public override int NextDoc()
    {
        if (_doc == NoMoreDocs)
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
                if (_docs[i] == _doc)
                {
                    _docs[i] = _clauses[i].NextDoc();
                }
            }
        }

        return Find();
    }

    public override int Advance(int target)
    {
        if (_doc == NoMoreDocs)
        {
            return NoMoreDocs;
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

    public override float Score()
    {
        float sum = 0f;
        for (int i = 0; i < _clauses.Length; i++)
        {
            if (_docs[i] == _doc)
            {
                sum += _clauses[i].Score();
            }
        }

        return sum;
    }

    private int Find()
    {
        while (true)
        {
            int min = NoMoreDocs;
            foreach (int d in _docs)
            {
                if (d < min)
                {
                    min = d;
                }
            }

            if (min == NoMoreDocs)
            {
                return _doc = NoMoreDocs;
            }

            int count = 0;
            for (int i = 0; i < _docs.Length; i++)
            {
                if (_docs[i] == min)
                {
                    count++;
                }
            }

            if (count >= _min)
            {
                return _doc = min;
            }

            for (int i = 0; i < _clauses.Length; i++)
            {
                if (_docs[i] == min)
                {
                    _docs[i] = _clauses[i].NextDoc();
                }
            }
        }
    }
}
