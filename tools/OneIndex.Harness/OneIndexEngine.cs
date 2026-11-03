using OneIndex.Aggregations;
using OneIndex.Analysis;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Harness;

/// <summary>
/// OneIndex behind the harness: the BEIR profile (title, text and a flat title+text field, English analysis, frequencies only) and
/// free-text <see cref="MatchQuery"/> / <see cref="TermsQuery"/> scoring; and the benchmark profile's term, facet and statistics rows.
/// The filtered row (a numeric range and three keyword values as non-scoring filters) and the sorted row (the two terms ordered by the `len` column, descending, ties by identifier) run through the query objects and the sort collector; the other rows are reported as unsupported until their queries exist.
/// </summary>
public sealed class OneIndexEngine : ITextEngine
{
    private EngineSettings? _settings;
    private FileSystemStorage? _storage;
    private IndexWriter? _writer;
    private IndexSnapshot? _snapshot;
    private SearchContext? _context;
    private IndexSearcher? _searcher;
    private string[] _idOf = [];
    private float _k1 = Bm25Parameters.Default.K1;
    private float _b = Bm25Parameters.Default.B;

    /// <inheritdoc/>
    public string Name => "OneIndex";

    /// <inheritdoc/>
    public string Version => typeof(IndexSearcher).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <inheritdoc/>
    public bool IsAvailable(out string reason)
    {
        reason = string.Empty;
        return true;
    }

    /// <inheritdoc/>
    public bool Supports(Type queryType) => queryType == typeof(MatchQuery) || queryType == typeof(PhraseQuery) || queryType == typeof(PrefixQuery) || queryType == typeof(FuzzyQuery) || queryType == typeof(TypoQuery) || queryType == typeof(TermsQuery) || queryType == typeof(FacetQuery) || queryType == typeof(StatsQuery) || queryType == typeof(SortedQuery) || queryType == typeof(DecayedQuery) || queryType == typeof(FilteredQuery) || (queryType == typeof(GeoQuery) && WithGeo);

    // The geo row indexes synthesized coordinates in a second field; it is opt-in so the other rows keep the matched index.
    private static bool WithGeo => Environment.GetEnvironmentVariable("ONEINDEX_BENCH_GEO") == "1";

