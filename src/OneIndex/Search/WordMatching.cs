using System.Text;
using OneIndex.Index;

namespace OneIndex.Search;

// Word-level matching shared by the proximity factor and the ranking rules. A "query word" is what one word of user text became:
// a term, a term in several fields, or a typo-tolerant term. It is resolved against a snapshot into the indexed terms it can match
// (with their typos and whether they are the whole word), and a LeafWordMatcher then reads, for one document, where each word
// occurs in each field.

/// <summary>One indexed term that a query word matches in one segment and field.</summary>
internal sealed class WordTerm
{
    public required int Leaf { get; init; }

    public required int Field { get; init; }

    public required byte[] Bytes { get; init; }

    public required TermInfo Info { get; init; }

    /// <summary>Typos between the query word and the term (0 for an exact term).</summary>
    public required int Typos { get; init; }

    /// <summary>Whether the term is the whole word, not a longer word that only begins like it.</summary>
    public required bool Whole { get; init; }
}

/// <summary>A query word resolved against a snapshot.</summary>
internal sealed class QueryWord
{
    /// <summary>The query word as analyzed.</summary>
    public required string Text { get; init; }

    /// <summary>The Robertson and Sparck Jones weight of the word, from the document frequency of its closest term.</summary>
    public required float Idf { get; init; }

    /// <summary>The terms it matches, best first (fewest typos, whole words first, most frequent first), at most <see cref="WordResolver.MaxTerms"/>.</summary>
    public required IReadOnlyList<WordTerm> Terms { get; init; }
}

/// <summary>The text fields of a snapshot's schema that can hold query words, in schema order: the attribute order of the ranking rules.</summary>
internal sealed class WordFields
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public WordFields(Schema schema)
    {
        var names = new List<string>();
        var positions = new List<bool>();
        foreach (FieldDefinition f in schema.Fields)
        {
            if (f.Type == FieldType.Text && f.Indexed)
            {
                _index[f.Name] = names.Count;
                names.Add(f.Name);
                positions.Add(f.IndexOptions >= IndexOptions.Positions);
            }
        }

        Names = names;
        HasPositions = positions;
    }

    public IReadOnlyList<string> Names { get; }

    public IReadOnlyList<bool> HasPositions { get; }

    public int Count => Names.Count;

    public bool TryGet(string name, out int index) => _index.TryGetValue(name, out index);
}

/// <summary>Resolves the word queries that user text compiles to.</summary>
internal static class WordResolver
{
    /// <summary>The most terms one word keeps per snapshot. A word typed as a prefix can match thousands of terms; the best ones locate it.</summary>
    public const int MaxTerms = 32;

    /// <summary>Whether a query is a single word that <see cref="Resolve"/> understands.</summary>
    public static bool IsWord(Query query) => query is TermQuery or MultiFieldTermQuery or TypoTermQuery;

    /// <summary>Finds the single word query inside a group of readings: the query itself, or the first reading of an alternatives query (synonym readings follow the word).</summary>
    public static Query? PrimaryWord(Query query)
    {
        if (IsWord(query))
        {
            return query;
        }

        if (query is BooleanQuery { Clauses.Count: > 0 } b && b.Clauses.All(static c => c.Occur == Occur.Should) && IsWord(b.Clauses[0].Query))
        {
            return b.Clauses[0].Query;
        }

        return null;
    }

    public static QueryWord? Resolve(SearchContext context, WordFields fields, Query word)
    {
        switch (word)
        {
            case TermQuery t:
                return ResolveExact(context, fields, [t.Field], t.Term);
            case MultiFieldTermQuery m:
                return ResolveExact(context, fields, m.Fields, m.Term);
            case TypoTermQuery ty:
                return ResolveTypo(context, fields, ty);
            default:
                return null;
        }
    }

    private static QueryWord? ResolveExact(SearchContext context, WordFields fields, IReadOnlyList<string> names, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var terms = new List<WordTerm>();
        foreach (string name in names)
        {
            if (!fields.TryGet(name, out int field))
            {
                continue;
            }

            foreach (LeafContext leaf in context.Leaves)
            {
                if (leaf.Reader.GetTextField(name) is { } reader && reader.TryGetTerm(bytes, out TermInfo info))
                {
                    terms.Add(new WordTerm { Leaf = leaf.Ordinal, Field = field, Bytes = bytes, Info = info, Typos = 0, Whole = true });
                }
            }
        }

        return new QueryWord { Text = text, Idf = IdfOf(context, fields, names, bytes), Terms = terms };
    }

