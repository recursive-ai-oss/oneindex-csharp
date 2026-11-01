using OneIndex.Index;
using System.Text;

namespace OneIndex.Search;

/// <summary>Receives one term found in one segment: its bytes (valid during the call only) and its statistics there.</summary>
internal delegate void TermSink(ReadOnlySpan<byte> term, in TermInfo info);

/// <summary>One occurrence of a candidate term in one field of one segment, with the statistics needed to open its postings.</summary>
internal readonly record struct ExpansionHit(int Candidate, int Leaf, int Field, TermInfo Info);

/// <summary>
/// The terms that a multi-term query found across segments and fields, each stored once with its per-field document frequency and
/// the places (segment, field, statistics) it occurs. Opening the postings later needs no dictionary lookup.
/// </summary>
internal sealed class ExpansionSet(int fieldCount)
{
    private readonly Dictionary<byte[], int> _ids = new(ByteArrayEquality.Instance);

    /// <summary>The candidate terms in the order they were first seen.</summary>
    public List<byte[]> Terms { get; } = [];

    /// <summary>Per candidate, the document frequency in each field summed over segments.</summary>
    public List<long[]> DocFreq { get; } = [];

    /// <summary>Every occurrence.</summary>
    public List<ExpansionHit> Hits { get; } = [];

    /// <summary>Records an occurrence.</summary>
    public int Add(ReadOnlySpan<byte> term, int leaf, int field, in TermInfo info)
    {
        var lookup = _ids.GetAlternateLookup<ReadOnlySpan<byte>>();
        if (!lookup.TryGetValue(term, out int id))
        {
            id = Terms.Count;
            byte[] key = term.ToArray(); // one copy per distinct candidate
            Terms.Add(key);
            DocFreq.Add(new long[fieldCount]);
            _ids.Add(key, id);
        }

        DocFreq[id][field] += info.DocFreq;
        Hits.Add(new ExpansionHit(id, leaf, field, info));
        return id;
    }
}

/// <summary>A term chosen from an <see cref="ExpansionSet"/> to be scored.</summary>
internal sealed class ExpandedTerm
{
    /// <summary>The index in the set.</summary>
    public required int Candidate { get; init; }

    /// <summary>The fields the term is scored in, as a bit mask over the query's fields.</summary>
    public required uint Fields { get; init; }

    /// <summary>The boost: the query boost times the typo and exactness factors for typo matching, 1 otherwise.</summary>
    public required float Boost { get; init; }

    /// <summary>How the word matched, for the explanation; null for fuzzy expansions.</summary>
    public TypoMatch? Match { get; init; }
}

/// <summary>
/// A disjunction over many expanded terms, each scored like a <see cref="TermWeight"/> (BM25F over its fields, with its own idf) but
/// without a weight object, dictionary lookup or statistics pass per term: the statistics of each field are computed once, the idf
/// from the document frequencies the expansion already collected, and the postings are opened from the statistics it found.
/// </summary>
internal sealed class ExpandedTermsWeight : Weight
{
    private readonly SearchContext _context;
    private readonly Bm25Parameters _bm25;
    private readonly FieldDefinition[] _defs;
    private readonly Bm25FField[] _fields;
    private readonly ExpandedTerm[] _terms;
    private readonly byte[][] _bytes;
    private readonly float[] _scale;
    private readonly (int Term, int Field, TermInfo Info)[][] _perLeaf;
    private readonly float _outerBoost;
    private readonly string? _queryWord;

