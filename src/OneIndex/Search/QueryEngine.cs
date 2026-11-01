using System.Text;
using OneIndex.Codecs;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>What a prefix, fuzzy or regular-expression query does when it matches more distinct terms than <see cref="QueryEngineOptions.MaxExpandedTerms"/>.</summary>
public enum ExpansionOverflow : byte
{
    /// <summary>Raise <see cref="TooManyTermsException"/>. The default: a query that would silently match less than it says is worse than an error.</summary>
    Throw,

    /// <summary>Keep the terms that occur in the most documents (ties by term bytes) and drop the rest. The usual choice for as-you-type search.</summary>
    KeepMostFrequent,
}

/// <summary>Settings of the query engine. Every property has a working default.</summary>
public sealed record QueryEngineOptions
{
    /// <summary>The BM25F parameters. Default <see cref="Bm25Parameters.Default"/>.</summary>
    public Bm25Parameters Bm25 { get; init; } = Bm25Parameters.Default;

    /// <summary>The most distinct terms one prefix, fuzzy or regular-expression query may expand to. Default 1,024.</summary>
    public int MaxExpandedTerms { get; init; } = 1024;

    /// <summary>What happens past <see cref="MaxExpandedTerms"/>. Default <see cref="ExpansionOverflow.Throw"/>. <see cref="ExpansionOverflow.KeepMostFrequent"/> still refuses to look at more than 1,048,576 distinct terms.</summary>
    public ExpansionOverflow Overflow { get; init; } = ExpansionOverflow.Throw;

    /// <summary>The most clauses one boolean query may have. Default 4,096.</summary>
    public int MaxClauses { get; init; } = 4096;

    /// <summary>The deepest nesting of queries. Default 64.</summary>
    public int MaxDepth { get; init; } = 64;
}

/// <summary>
/// The query engine: turns the query records into weights and scorers over a snapshot with BM25F scoring (docs/scoring.md) and the
/// semantics of docs/queries.md. Statistics (document counts, frequencies, average field lengths) are summed across all segments
/// of the snapshot when the weight is made, so every segment scores on the same numbers.
/// </summary>
public sealed class QueryEngine : IWeightFactory
{
    private readonly QueryEngineOptions _options;

    /// <summary>Creates an engine.</summary>
    /// <param name="options">The settings; the defaults when <see langword="null"/>.</param>
    /// <exception cref="QueryException">A setting is out of range.</exception>
    public QueryEngine(QueryEngineOptions? options = null)
    {
        _options = options ?? new QueryEngineOptions();
        _options.Bm25.Validate();
        if (_options.MaxExpandedTerms < 1 || _options.MaxClauses < 1 || _options.MaxDepth < 1)
        {
            throw new QueryException("MaxExpandedTerms, MaxClauses and MaxDepth must be at least 1.");
        }
    }