    private static QueryWord? ResolveTypo(SearchContext context, WordFields fields, TypoTermQuery query)
    {
        var all = new List<(WordTerm Term, long DocFreq)>();
        int[] word = TypoRanking.CodePoints(query.Term);
        var plans = new TypoCandidates.Plan?[3];
        for (int f = 0; f < query.Fields.Count; f++)
        {
            string name = query.Fields[f];
            if (!fields.TryGet(name, out int field))
            {
                continue;
            }

            int budget = query.ExactFields.Contains(name) ? 0 : query.MaxTypos;
            TypoCandidates.Plan plan = plans[budget] ??= new TypoCandidates.Plan(query.Term, budget, query.Prefix);
            int scanned = 0;
            foreach (LeafContext leaf in context.Leaves)
            {
                if (leaf.Reader.GetTextField(name) is not { } reader)
                {
                    continue;
                }

                int ordinal = leaf.Ordinal;
                plan.Collect(reader, (ReadOnlySpan<byte> term, in TermInfo info) =>
                {
                    if (++scanned > (1 << 16))
                    {
                        return;
                    }

                    int[] cps = TypoRanking.CodePoints(Encoding.UTF8.GetString(term));
                    if (TypoRanking.Match(word, cps, budget, query.Prefix) is { } match)
                    {
                        all.Add((new WordTerm { Leaf = ordinal, Field = field, Bytes = term.ToArray(), Info = info, Typos = match.Typos, Whole = !match.IsPrefixExtension }, info.DocFreq));
                    }
                });
            }
        }

        if (all.Count == 0)
        {
            return new QueryWord { Text = query.Term, Idf = IdfOf(context, fields, query.Fields, Encoding.UTF8.GetBytes(query.Term)), Terms = [] };
        }

        // Best first: fewest typos, whole words before longer ones, then the most frequent, then term bytes, so the cut is deterministic.
        all.Sort(static (a, b) =>
        {
            int c = a.Term.Typos.CompareTo(b.Term.Typos);
            if (c == 0)
            {
                c = b.Term.Whole.CompareTo(a.Term.Whole);
            }

            if (c == 0)
            {
                c = b.DocFreq.CompareTo(a.DocFreq);
            }

            if (c == 0)
            {
                c = ByteArrayOrder.Instance.Compare(a.Term.Bytes, b.Term.Bytes);
            }

            if (c == 0)
            {
                c = a.Term.Leaf.CompareTo(b.Term.Leaf);
            }

            return c != 0 ? c : a.Term.Field.CompareTo(b.Term.Field);
        });

        // The idf is the closest term's. The cut keeps whole terms (every segment and field of a term stays or goes together).
        byte[] closest = all[0].Term.Bytes;
        var kept = new List<WordTerm>();
        var keptTerms = new List<byte[]>();
        foreach ((WordTerm term, _) in all)
        {
            if (!keptTerms.Exists(k => ByteArrayOrder.Instance.Compare(k, term.Bytes) == 0))
            {
                if (keptTerms.Count == MaxTerms)
                {
                    continue;
                }

                keptTerms.Add(term.Bytes);
            }

            kept.Add(term);
        }

        return new QueryWord { Text = query.Term, Idf = IdfOf(context, fields, query.Fields, closest), Terms = kept };
    }

    private static float IdfOf(SearchContext context, WordFields fields, IReadOnlyList<string> names, byte[] term)
    {
        long docCount = 0;
        long docFreq = 0;
        foreach (string name in names)
        {
            if (fields.TryGet(name, out _))
            {
                docCount = Math.Max(docCount, context.GetFieldStatistics(name).DocCount);
                docFreq = Math.Max(docFreq, context.GetDocFreq(name, term));
            }
        }

        return Bm25.Idf(docCount, docFreq);
    }
}