    public ExpandedTermsWeight(SearchContext context, Bm25Parameters bm25, IReadOnlyList<FieldDefinition> defs, ExpansionSet set, IReadOnlyList<ExpandedTerm> chosen, float outerBoost, string? queryWord)
    {
        _context = context;
        _bm25 = bm25;
        _defs = [.. defs];
        _outerBoost = outerBoost;
        _queryWord = queryWord;
        _terms = [.. chosen];
        _fields = new Bm25FField[_defs.Length];
        var docCounts = new int[_defs.Length];
        for (int f = 0; f < _defs.Length; f++)
        {
            FieldStatistics stats = context.GetFieldStatistics(_defs[f].Name);
            _fields[f] = Bm25FField.Create(_defs[f].Name, _defs[f].Boost, stats, bm25);
            docCounts[f] = stats.DocCount;
        }

        _bytes = new byte[_terms.Length][];
        _scale = new float[_terms.Length];
        var index = new int[set.Terms.Count];
        Array.Fill(index, -1);
        for (int t = 0; t < _terms.Length; t++)
        {
            ExpandedTerm term = _terms[t];
            index[term.Candidate] = t;
            _bytes[t] = set.Terms[term.Candidate];
            long docCount = 0;
            long docFreq = 0;
            for (int f = 0; f < _defs.Length; f++)
            {
                if ((term.Fields & (1u << f)) != 0)
                {
                    docCount = Math.Max(docCount, docCounts[f]);
                    docFreq = Math.Max(docFreq, set.DocFreq[term.Candidate][f]);
                }
            }

            _scale[t] = term.Boost * Bm25.Idf(docCount, docFreq);
        }

        var lists = new List<(int, int, TermInfo)>[context.Leaves.Count];
        foreach (ExpansionHit h in set.Hits)
        {
            int t = index[h.Candidate];
            if (t >= 0 && (_terms[t].Fields & (1u << h.Field)) != 0)
            {
                (lists[h.Leaf] ??= []).Add((t, h.Field, h.Info));
            }
        }

        _perLeaf = new (int, int, TermInfo)[lists.Length][];
        for (int l = 0; l < lists.Length; l++)
        {
            if (lists[l] is { } list)
            {
                list.Sort(static (a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
                _perLeaf[l] = [.. list];
            }
            else
            {
                _perLeaf[l] = [];
            }
        }
    }

    public override Scorer? GetScorer(LeafContext leaf)
    {
        (int Term, int Field, TermInfo Info)[] hits = _perLeaf[leaf.Ordinal];
        if (hits.Length == 0)
        {
            return null;
        }

        var scorers = new List<Scorer>();
        for (int i = 0; i < hits.Length;)
        {
            int j = i;
            while (j < hits.Length && hits[j].Term == hits[i].Term)
            {
                j++;
            }

            int n = j - i;
            var its = new PostingsIterator[n];
            var readers = new ITextFieldReader[n];
            var fields = new Bm25FField[n];
            for (int k = 0; k < n; k++)
            {
                (int _, int field, TermInfo info) = hits[i + k];
                ITextFieldReader reader = leaf.Reader.GetTextField(_defs[field].Name)!; // found there when the expansion ran
                readers[k] = reader;
                its[k] = reader.GetPostings(info, PostingsFeatures.Frequencies);
                fields[k] = _fields[field];
            }

            scorers.Add(new Bm25FTermScorer(its, readers, fields, _bm25.K1, _scale[hits[i].Term]));
            i = j;
        }

        Scorer inner = scorers.Count == 1 ? scorers[0] : new BlockMaxWandScorer(scorers);
        return _outerBoost == 1f ? inner : new ScaledScorer(inner, _outerBoost);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        // Explanations are rare: they are built the long way, from one ordinary term weight per expansion.
        var children = new List<(Weight, Occur)>(_terms.Length);
        for (int t = 0; t < _terms.Length; t++)
        {
            var defs = new List<FieldDefinition>();
            for (int f = 0; f < _defs.Length; f++)
            {
                if ((_terms[t].Fields & (1u << f)) != 0)
                {
                    defs.Add(_defs[f]);
                }
            }

            Weight w = TermWeight.Create(_context, _bm25, defs, _bytes[t], _terms[t].Boost);
            if (_terms[t].Match is { } match)
            {
                w = new TypoMatchWeight(w, _queryWord!, Encoding.UTF8.GetString(_bytes[t]), match);
            }

            children.Add((w, Occur.Should));
        }

        if (children.Count == 1 && _queryWord is not null)
        {
            return children[0].Item1.Explain(leaf, docId);
        }

        return new BooleanWeight(children, 0, _outerBoost).Explain(leaf, docId);
    }
}