    /// <inheritdoc/>
    public void Create(EngineSettings settings)
    {
        _settings = settings;
        Directory.CreateDirectory(settings.Directory);
        _storage = new FileSystemStorage(settings.Directory);
        Schema schema;
        if (settings.Profile == IndexProfile.Bench && settings.Analysis == AnalysisKind.Standard)
        {
            // The benchmark profile: body text, a numeric column and a keyword facet column, standard analysis.
            SchemaBuilder builder = new SchemaBuilder().Id("id")
                .Add(new FieldDefinition("text", FieldType.Text) { Analyzer = "standard", IndexOptions = IndexOptions.Positions, Stored = false })
                .Add(new FieldDefinition("len", FieldType.Integral) { Column = true, Stored = false })
                .Add(new FieldDefinition("cat", FieldType.Keyword) { Facet = true, Stored = false });
            schema = (WithGeo ? builder.Add(new FieldDefinition("loc", FieldType.GeoPoint) { Column = true, Stored = false }) : builder).Build();
        }
        else if (settings.Profile == IndexProfile.Beir && settings.Analysis == AnalysisKind.English)
        {
            FieldDefinition Text(string name) => new(name, FieldType.Text) { Analyzer = "en", IndexOptions = ProximityIndexOptions, Stored = false };
            // A match in the title says more than one in the body: its BM25F weight is 3 (chosen on the four BEIR gate sets, docs/scoring.md).
            float titleBoost = float.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_TITLE_WEIGHT"), System.Globalization.CultureInfo.InvariantCulture, out float w) ? w : 3f;
            schema = new SchemaBuilder().Id("id").Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "en", IndexOptions = ProximityIndexOptions, Stored = false, Boost = titleBoost }).Add(Text("text")).Add(Text("all")).Build();
        }
        else
        {
            throw new NotSupportedException("OneIndex runs the BEIR profile with English analysis and the benchmark profile with standard analysis.");
        }

        _settings = settings;
        _writer = IndexWriter.Open(_storage, schema, new IndexWriterOptions { MergePolicy = new TieredMergePolicy { SizeRatio = double.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_SIZE_RATIO"), System.Globalization.CultureInfo.InvariantCulture, out double sr) ? sr : 2 }, MergeScheduler = new ThreadedMergeScheduler(), AutoMerge = Environment.GetEnvironmentVariable("ONEINDEX_AUTOMERGE") != "0", MaxBufferedDocuments = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_BUFFERED_DOCS"), out int buffered) ? buffered : 20_000 });
    }

    /// <inheritdoc/>
    public void Add(Doc doc)
    {
        if (_settings!.Profile == IndexProfile.Bench)
        {
            Document d = new Document().Add("id", doc.Id).Add("text", doc.Text).Add("len", doc.Length).Add("cat", doc.Category);
            if (WithGeo)
            {
                (double lat, double lng) = GeoSynth.For(doc.Id);
                d.Add("loc", FieldValue.FromGeoPoint(lat, lng));
            }

            // Identifiers are unique and the comparator appends without looking for a previous version, so the bulk path is the matched call.
            _writer!.AddUnique(d);
        }
        else
        {
            // Corpus identifiers are unique, so the bulk path skips the per-document lookup in every segment.
            _writer!.AddUnique(new Document().Add("id", doc.Id).Add("title", doc.Title).Add("text", doc.Text).Add("all", doc.Title + " " + doc.Text));
        }
    }

    /// <inheritdoc/>
    public void Commit()
    {
        // Like the comparator, a commit does not wait for the merges it may start: they run in the background.
        _writer!.Commit();
    }

    private int SegmentsNow()
    {
        using IndexSnapshot s = _writer!.OpenSnapshot();
        return s.SegmentCount;
    }

    /// <inheritdoc/>
    public void OpenSearcher()
    {
        // Query rows run on a settled index, so pending merges finish first.
        // A finished merge is installed by the next commit, so settle by committing until a round installs nothing new. Lucene.NET
        // installs merges as they finish; this is the same settled state.
        for (int round = 0; round < 8; round++)
        {
            _writer!.WaitForMerges();
            int before = SegmentsNow();
            _writer.Commit();
            if (SegmentsNow() == before)
            {
                break;
            }
        }

        _snapshot?.Dispose();
        _snapshot = _writer!.OpenSnapshot();
        Console.Error.WriteLine("segments: " + string.Join(",", Enumerable.Range(0, _snapshot.SegmentCount).Select(i => _snapshot.GetSegment(i).MaxDoc)));
        _context = SnapshotSearch.CreateContext(_snapshot);
        // Hits carry document numbers; the identifier of each comes from the stored source, so a reopened index needs no side table.
        _idOf = new string[_context.MaxDoc];
        foreach (LeafContext leaf in _context.Leaves)
        {
            IStoredFieldsReader stored = leaf.Reader.StoredFields ?? throw new InvalidOperationException("The index keeps no stored source.");
            for (int d = 0; d < leaf.Reader.MaxDoc; d++)
            {
                _idOf[leaf.DocBase + d] = stored.GetDocument(d).Get("id").AsText();
            }
        }

        _searcher = new IndexSearcher(_context, new QueryEngine(Engine()));
    }

    /// <inheritdoc/>
    public void SetBm25(float k1, float b)
    {
        _k1 = k1;
        _b = b;
        if (_context is not null)
        {
            _searcher = new IndexSearcher(_context, new QueryEngine(Engine()));
        }
    }

    /// <inheritdoc/>
    public SearchResult Search(HarnessQuery query, int k)
    {
        Query q = query switch
        {
            MatchQuery m => Match(m),
            TermsQuery t => Proximate(TermsOf(t.Terms), t.Terms),
            FacetQuery f => TermsOf(f.Terms),
            StatsQuery st => TermsOf(st.Terms),
            PhraseQuery p => new global::OneIndex.Search.PhraseQuery("text", p.Terms),
            SortedQuery so => TermsOf(so.Terms),
            DecayedQuery dq => new FunctionScoreQuery(TermsOf(dq.Terms), [new DecayFunction(DecayShape.Gauss, "len", dq.Origin, dq.Scale)]),
            FilteredQuery fq => Filtered(fq),
            GeoQuery g => new GeoRadiusQuery("loc", g.Latitude, g.Longitude, g.RadiusMeters),
            PrefixQuery px => With(px.Required, new global::OneIndex.Search.PrefixQuery("text", px.Prefix)),
            FuzzyQuery fz => new global::OneIndex.Search.FuzzyQuery("text", fz.Term) { MaxEdits = fz.MaxEdits },
            TypoQuery ty when TypoMatched && ty.Prefix => With(ty.Required, LuceneShaped(ty.Word)),
            TypoQuery ty => With(ty.Required, new TypoTermQuery(["text"], ty.Word) { MaxTypos = new TypoSettings().Budget(ty.Word.Length), Prefix = ty.Prefix }),
            _ => throw new NotSupportedException(query.GetType().Name),
        };
        if (query is FacetQuery)
        {
            // The top page and the ten most frequent category values over all matches, in one pass.
            var pageCollector = new TopScoreCollector(k, countExactly: true);
            var facets = new FacetCollector(_context!.Schema, [new FacetRequest("cat") { MaxValues = 10, SortBy = FacetSort.Count }]);
            _searcher!.Search(q, new MultiCollector([pageCollector, facets]));
            TopDocs page = pageCollector.GetTopDocs();
            Hit[] pageHits = [.. page.Hits.Select(h => new Hit(_idOf[h.DocId], h.Score))];
            return new SearchResult(pageHits, page.TotalHits, [.. facets.GetResults()[0].Values.Select(v => (v.Value, v.Count))]);
        }

        if (query is StatsQuery)
        {
            var stats = new AggregationCollector(_context!.Schema, [new StatsAggregation("len", "len")]);
            _searcher!.Search(q, stats);
            var s = stats.GetResults().Get<StatsResult>("len");
            return new SearchResult([], s.Count, null, (s.Count, (long)(s.Min ?? 0), (long)(s.Max ?? 0), (long)s.Sum));
        }
        if (query is SortedQuery)
        {
            var sorted = new TopFieldCollector([new FieldSort("len") { Direction = SortDirection.Descending }], k, tieBreakField: "id");
            _searcher!.Search(q, sorted);
            TopFieldDocs page = sorted.GetTopDocs();
            return new SearchResult([.. page.Hits.Select(h => new Hit(_idOf[h.DocId], 0f))], page.TotalHits);
        }

        TopDocs top = _searcher!.Search(q, k, countExactly: query is GeoQuery);
        var hits = new Hit[top.Hits.Count];
        for (int i = 0; i < hits.Length; i++)
        {
            hits[i] = new Hit(_idOf[top.Hits[i].DocId], top.Hits[i].Score);
        }

        return new SearchResult(hits, top.TotalHits);
    }

    // The two terms score; the length range and the three categories only select.
    private static BooleanQuery Filtered(FilteredQuery fq)
    {
        var categories = new BooleanQuery([.. fq.Categories.Select(c => new BooleanClause(new TermQuery("cat", c), Occur.Should))]) { MinimumShouldMatch = 1 };
        return new BooleanQuery(
        [
            new BooleanClause(TermsOf(fq.Terms), Occur.Must),
            new BooleanClause(new RangeQuery("len", FieldValue.FromInt64(fq.MinLength), FieldValue.FromInt64(fq.MaxLength)), Occur.Filter),
            new BooleanClause(categories, Occur.Filter),
        ]);
    }

    /// <summary>The terms the English analysis makes of a text, in order.</summary>
    /// <param name="text">The text.</param>
    /// <returns>The terms.</returns>
    public static IReadOnlyList<string> Analyze(string text)
    {
        Analyzer analyzer = AnalyzerRegistry.Create("en");
        var terms = new List<string>();
        TokenSource tokens = analyzer.GetTokens(text.AsMemory());
        while (tokens.MoveNext())
        {
            terms.Add(tokens.Token.Term.ToString());
        }

        return terms;
    }

    /// <summary>
    /// Runs a disjunction of BM25F terms twice, pruned and exhaustive, and reports how many documents the pruned run scored out of the
    /// documents that match, and whether the two top-k pages are identical.
    /// </summary>
    /// <param name="terms">The analyzed query terms.</param>
    /// <param name="k">The page size.</param>
    /// <returns>Matching documents, documents scored by the pruned run, and whether the pages are identical.</returns>
    public (long Matching, long Scored, bool Identical) MeasureWand(IReadOnlyList<string> terms, int k)
    {
        Query q = new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
        TopDocs exact = _searcher!.Search(q, k, countExactly: true);
        var pruned = new CountingCollector(k);
        _searcher.Search(q, pruned);
        TopDocs page = pruned.Inner.GetTopDocs();
        return (exact.TotalHits, pruned.Collected, exact.Hits.SequenceEqual(page.Hits));
    }

    /// <summary>Bytes the calling thread allocates answering one pruned top-k BM25F disjunction (the search call only, after the query object exists).</summary>
    /// <param name="terms">The analyzed query terms.</param>
    /// <param name="k">The page size.</param>
    /// <returns>Allocated bytes.</returns>
    public long MeasureSearchAllocation(IReadOnlyList<string> terms, int k)
    {
        Query q = new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        _ = _searcher!.Search(q, k, countExactly: false);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <inheritdoc/>
    public long IndexSizeBytes() => new DirectoryInfo(_settings!.Directory).EnumerateFiles().Sum(f => f.Length);

    /// <inheritdoc/>
    public void Dispose()
    {
        _snapshot?.Dispose();
        _writer?.Dispose();
    }

    // The required terms must all appear; the last clause is the one whose expansion is being measured.
    // Information row: ONEINDEX_TYPO_MATCHED=1 runs the as-you-type typo row with the match set of the comparator's query (the word as a
    // prefix, or the word within its typo budget with the first character fixed), which has no typos in the beginning of longer words.
    private static readonly bool TypoMatched = Environment.GetEnvironmentVariable("ONEINDEX_TYPO_MATCHED") == "1";

    private static BooleanQuery LuceneShaped(string word)
    {
        int edits = new TypoSettings().Budget(word.Length);
        Query fuzzy = edits == 0 ? new TermQuery("text", word) : new global::OneIndex.Search.FuzzyQuery("text", word) { MaxEdits = edits, PrefixLength = 1 };
        return new BooleanQuery([new BooleanClause(new global::OneIndex.Search.PrefixQuery("text", word), Occur.Should), new BooleanClause(fuzzy, Occur.Should)]);
    }

    private static BooleanQuery With(string[] required, Query last)
        => new([.. required.Select(t => new BooleanClause(new TermQuery("text", t), Occur.Must)), new BooleanClause(last, Occur.Must)]);

    // Information runs: ONEINDEX_PROXIMITY=<weight> adds the term-proximity bonus to the disjunction rows (Terms2, Terms5) so that its cost can be read.
    private static Query Proximate(BooleanQuery disjunction, string[] terms)
        => ProximityWeight > 0f && terms.Length >= 2 ? new ProximityQuery(disjunction, [.. terms.Select(t => new TermQuery("text", t))]) { Weight = ProximityWeight } : disjunction;

    // Information runs: ONEINDEX_CONJUNCTION=1 requires every word in the Terms rows, which is what user text compiles to by default (the match sets then differ from the comparator's).
    private static readonly Occur TermsOccur = Environment.GetEnvironmentVariable("ONEINDEX_CONJUNCTION") == "1" ? Occur.Must : Occur.Should;

    private static BooleanQuery TermsOf(string[] terms) => new([.. terms.Select(x => new BooleanClause(new TermQuery("text", x), TermsOccur))]);

    private QueryEngineOptions Engine() => new() { Bm25 = new Bm25Parameters(_k1, _b), Overflow = ExpansionOverflow.KeepMostFrequent };

    // Information runs only: ONEINDEX_TYPO=on matches the Bm25F layout's words with the default typo budgets and the typo factor.
    private static readonly bool TypoTolerant = Environment.GetEnvironmentVariable("ONEINDEX_TYPO") == "on";

    // Information runs: ONEINDEX_PROXIMITY=<weight> keeps positions in the title and text fields and adds the term-proximity bonus of that weight to the Bm25F layout.
    private static readonly float ProximityWeight = float.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_PROXIMITY"), System.Globalization.CultureInfo.InvariantCulture, out float pw) ? pw : 0f;

    // Positions whenever the variable is set (also at 0, the baseline of a sweep over one index).
    private static IndexOptions ProximityIndexOptions => Environment.GetEnvironmentVariable("ONEINDEX_PROXIMITY") is not null ? IndexOptions.Positions : IndexOptions.Freqs;

    private static global::OneIndex.Search.Query Match(MatchQuery m)
    {
        // The text is analyzed like the corpus and the terms are OR-ed, one clause per token as every engine in the harness does.
        Analyzer analyzer = AnalyzerRegistry.Create("en");
        var terms = new List<string>();
        TokenSource tokens = analyzer.GetTokens(m.Text.AsMemory());
        while (tokens.MoveNext())
        {
            terms.Add(tokens.Token.Term.ToString());
        }

        IEnumerable<Query> perTerm = m.Layout switch
        {
            FieldLayout.Flat => terms.Select(t => new TermQuery("all", t)),
            FieldLayout.Bm25F when TypoTolerant => terms.Select(t => (Query)new TypoTermQuery(["title", "text"], t) { MaxTypos = new TypoSettings().Budget(t.Length) }),
            FieldLayout.Bm25F => terms.Select(t => new MultiFieldTermQuery(["title", "text"], t)),
            FieldLayout.MultiFieldSum => terms.SelectMany(t => new Query[] { new TermQuery("title", t), new TermQuery("text", t) }),
            _ => throw new NotSupportedException($"{m.Layout} needs a dis-max query, which OneIndex does not have; its default is {FieldLayout.Bm25F}."),
        };
        Query[] queries = [.. perTerm];
        var disjunction = new BooleanQuery([.. queries.Select(q => new BooleanClause(q, Occur.Should))]);
        if (ProximityWeight > 0f && m.Layout == FieldLayout.Bm25F && queries.Length >= 2)
        {
            return new ProximityQuery(disjunction, queries) { Weight = ProximityWeight };
        }

        return disjunction;
    }
}