    /// <inheritdoc/>
    /// <exception cref="QueryException">The query names an unknown or unsuitable field, or has an invalid parameter.</exception>
    /// <exception cref="TooManyTermsException">A multi-term query expands past the limit.</exception>
    public Weight CreateWeight(Query query, SearchContext context, ScoreMode scoreMode)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        return query.Accept(new WeightBuilder(context, _options));
    }

    private sealed class WeightBuilder(SearchContext context, QueryEngineOptions options) : QueryVisitor<Weight>
    {
        private const int ScanCeiling = 1 << 20;
        private int _depth;

        public override Weight VisitTerm(TermQuery query)
        {
            CheckBoost(query);
            return TermWeight.Create(context, options.Bm25, [RequireTextField(query.Field)], query.Term, query.Boost);
        }

        public override Weight VisitMultiFieldTerm(MultiFieldTermQuery query)
        {
            CheckBoost(query);
            if (query.Fields.Count == 0)
            {
                throw new QueryException("A multi-field term query needs at least one field.");
            }

            var fields = new List<FieldDefinition>();
            foreach (string name in query.Fields)
            {
                FieldDefinition f = RequireTextField(name);
                if (fields.Contains(f))
                {
                    throw new QueryException($"Field '{name}' is listed twice in a multi-field term query.");
                }

                fields.Add(f);
            }

            return TermWeight.Create(context, options.Bm25, fields, query.Term, query.Boost);
        }

        public override Weight VisitTypoTerm(TypoTermQuery query)
        {
            CheckBoost(query);
            if (query.Fields.Count == 0)
            {
                throw new QueryException("A typo-tolerant term query needs at least one field.");
            }

            if (query.MaxTypos is < 0 or > 2)
            {
                throw new QueryException($"The typos of a term query are 0 to 2, got {query.MaxTypos}.");
            }

            var defs = new List<FieldDefinition>();
            foreach (string name in query.Fields)
            {
                FieldDefinition f = RequireTextField(name);
                if (defs.Contains(f))
                {
                    throw new QueryException($"Field '{name}' is listed twice in a typo-tolerant term query.");
                }

                defs.Add(f);
            }

            // Gather the candidate words per field, then cost each once with the reference computation (the automata only narrow the scan).
            var candidates = new SortedDictionary<byte[], (List<FieldDefinition> Fields, long DocFreq)>(ByteArrayOrder.Instance);
            foreach (FieldDefinition def in defs)
            {
                int budget = query.ExactFields.Contains(def.Name) ? 0 : query.MaxTypos;
                int scanned = 0;
                foreach (LeafContext leaf in context.Leaves)
                {
                    if (leaf.Reader.GetTextField(def.Name) is not { } reader)
                    {
                        continue;
                    }

                    TypoCandidates.Collect(reader, query.Term, budget, query.Prefix, (term, df) =>
                    {
                        if (++scanned > ScanCeiling)
                        {
                            return;
                        }

                        if (!candidates.TryGetValue(term, out var entry))
                        {
                            entry = ([], 0);
                        }

                        if (!entry.Fields.Contains(def))
                        {
                            entry.Fields.Add(def);
                        }

                        candidates[term] = (entry.Fields, entry.DocFreq + df);
                    });
                }
            }

            var accepted = new List<(byte[] Term, string Text, TypoMatch Match, List<FieldDefinition> Fields, long DocFreq)>();
            foreach ((byte[] term, (List<FieldDefinition> fields, long df)) in candidates)
            {
                string text = Encoding.UTF8.GetString(term);
                TypoMatch? m = TypoRanking.Match(query.Term, text, query.MaxTypos, query.Prefix);
                if (m is not { } match)
                {
                    continue;
                }

                // A field that allows no typos only takes matches without them.
                List<FieldDefinition> allowed = fields.FindAll(f => match.Typos == 0 || !query.ExactFields.Contains(f.Name));
                if (allowed.Count > 0)
                {
                    accepted.Add((term, text, match, allowed, df));
                }
            }

            // Past the limit keep the closest and then the most frequent words; unlike the other expansions this never throws, because it
            // serves user text, which must not fail on a short word.
            IEnumerable<(byte[] Term, string Text, TypoMatch Match, List<FieldDefinition> Fields, long DocFreq)> chosen = accepted;
            if (accepted.Count > options.MaxExpandedTerms)
            {
                chosen = accepted.OrderBy(static a => a.Match.Typos).ThenBy(static a => a.Match.IsPrefixExtension).ThenByDescending(static a => a.DocFreq)
                    .ThenBy(static a => a.Term, ByteArrayOrder.Instance).Take(options.MaxExpandedTerms).OrderBy(static a => a.Term, ByteArrayOrder.Instance);
            }

            var children = new List<(Weight, Occur)>();
            foreach ((byte[] term, string text, TypoMatch match, List<FieldDefinition> fields, _) in chosen)
            {
                Weight w = TermWeight.Create(context, options.Bm25, fields, term, query.Boost * match.Factor);
                children.Add((new TypoMatchWeight(w, query.Term, text, match), Occur.Should));
            }

            return children.Count == 0 ? EmptyWeight.Instance : children.Count == 1 ? children[0].Item1 : new BooleanWeight(children, 0, 1f);
        }

        public override Weight VisitPhrase(PhraseQuery query)
        {
            CheckBoost(query);
            FieldDefinition def = RequireTextField(query.Field);
            if (query.Slop < 0)
            {
                throw new QueryException($"The slop of a phrase must not be negative, got {query.Slop}.");
            }

            if (query.Terms.Count == 0)
            {
                return EmptyWeight.Instance;
            }

            if (query.Terms.Count == 1)
            {
                return TermWeight.Create(context, options.Bm25, [def], query.Terms[0], query.Boost);
            }

            if (def.Type != FieldType.Text || def.IndexOptions < IndexOptions.Positions)
            {
                throw new QueryException($"Field '{query.Field}' does not store positions, so a phrase cannot be matched on it.");
            }

            return PhraseWeight.Create(context, options.Bm25, def, query.Terms, query.Slop, query.Boost);
        }

        public override Weight VisitBoolean(BooleanQuery query)
        {
            CheckBoost(query);
            if (query.MinimumShouldMatch < 0)
            {
                throw new QueryException($"MinimumShouldMatch must not be negative, got {query.MinimumShouldMatch}.");
            }

            if (query.Clauses.Count > options.MaxClauses)
            {
                throw new QueryException($"A boolean query has {query.Clauses.Count} clauses; the limit is {options.MaxClauses}.");
            }

            if (++_depth > options.MaxDepth)
            {
                throw new QueryException($"Queries are nested deeper than {options.MaxDepth} levels.");
            }

            try
            {
                var children = new List<(Weight, Occur)>(query.Clauses.Count);
                foreach (BooleanClause c in query.Clauses)
                {
                    ArgumentNullException.ThrowIfNull(c.Query);
                    children.Add((c.Query.Accept(this), c.Occur));
                }

                return children.Count == 0 ? EmptyWeight.Instance : new BooleanWeight(children, query.MinimumShouldMatch, query.Boost);
            }
            finally
            {
                _depth--;
            }
        }

        public override Weight VisitPrefix(PrefixQuery query)
        {
            CheckBoost(query);
            FieldDefinition def = RequireTextField(query.Field);
            byte[] prefix = Encoding.UTF8.GetBytes(query.Prefix);
            return Expand(def, query.Boost, leaf => leaf.GetTerms(prefix));
        }

        public override Weight VisitFuzzy(FuzzyQuery query)
        {
            CheckBoost(query);
            FieldDefinition def = RequireTextField(query.Field);
            if (query.MaxEdits is < 0 or > 2)
            {
                throw new QueryException($"The edit distance of a fuzzy query is 0 to 2, got {query.MaxEdits}.");
            }

            if (query.PrefixLength < 0)
            {
                throw new QueryException($"The prefix length of a fuzzy query must not be negative, got {query.PrefixLength}.");
            }

            if (query.MaxEdits == 0)
            {
                return TermWeight.Create(context, options.Bm25, [def], query.Term, query.Boost);
            }

            // The first PrefixLength code points must match exactly: they are the enumeration's prefix, which the automaton also reads
            // (with no edits, since they are the word's own characters), so its budget is left for the rest of the word.
            int split = 0;
            int kept = 0;
            foreach (Rune r in query.Term.EnumerateRunes())
            {
                if (kept == query.PrefixLength)
                {
                    break;
                }

                split += r.Utf16SequenceLength;
                kept++;
            }

            byte[] prefix = Encoding.UTF8.GetBytes(query.Term[..split]);
            return Expand(def, query.Boost, leaf => leaf.Intersect(new LevenshteinAutomaton(query.Term, query.MaxEdits, query.Transpositions), prefix));
        }

        public override Weight VisitRegex(RegexQuery query)
        {
            CheckBoost(query);
            FieldDefinition def = RequireTextField(query.Field);
            try
            {
                _ = new RegexAutomaton(query.Pattern);
            }
            catch (RegexSyntaxException e)
            {
                throw new QueryException($"The regular expression for field '{query.Field}' is invalid at position {e.Position}: {e.Message}");
            }

            return Expand(def, query.Boost, leaf => leaf.Intersect(new RegexAutomaton(query.Pattern)));
        }

        public override Weight VisitMatchAll(MatchAllQuery query)
        {
            CheckBoost(query);
            return new ConstantWeight("match all", query.Boost, static leaf => new AllDocsIterator(leaf.Reader.MaxDoc));
        }

        public override Weight VisitRange(RangeQuery query)
        {
            CheckBoost(query);
            return RangeWeightFactory.Create(context.Schema, query);
        }

        public override Weight VisitGeoRadius(GeoRadiusQuery query)
        {
            CheckBoost(query);
            return GeoWeightFactory.Radius(context.Schema, query);
        }

        public override Weight VisitGeoBoundingBox(GeoBoundingBoxQuery query)
        {
            CheckBoost(query);
            return GeoWeightFactory.BoundingBox(context.Schema, query);
        }

        public override Weight VisitFunctionScore(FunctionScoreQuery query)
        {
            CheckBoost(query);
            if (query.Functions.Count is < 1 or > FunctionScoreQuery.MaxFunctions)
            {
                throw new QueryException($"A function score query needs 1 to {FunctionScoreQuery.MaxFunctions} functions, got {query.Functions.Count}.");
            }

            if (!(query.MaxBoost > 0f) || float.IsNaN(query.MaxBoost) || (query.MinScore is { } min && float.IsNaN(min)))
            {
                throw new QueryException("MaxBoost of a function score query must be above 0, and MinScore a number.");
            }

            if (++_depth > options.MaxDepth)
            {
                throw new QueryException($"Queries are nested deeper than {options.MaxDepth} levels.");
            }

            try
            {
                var filters = new Weight?[query.Functions.Count];
                for (int i = 0; i < filters.Length; i++)
                {
                    ScoreFunction f = query.Functions[i] ?? throw new QueryException("A function score query has a null function.");
                    f.Validate(context.Schema);
                    filters[i] = f.Filter?.Accept(this);
                }

                return new FunctionScoreWeight(query, query.Inner.Accept(this), filters);
            }
            finally
            {
                _depth--;
            }
        }

        private Weight Expand(FieldDefinition def, float boost, Func<ITextFieldReader, ITermEnumerator> open)
        {
            int limit = options.MaxExpandedTerms;
            bool keepMost = options.Overflow == ExpansionOverflow.KeepMostFrequent;
            var found = new SortedDictionary<byte[], long>(ByteArrayOrder.Instance);
            foreach (LeafContext leaf in context.Leaves)
            {
                if (leaf.Reader.GetTextField(def.Name) is not { } reader)
                {
                    continue;
                }

                using ITermEnumerator terms = open(reader);
                while (terms.MoveNext())
                {
                    byte[] key = terms.Term.ToArray(); // one copy per distinct term of an expansion, not a hot path
                    if (found.TryGetValue(key, out long df))
                    {
                        found[key] = df + terms.Info.DocFreq;
                        continue;
                    }

                    if (found.Count >= (keepMost ? ScanCeiling : limit))
                    {
                        throw new TooManyTermsException(def.Name, keepMost ? ScanCeiling : limit);
                    }

                    found.Add(key, terms.Info.DocFreq);
                }
            }

            if (found.Count == 0)
            {
                return EmptyWeight.Instance;
            }

            IEnumerable<byte[]> chosen = found.Keys;
            if (found.Count > limit)
            {
                chosen = found.OrderByDescending(static kv => kv.Value).ThenBy(static kv => kv.Key, ByteArrayOrder.Instance).Take(limit).Select(static kv => kv.Key).Order(ByteArrayOrder.Instance);
            }

            var children = new List<(Weight, Occur)>();
            foreach (byte[] term in chosen)
            {
                children.Add((TermWeight.Create(context, options.Bm25, [def], term, 1f), Occur.Should));
            }

            return new BooleanWeight(children, 0, boost);
        }

        private FieldDefinition RequireTextField(string name)
        {
            if (!context.Schema.TryGetField(name, out FieldDefinition? def))
            {
                throw new QueryException($"The schema has no field '{name}'.");
            }

            bool searchable = def.Indexed && (def.Type == FieldType.Keyword || (def.Type == FieldType.Text && def.IndexOptions != IndexOptions.None));
            if (!searchable)
            {
                throw new QueryException($"Field '{name}' ({def.Type}) is not searchable by term.");
            }

            return def;
        }

        private static void CheckBoost(Query q)
        {
            if (!float.IsFinite(q.Boost) || q.Boost < 0f)
            {
                throw new QueryException($"A query boost must be finite and not negative, got {q.Boost}.");
            }
        }
    }
}