/// <summary>
/// Where the words of a query occur in one document, read from the postings of one segment. Documents must be offered in increasing
/// order. The positions of all terms of a word in a field are merged into one ascending list.
/// </summary>
internal sealed class LeafWordMatcher
{
    private readonly QueryWord[] _words;
    private readonly WordFields _fields;
    private readonly bool _positions;
    private readonly Entry[][] _entries; // per word, the postings of its terms in this segment
    private readonly ITextFieldReader?[] _fieldReaders; // per field, for the length of the loaded document
    private int[][][] _pos; // [word][field] merged ascending positions
    private readonly int[][] _count; // [word][field]
    private readonly int[] _bestTypos;
    private readonly bool[] _bestWhole;
    private readonly bool[] _matched;
    private readonly bool[][] _present; // [word][field]
    private int _doc = -1;
    private bool _positionsRead;

    private sealed class Entry(PostingsIterator it, WordTerm term, bool hasPositions)
    {
        public PostingsIterator It { get; } = it;

        public WordTerm Term { get; } = term;

        public bool HasPositions { get; } = hasPositions;

        public bool AtDoc { get; set; }
    }

    public LeafWordMatcher(LeafContext leaf, IReadOnlyList<QueryWord> words, WordFields fields, bool positions)
    {
        _words = [.. words];
        _fields = fields;
        _positions = positions;
        _entries = new Entry[_words.Length][];
        _fieldReaders = new ITextFieldReader?[fields.Count];
        for (int f = 0; f < _fieldReaders.Length; f++)
        {
            _fieldReaders[f] = leaf.Reader.GetTextField(fields.Names[f]);
        }

        for (int w = 0; w < _words.Length; w++)
        {
            var list = new List<Entry>();
            foreach (WordTerm t in _words[w].Terms)
            {
                if (t.Leaf != leaf.Ordinal || leaf.Reader.GetTextField(fields.Names[t.Field]) is not { } reader)
                {
                    continue;
                }

                bool withPositions = positions && fields.HasPositions[t.Field] && (reader.IndexedFeatures & PostingsFeatures.Positions) == PostingsFeatures.Positions;
                list.Add(new Entry(reader.GetPostings(t.Info, withPositions ? PostingsFeatures.Positions : PostingsFeatures.None), t, withPositions));
            }

            _entries[w] = [.. list];
        }

        _pos = new int[_words.Length][][];
        _count = new int[_words.Length][];
        for (int w = 0; w < _words.Length; w++)
        {
            _pos[w] = new int[fields.Count][];
            _count[w] = new int[fields.Count];
            for (int f = 0; f < fields.Count; f++)
            {
                _pos[w][f] = new int[16]; // grows to the largest frequency seen, then never again
            }
        }

        _bestTypos = new int[_words.Length];
        _bestWhole = new bool[_words.Length];
        _matched = new bool[_words.Length];
        _present = new bool[_words.Length][];
        for (int w = 0; w < _words.Length; w++)
        {
            _present[w] = new bool[fields.Count];
        }
    }

    public int WordCount => _words.Length;

    /// <summary>The token count of the loaded document in a field (0 when the segment has no such field).</summary>
    public int FieldLength(int field) => _fieldReaders[field] is { } r && _doc >= 0 ? r.GetFieldLength(_doc) : 0;

    public int FieldCount => _fields.Count;

    public bool Matched(int word) => _matched[word];

    public int BestTypos(int word) => _bestTypos[word];

    public bool BestWhole(int word) => _bestWhole[word];

    /// <summary>Whether the word occurs in the field of the loaded document (also for fields that keep no positions).</summary>
    public bool Present(int word, int field) => _present[word][field];

    public ReadOnlySpan<int> Positions(int word, int field) => _pos[word][field].AsSpan(0, _count[word][field]);

    /// <summary>How many of the words occur in the loaded document.</summary>
    public int MatchedWords { get; private set; }

    /// <summary>Finds which words occur in <paramref name="doc"/> and with which typos. Positions are read by <see cref="EnsurePositions"/>.</summary>
    public void Load(int doc)
    {
        if (doc == _doc)
        {
            return;
        }

        _doc = doc;
        _positionsRead = false;
        MatchedWords = 0;
        for (int w = 0; w < _words.Length; w++)
        {
            Array.Clear(_count[w]);
            Array.Clear(_present[w]);
            _matched[w] = false;
            _bestTypos[w] = int.MaxValue;
            _bestWhole[w] = false;
            foreach (Entry e in _entries[w])
            {
                PostingsIterator it = e.It;
                int at = it.DocId < doc ? it.Advance(doc) : it.DocId;
                e.AtDoc = at == doc;
                if (!e.AtDoc)
                {
                    continue;
                }

                _matched[w] = true;
                _present[w][e.Term.Field] = true;
                int typos = e.Term.Typos;
                if (typos < _bestTypos[w] || (typos == _bestTypos[w] && e.Term.Whole && !_bestWhole[w]))
                {
                    _bestTypos[w] = typos;
                    _bestWhole[w] = e.Term.Whole;
                }
            }

            if (_matched[w])
            {
                MatchedWords++;
            }
        }
    }