internal sealed class CountingCollector(int k) : Collector
{
    public TopScoreCollector Inner { get; } = new(k, countExactly: false);

    public long Collected { get; private set; }

    public override ScoreMode ScoreMode => Inner.ScoreMode;

    public override LeafCollector GetLeafCollector(LeafContext leaf) => new Leaf(this, Inner.GetLeafCollector(leaf));

    private sealed class Leaf(CountingCollector owner, LeafCollector inner) : LeafCollector
    {
        public override void SetScorer(Scorer scorer) => inner.SetScorer(scorer);

        public override void Collect(int docId)
        {
            owner.Collected++;
            inner.Collect(docId);
        }
    }
}

/// <summary>Names the engines the harness can drive.</summary>
public static class Engines
{
    /// <summary>All known engine names.</summary>
    public static readonly string[] Names = ["lucene", "oneindex"];

    /// <summary>Creates an engine by name.</summary>
    public static ITextEngine Create(string name) => name.ToLowerInvariant() switch
    {
        "lucene" => new LuceneEngine(),
        "oneindex" => new OneIndexEngine(),
        _ => throw new ArgumentException($"Unknown engine '{name}'. Known: {string.Join(", ", Names)}."),
    };

    /// <summary>The engines that can run now.</summary>
    public static IEnumerable<string> Available() => Names.Where(n =>
    {
        using var e = Create(n);
        return e.IsAvailable(out _);
    });
}