internal sealed class ByteArrayOrder : IComparer<byte[]>
{
    public static ByteArrayOrder Instance { get; } = new();

    public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
}

internal sealed class EmptyWeight : Weight
{
    public static EmptyWeight Instance { get; } = new();

    public override Scorer? GetScorer(LeafContext leaf) => null;

    public override Explanation? Explain(LeafContext leaf, int docId) => null;
}

/// <summary>A weight whose matches are a plain set of documents, each scored with the boost.</summary>
internal sealed class ConstantWeight(string description, float boost, Func<LeafContext, DocIdSetIterator?> documents) : Weight
{
    public override Scorer? GetScorer(LeafContext leaf) => documents(leaf) is { } it ? new ConstantScorer(it, boost) : null;

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        DocIdSetIterator? it = documents(leaf);
        return it is not null && it.Advance(docId) == docId ? new Explanation(boost, $"{description}, constant score = boost {boost}") : null;
    }
}

internal sealed class TermWeight : Weight
{
    private readonly Bm25FField[] _fields;
    private readonly byte[] _term;
    private readonly string _termText;
    private readonly float _k1;
    private readonly float _b;
    private readonly float _idf;
    private readonly float _boost;
    private readonly long _docCount;
    private readonly long _docFreq;

    private TermWeight(Bm25FField[] fields, byte[] term, float k1, float b, long docCount, long docFreq, float boost)
    {
        _fields = fields;
        _term = term;
        _termText = Encoding.UTF8.GetString(term);
        _k1 = k1;
        _b = b;
        _docCount = docCount;
        _docFreq = docFreq;
        _idf = Bm25.Idf(docCount, docFreq);
        _boost = boost;
    }