    /// <summary>Reads the positions of the words found by <see cref="Load"/>. Skipped by callers that do not need them (a document with one matching word has nothing to compare).</summary>
    public void EnsurePositions()
    {
        if (_positionsRead || !_positions)
        {
            return;
        }

        _positionsRead = true;
        for (int w = 0; w < _words.Length; w++)
        {
            if (!_matched[w])
            {
                continue;
            }

            foreach (Entry e in _entries[w])
            {
                if (!e.AtDoc || !e.HasPositions)
                {
                    continue;
                }

                PostingsIterator it = e.It;
                int f = e.Term.Field;
                int n = it.Frequency;
                int[] buffer = _pos[w][f];
                int used = _count[w][f];
                if (buffer.Length < used + n)
                {
                    Array.Resize(ref buffer, Math.Max(used + n, buffer.Length * 2));
                    _pos[w][f] = buffer;
                }

                for (int i = 0; i < n; i++)
                {
                    buffer[used++] = it.NextPosition();
                }

                _count[w][f] = used;
            }

            for (int f = 0; f < _fields.Count; f++)
            {
                if (_count[w][f] > 1)
                {
                    Array.Sort(_pos[w][f], 0, _count[w][f]);
                }
            }
        }
    }
}

/// <summary>
/// The term-proximity score of Büttcher, Clarke and Lushman (SIGIR 2006, "Term proximity scoring for ad-hoc retrieval on very large
/// text collections"): every pair of occurrences of two different query words with no other query word occurrence between them adds
/// idf(other)/distance² to the accumulator of each of the two words; a word's accumulator then counts, saturated, like a term frequency.
/// </summary>
internal static class Proximity
{
    /// <summary>The weight of the proximity bonus relative to the word scores. See docs/scoring.md for the choice.</summary>
    public const float DefaultWeight = 0.1f;

    /// <summary>
    /// The proximity of the words in one field: sum over words of min(1, idf) * acc * (k1 + 1) / (acc + K), with K = k1 * (1 - b + b * length / average length) passed as <paramref name="norm"/>. <paramref name="acc"/> is scratch of one value per word.
    /// Positions of one word are ascending; the pairs come from the merged ascending order of all words' positions.
    /// </summary>
    public static float FieldBonus(LeafWordMatcher m, int field, ReadOnlySpan<float> idf, ReadOnlySpan<float> cap, float k1, float norm, Span<float> acc, Span<int> cursor)
    {
        int words = m.WordCount;
        acc[..words].Clear();
        cursor[..words].Clear();
        int prevWord = -1;
        int prevPos = 0;
        while (true)
        {
            // Next occurrence in merged order: smallest position, lowest word on ties.
            int best = -1;
            int bestPos = int.MaxValue;
            for (int w = 0; w < words; w++)
            {
                ReadOnlySpan<int> p = m.Positions(w, field);
                if (cursor[w] < p.Length && p[cursor[w]] < bestPos)
                {
                    best = w;
                    bestPos = p[cursor[w]];
                }
            }

            if (best < 0)
            {
                break;
            }

            cursor[best]++;
            if (prevWord >= 0 && prevWord != best)
            {
                int d = Math.Max(1, bestPos - prevPos);
                float inv = 1f / ((float)d * d);
                acc[prevWord] += idf[best] * inv;
                acc[best] += idf[prevWord] * inv;
            }

            prevWord = best;
            prevPos = bestPos;
        }

        float sum = 0f;
        for (int w = 0; w < words; w++)
        {
            if (acc[w] > 0f)
            {
                // Buttcher et al. section 2: min(1, idf) * acc * (k1 + 1) / (acc + K), K the document's BM25 length normalisation.
                sum += cap[w] * ((k1 + 1f) * acc[w] / (acc[w] + norm));
            }
        }

        return sum;
    }
}
