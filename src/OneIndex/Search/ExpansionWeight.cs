using System.Buffers;
using System.Numerics;
using OneIndex.Codecs;

namespace OneIndex.Search;

/// <summary>
/// The weight of a prefix, fuzzy or regular-expression query after the dictionary expanded it to many terms. A document scores the
/// sum of its matching terms' scores, in term order (docs/scoring.md), exactly as a disjunction of term queries would.
/// </summary>
/// <remarks>
/// A disjunction of hundreds of term scorers pays per term for an iterator, a weight and a priority-queue entry, though most terms
/// match a handful of documents. Term-at-a-time evaluation pays per posting instead: each term's postings are read once through one
/// reused iterator and added into a dense accumulator, which is then compacted to the matching documents. Adding the terms one
/// after the other in term order gives every document the same sum, bit for bit, as the disjunction's.
/// </remarks>
internal sealed class ExpansionWeight : Weight
{
    // The accumulator holds one float per document of the segment; segments past this size use the plain disjunction instead.
    // Fewer terms than this are not worth the dense accumulator; the plain disjunction prunes with block maxima instead.
    public const int MinTerms = 4;

    private const int MaxDenseDocs = 1 << 22;

    private readonly Bm25FField _field;
    private readonly (float Idf, TermInfo Info)[]?[] _perLeaf;
    private readonly float _k1;
    private readonly float _boost;
    private readonly Lazy<Weight> _disjunction;

    public ExpansionWeight(Bm25FField field, (float Idf, TermInfo Info)[]?[] perLeaf, float k1, float boost, Func<Weight> disjunction)
    {
        _field = field;
        _perLeaf = perLeaf;
        _k1 = k1;
        _boost = boost;
        _disjunction = new Lazy<Weight>(disjunction);
    }

    public override Scorer? GetScorer(LeafContext leaf)
    {
        if (_perLeaf[leaf.Ordinal] is not { Length: > 0 } terms || leaf.Reader.GetTextField(_field.Name) is not { } reader)
        {
            return null;
        }

        int maxDoc = leaf.Reader.MaxDoc;
        if (maxDoc > MaxDenseDocs)
        {
            return _disjunction.Value.GetScorer(leaf);
        }

        int words = (maxDoc + 63) >> 6;
        float[] acc = ArrayPool<float>.Shared.Rent(maxDoc);
        ulong[] seen = ArrayPool<ulong>.Shared.Rent(words);
        Array.Clear(seen, 0, words);
        PostingsIterator? it = null;
        try
        {
            foreach ((float scale, TermInfo info) in terms)
            {

                // One iterator is reused for every term of the segment: Reset repoints it without allocating its block buffers.
                if (it is TermPostings reusable)
                {
                    reusable.Reset(info, PostingsFeatures.Frequencies);
                }
                else
                {
                    it = reader.GetPostings(info, PostingsFeatures.Frequencies);
                }

                for (int d = it!.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
                {
                    float x = _field.Weighted(it.Frequency, reader.GetFieldLength(d));
                    float s = Bm25.Saturate(x, _k1, scale);
                    if ((seen[d >> 6] & (1UL << (d & 63))) == 0)
                    {
                        seen[d >> 6] |= 1UL << (d & 63);
                        acc[d] = s;
                    }
                    else
                    {
                        acc[d] += s;
                    }
                }
            }

            int count = 0;
            for (int w = 0; w < words; w++)
            {
                count += BitOperations.PopCount(seen[w]);
            }

            if (count == 0)
            {
                return null;
            }

            var docs = new int[count];
            var scores = new float[count];
            int n = 0;
            for (int w = 0; w < words; w++)
            {
                ulong bits = seen[w];
                while (bits != 0)
                {
                    int d = (w << 6) + BitOperations.TrailingZeroCount(bits);
                    bits &= bits - 1;
                    docs[n] = d;
                    scores[n++] = acc[d];
                }
            }

            Scorer scorer = new SortedDocScorer(docs, scores);
            return _boost == 1f ? scorer : new ScaledScorer(scorer, _boost);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(acc);
            ArrayPool<ulong>.Shared.Return(seen);
        }
    }

    public override Explanation? Explain(LeafContext leaf, int docId) => _disjunction.Value.Explain(leaf, docId);
}

/// <summary>The weight of an expansion whose matches all score the boost: the union of the matching terms' postings.</summary>
internal sealed class ConstantExpansionWeight(string field, TermInfo[]?[] perLeaf, float boost) : Weight
{
    public override Scorer? GetScorer(LeafContext leaf)
    {
        if (perLeaf[leaf.Ordinal] is not { Length: > 0 } terms || leaf.Reader.GetTextField(field) is not { } reader)
        {
            return null;
        }

        var bits = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
        PostingsIterator? it = null;
        foreach (TermInfo info in terms)
        {
            if (it is TermPostings reusable)
            {
                reusable.Reset(info, PostingsFeatures.None);
            }
            else
            {
                it = reader.GetPostings(info, PostingsFeatures.None);
            }

            for (int d = it!.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
            {
                bits[d >> 6] |= 1UL << (d & 63);
            }
        }

        return new ConstantScorer(new BitSetIterator(bits), boost);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        Scorer? s = GetScorer(leaf);
        return s is not null && s.Advance(docId) == docId ? new Explanation(boost, "expanded prefix, constant score = boost") : null;
    }
}

/// <summary>Documents and scores already computed, in increasing document order.</summary>
internal sealed class SortedDocScorer : Scorer
{
    private readonly int[] _docs;
    private readonly float[] _scores;
    private readonly float _max;
    private int _index = -1;
    private int _doc = -1;

    public SortedDocScorer(int[] docs, float[] scores)
    {
        _docs = docs;
        _scores = scores;
        float m = 0f;
        foreach (float s in scores)
        {
            if (s > m)
            {
                m = s;
            }
        }

        _max = m;
    }

    public override int DocId => _doc;

    public override long Cost => _docs.Length;

    public override float MaxScore => _max;

    public override float GetMaxScore(int upTo) => _max;

    public override int NextDoc()
    {
        if (++_index >= _docs.Length)
        {
            _index = _docs.Length;
            return _doc = NoMoreDocs;
        }

        return _doc = _docs[_index];
    }

    public override int Advance(int target)
    {
        if (_doc >= target)
        {
            return _doc;
        }

        int from = Math.Max(_index + 1, 0);
        int i = Array.BinarySearch(_docs, from, _docs.Length - from, target);
        if (i < 0)
        {
            i = ~i;
        }

        _index = i;
        return _doc = i >= _docs.Length ? NoMoreDocs : _docs[i];
    }

    public override float Score() => _scores[_index];
}

/// <summary>One distinct term of an expansion with its document frequency summed over the segments.</summary>
internal sealed class ExpandedTerm(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;

    public long DocFreq { get; set; }

    public bool Dropped { get; set; }
}

/// <summary>Hashes term bytes, and looks them up from a span without copying. The hash lives only in memory and is never persisted or iterated in hash order.</summary>
internal sealed class TermKeyComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
{
    public static TermKeyComparer Instance { get; } = new();

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) => GetHashCode(obj.AsSpan());

    public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

    public int GetHashCode(ReadOnlySpan<byte> alternate)
    {
        var h = default(HashCode);
        h.AddBytes(alternate);
        return h.ToHashCode();
    }

    public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();
}