    public static TermWeight Create(SearchContext context, Bm25Parameters p, IReadOnlyList<FieldDefinition> defs, string term, float boost)
        => Create(context, p, defs, Encoding.UTF8.GetBytes(term), boost);

    public static TermWeight Create(SearchContext context, Bm25Parameters p, IReadOnlyList<FieldDefinition> defs, byte[] term, float boost)
    {
        var fields = new Bm25FField[defs.Count];
        long docCount = 0;
        long docFreq = 0;
        for (int i = 0; i < fields.Length; i++)
        {
            FieldStatistics stats = context.GetFieldStatistics(defs[i].Name);
            fields[i] = Bm25FField.Create(defs[i].Name, defs[i].Boost, stats, p);
            docCount = Math.Max(docCount, stats.DocCount);
            docFreq = Math.Max(docFreq, context.GetDocFreq(defs[i].Name, term));
        }

        return new TermWeight(fields, term, p.K1, p.B, docCount, docFreq, boost);
    }

    public float Idf => _idf;

    public override Scorer? GetScorer(LeafContext leaf)
    {
        var its = new List<PostingsIterator>(_fields.Length);
        var readers = new List<ITextFieldReader>(_fields.Length);
        var fields = new List<Bm25FField>(_fields.Length);
        foreach (Bm25FField f in _fields)
        {
            if (leaf.Reader.GetTextField(f.Name) is { } reader && reader.TryGetTerm(_term, out TermInfo info))
            {
                its.Add(reader.GetPostings(info, PostingsFeatures.Frequencies));
                readers.Add(reader);
                fields.Add(f);
            }
        }

        return its.Count == 0 ? null : new Bm25FTermScorer([.. its], [.. readers], [.. fields], _k1, _boost * _idf);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        float x = 0f;
        var parts = new List<Explanation>();
        foreach (Bm25FField f in _fields)
        {
            if (leaf.Reader.GetTextField(f.Name) is { } reader && reader.TryGetTerm(_term, out TermInfo info))
            {
                PostingsIterator it = reader.GetPostings(info, PostingsFeatures.Frequencies);
                if (it.Advance(docId) == docId)
                {
                    int tf = it.Frequency;
                    int len = reader.GetFieldLength(docId);
                    float v = f.Weighted(tf, len);
                    x += v;
                    parts.Add(new Explanation(v, $"field {f.Name}: weight {Fmt(f.Weight)} * tf {tf} / ((1 - b) + b * length {len} / avgLength {Fmt(f.AverageLength)}), b = {Fmt(_b)}"));
                }
            }
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var idf = new Explanation(_idf, $"idf = ln(1 + (N - n + 0.5) / (n + 0.5)), n = {_docFreq}, N = {_docCount}");
        var sum = new Explanation(x, "x = sum over fields of weight * tf / ((1 - b) + b * length / avgLength)", [.. parts]);
        float scale = _boost * _idf;
        return new Explanation(Bm25.Saturate(x, _k1, scale), $"term '{_termText}': boost {Fmt(_boost)} * idf * x / (k1 + x), k1 = {Fmt(_k1)}", idf, sum);
    }

    private static string Fmt(float v) => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
}

internal sealed class PhraseWeight : Weight
{
    private readonly Bm25FField _field;
    private readonly byte[][] _terms;
    private readonly int _slop;
    private readonly float _k1;
    private readonly float _idf;
    private readonly float _boost;

    private PhraseWeight(Bm25FField field, byte[][] terms, int slop, float k1, float idf, float boost)
    {
        _field = field;
        _terms = terms;
        _slop = slop;
        _k1 = k1;
        _idf = idf;
        _boost = boost;
    }

    public static PhraseWeight Create(SearchContext context, Bm25Parameters p, FieldDefinition def, IReadOnlyList<string> terms, int slop, float boost)
    {
        FieldStatistics stats = context.GetFieldStatistics(def.Name);
        var bytes = new byte[terms.Count][];
        float idf = 0f;
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Encoding.UTF8.GetBytes(terms[i]);
            idf += Bm25.Idf(stats.DocCount, context.GetDocFreq(def.Name, bytes[i]));
        }

        return new PhraseWeight(Bm25FField.Create(def.Name, def.Boost, stats, p), bytes, slop, p.K1, idf, boost);
    }

    public override Scorer? GetScorer(LeafContext leaf) => GetPhraseScorer(leaf);

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        PhraseScorer? s = GetPhraseScorer(leaf);
        if (s is null || s.Advance(docId) != docId)
        {
            return null;
        }

        int len = leaf.Reader.GetTextField(_field.Name)!.GetFieldLength(docId);
        float freq = s.Frequency;
        var f = new Explanation(freq, $"phrase frequency = sum of 1 / (1 + gap) over matches, slop {_slop}");
        var idf = new Explanation(_idf, "idf = sum of the terms' idf");
        float x = _field.Weighted(freq, len);
        var sum = new Explanation(x, $"x = weight {_field.Weight.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)} * frequency / ((1 - b) + b * length {len} / avgLength {_field.AverageLength.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)})", f);
        return new Explanation(s.Score(), $"phrase in {_field.Name}: boost * idf * x / (k1 + x)", idf, sum);
    }

    private PhraseScorer? GetPhraseScorer(LeafContext leaf)
    {
        if (leaf.Reader.GetTextField(_field.Name) is not { } reader)
        {
            return null;
        }

        if ((reader.IndexedFeatures & PostingsFeatures.Positions) != PostingsFeatures.Positions)
        {
            throw new QueryException($"Field '{_field.Name}' was indexed without positions in segment {leaf.Reader.Info.Name}, so a phrase cannot be matched on it.");
        }

        var its = new PostingsIterator[_terms.Length];
        for (int i = 0; i < its.Length; i++)
        {
            if (!reader.TryGetTerm(_terms[i], out TermInfo info))
            {
                return null;
            }

            its[i] = reader.GetPostings(info, PostingsFeatures.Positions);
        }

        return new PhraseScorer(its, reader, _field, _slop, _k1, _boost * _idf);
    }
}

internal sealed class BooleanWeight : Weight
{
    private readonly List<(Weight Weight, Occur Occur)> _clauses;
    private readonly int _minimumShouldMatch;
    private readonly float _boost;
    private readonly int _shouldCount;
    private readonly bool _hasRequired;

    public BooleanWeight(List<(Weight, Occur)> clauses, int minimumShouldMatch, float boost)
    {
        _clauses = clauses;
        _minimumShouldMatch = minimumShouldMatch;
        _boost = boost;
        foreach ((_, Occur o) in clauses)
        {
            _shouldCount += o == Occur.Should ? 1 : 0;
            _hasRequired |= o is Occur.Must or Occur.Filter;
        }
    }

    // How many Should clauses a document needs: the explicit minimum, else one when nothing else is required, else none.
    private int Needed => _minimumShouldMatch > 0 ? _minimumShouldMatch : (_hasRequired ? 0 : 1);

    public override Scorer? GetScorer(LeafContext leaf)
    {
        int needed = Needed;
        if (_shouldCount < needed)
        {
            return null;
        }

        var required = new List<Scorer>();
        var scoring = new List<bool>();
        var should = new List<Scorer>();
        var excluded = new List<Scorer>();
        foreach ((Weight w, Occur o) in _clauses)
        {
            Scorer? s = w.GetScorer(leaf);
            switch (o)
            {
                case Occur.Must:
                case Occur.Filter:
                    if (s is null)
                    {
                        return null;
                    }

                    required.Add(s);
                    scoring.Add(o == Occur.Must);
                    break;
                case Occur.Should:
                    if (s is not null)
                    {
                        should.Add(s);
                    }

                    break;
                default:
                    if (s is not null)
                    {
                        excluded.Add(s);
                    }

                    break;
            }
        }

        if (should.Count < needed)
        {
            return null;
        }

        Scorer? optional = should.Count == 0 ? null : needed > 1 ? new MinShouldMatchScorer([.. should], needed) : should.Count == 1 ? should[0] : new BlockMaxWandScorer(should);
        Scorer inner;
        if (required.Count == 0)
        {
            if (optional is null)
            {
                return null; // only exclusions: nothing is selected
            }

            inner = optional;
        }
        else if (needed == 0)
        {
            Scorer req = required.Count == 1 && scoring[0] ? required[0] : new ConjunctionScorer([.. required], [.. scoring]);
            inner = optional is null ? req : new ReqOptScorer(req, optional);
        }
        else
        {
            required.Add(optional!);
            scoring.Add(true);
            inner = new ConjunctionScorer([.. required], [.. scoring]);
        }

        if (excluded.Count > 0)
        {
            inner = new ExclusionScorer(inner, [.. excluded]);
        }

        return _boost == 1f ? inner : new ScaledScorer(inner, _boost);
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        float must = 0f;
        float should = 0f;
        int shouldMatched = 0;
        var parts = new List<Explanation>();
        foreach ((Weight w, Occur o) in _clauses)
        {
            Explanation? e = w.Explain(leaf, docId);
            switch (o)
            {
                case Occur.Must:
                    if (e is null)
                    {
                        return null;
                    }

                    must += e.Value;
                    parts.Add(e);
                    break;
                case Occur.Filter:
                    if (e is null)
                    {
                        return null;
                    }

                    parts.Add(new Explanation(0f, "filter clause, matches without scoring", e));
                    break;
                case Occur.Should:
                    if (e is not null)
                    {
                        shouldMatched++;
                        should += e.Value;
                        parts.Add(e);
                    }

                    break;
                default:
                    if (e is not null)
                    {
                        return null;
                    }

                    break;
            }
        }

        if (shouldMatched < Needed || (!_hasRequired && shouldMatched == 0))
        {
            return null;
        }

        return new Explanation((must + should) * _boost, $"sum of matching clauses, boost {_boost.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)}, {shouldMatched} of {_shouldCount} should clauses matched (need {Needed})", [.. parts]);
    }
}
