using System.Reflection;
using Lucene.Net.Analysis;
using Lucene.Net.Analysis.En;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Analysis.Util;
using Lucene.Net.Documents;
using Lucene.Net.Facet;
using Lucene.Net.Facet.SortedSet;
using Lucene.Net.Index;
using Lucene.Net.Search;
using Lucene.Net.Search.Similarities;
using Lucene.Net.Store;
using Lucene.Net.Util;
using LuceneDocument = Lucene.Net.Documents.Document;
using LuceneFuzzyQuery = Lucene.Net.Search.FuzzyQuery;
using LuceneIndexSearcher = Lucene.Net.Search.IndexSearcher;
using LucenePhraseQuery = Lucene.Net.Search.PhraseQuery;
using LucenePrefixQuery = Lucene.Net.Search.PrefixQuery;

namespace OneIndex.Harness;

/// <summary>
/// Lucene.NET 4.8.0-beta00018 as a black-box comparator, written from its published API documentation. BM25 similarity
/// with caller-chosen k1 and b; the English chain is Lucene's EnglishAnalyzer, the Standard chain is StandardAnalyzer with
/// an empty stop set (lowercase, no stemming).
/// </summary>
public sealed class LuceneEngine : ITextEngine
{
    private const LuceneVersion Version48 = LuceneVersion.LUCENE_48;
    private const int MaxClauses = 1 << 20;

    private EngineSettings? _settings;
    private Analyzer? _analyzer;
    private FSDirectory? _dir;
    private IndexWriter? _writer;
    private DirectoryReader? _reader;
    private LuceneIndexSearcher? _searcher;
    private SortedSetDocValuesReaderState? _facetState;
    private readonly FacetsConfig _facets = new();
    private float _k1 = 1.2f, _b = 0.75f;

    /// <inheritdoc/>
    public string Name => "Lucene.NET";

    /// <inheritdoc/>
    public string Version => typeof(LuceneIndexSearcher).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "4.8.0-beta00018";

    /// <inheritdoc/>
    public bool IsAvailable(out string reason)
    {
        reason = "";
        return true;
    }

    /// <inheritdoc/>
    public bool Supports(Type queryType) => queryType != typeof(GeoQuery); // no spatial module in the matched comparator setup

    /// <inheritdoc/>
    public void Create(EngineSettings settings)
    {
        _settings = settings;
        BooleanQuery.MaxClauseCount = MaxClauses;
        _analyzer = settings.Analysis == AnalysisKind.English
            ? new EnglishAnalyzer(Version48)
            : new StandardAnalyzer(Version48, CharArraySet.Empty);
        System.IO.Directory.CreateDirectory(settings.Directory);
        _dir = FSDirectory.Open(settings.Directory);
        var config = new IndexWriterConfig(Version48, _analyzer)
        {
            OpenMode = OpenMode.CREATE,
            RAMBufferSizeMB = settings.RamBufferMb,
            Similarity = new BM25Similarity(_k1, _b),
        };
        _writer = new IndexWriter(_dir, config);
    }

    /// <inheritdoc/>
    public void Add(Doc doc)
    {
        var d = new LuceneDocument { new StringField("id", doc.Id, Field.Store.YES) };
        if (_settings!.Profile == IndexProfile.Beir)
        {
            d.Add(new TextField("title", doc.Title, Field.Store.NO));
            d.Add(new TextField("text", doc.Text, Field.Store.NO));
            d.Add(new TextField("all", doc.Title + " " + doc.Text, Field.Store.NO));
            _writer!.AddDocument(d);
            return;
        }

        d.Add(new TextField("text", doc.Text, Field.Store.NO));
        d.Add(new Int64Field("len", doc.Length, Field.Store.NO));
        d.Add(new NumericDocValuesField("lenDV", doc.Length));
        d.Add(new StringField("catf", doc.Category, Field.Store.NO));
        d.Add(new SortedSetDocValuesFacetField("cat", doc.Category));
        _writer!.AddDocument(_facets.Build(d));
    }

    /// <inheritdoc/>
    public void Commit() => _writer!.Commit();

    /// <inheritdoc/>
    public void OpenSearcher()
    {
        _reader?.Dispose();
        _reader = DirectoryReader.Open(_dir!);
        _searcher = new LuceneIndexSearcher(_reader) { Similarity = new BM25Similarity(_k1, _b) };
        _facetState = _settings!.Profile == IndexProfile.Bench ? new DefaultSortedSetDocValuesReaderState(_reader) : null;
    }

    /// <inheritdoc/>
    public void SetBm25(float k1, float b)
    {
        _k1 = k1;
        _b = b;
        if (_searcher is not null)
        {
            _searcher.Similarity = new BM25Similarity(k1, b);
        }
    }

    /// <inheritdoc/>
    public SearchResult Search(HarnessQuery query, int k)
    {
        var searcher = _searcher!;
        switch (query)
        {
            case MatchQuery m:
                return Run(searcher.Search(MatchToLucene(m), k));
            case TermsQuery t:
                return Run(searcher.Search(Terms(t.Terms), k));
            case PhraseQuery p:
                var phrase = new LucenePhraseQuery { Slop = 0 };
                foreach (var term in p.Terms)
                {
                    phrase.Add(new Term("text", term));
                }

                return Run(searcher.Search(phrase, k));
            case PrefixQuery px:
                var all = new BooleanQuery();
                foreach (var term in px.Required)
                {
                    all.Add(new TermQuery(new Term("text", term)), Occur.MUST);
                }

                all.Add(new LucenePrefixQuery(new Term("text", px.Prefix)), Occur.MUST);
                return Run(searcher.Search(all, k));
            case FuzzyQuery f:
                return Run(searcher.Search(new LuceneFuzzyQuery(new Term("text", f.Term), f.MaxEdits), k));
            case FilteredQuery fq:
                var filter = new BooleanQuery
                {
                    { NumericRangeQuery.NewInt64Range("len", fq.MinLength, fq.MaxLength, true, true), Occur.MUST },
                };
                var cats = new BooleanQuery();
                foreach (var c in fq.Categories)
                {
                    cats.Add(new TermQuery(new Term("catf", c)), Occur.SHOULD);
                }

                filter.Add(cats, Occur.MUST);
                return Run(searcher.Search(new Lucene.Net.Search.FilteredQuery(Terms(fq.Terms), new QueryWrapperFilter(filter)), k));
            case FacetQuery fc:
                var collector = new FacetsCollector();
                var top = FacetsCollector.Search(searcher, Terms(fc.Terms), k, collector);
                var counts = new SortedSetDocValuesFacetCounts(_facetState, collector);
                var result = counts.GetTopChildren(10, "cat");
                var facets = result is null
                    ? Array.Empty<(string, long)>()
                    : result.LabelValues.Select(lv => (lv.Label, Convert.ToInt64(lv.Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
                return Run(top) with { Facets = facets };
            case StatsQuery sq:
                var stats = new StatsCollector();
                searcher.Search(Terms(sq.Terms), stats);
                return new SearchResult([], stats.Count, null, (stats.Count, stats.Count == 0 ? 0 : stats.Min, stats.Count == 0 ? 0 : stats.Max, stats.Sum));
            case SortedQuery so:
                return Run(searcher.Search(Terms(so.Terms), k, new Sort(new SortField("lenDV", SortFieldType.INT64, true))));
            default:
                throw new NotSupportedException(query.GetType().Name);
        }
    }

    /// <inheritdoc/>
    public long IndexSizeBytes() => new DirectoryInfo(_settings!.Directory).EnumerateFiles().Sum(f => f.Length);

    /// <inheritdoc/>
    public void Dispose()
    {
        _reader?.Dispose();
        _writer?.Dispose();
        _dir?.Dispose();
        _analyzer?.Dispose();
    }

    private static readonly HashSet<string> IdOnly = ["id"];

    private SearchResult Run(TopDocs docs)
    {
        var hits = new Hit[docs.ScoreDocs.Length];
        for (var i = 0; i < hits.Length; i++)
        {
            var sd = docs.ScoreDocs[i];
            hits[i] = new Hit(_searcher!.Doc(sd.Doc, IdOnly).Get("id"), float.IsNaN(sd.Score) ? 0f : sd.Score);
        }

        return new SearchResult(hits, docs.TotalHits);
    }

    /// <summary>The public Explain output of the top hit of a query, and of the hit at rank 10 (a black-box look at how Lucene.NET forms a score).</summary>
    /// <param name="m">The query.</param>
    /// <returns>Text.</returns>
    public string ExplainTop(MatchQuery m)
    {
        var q = MatchToLucene(m);
        var docs = _searcher!.Search(q, 10);
        var sb = new System.Text.StringBuilder();
        foreach (var rank in new[] { 0, docs.ScoreDocs.Length - 1 })
        {
            if (rank < 0)
            {
                continue;
            }

            var sd = docs.ScoreDocs[rank];
            sb.AppendLine($"-- rank {rank + 1} id {_searcher.Doc(sd.Doc, IdOnly).Get("id")} score {sd.Score}");
            sb.AppendLine(_searcher.Explain(q, sd.Doc).ToString());
        }

        return sb.ToString();
    }

    /// <summary>The terms Lucene's English analyzer makes of a text, in order (observed through its public token stream API).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The terms.</returns>
    public static IReadOnlyList<string> Analyze(string text)
    {
        using var analyzer = new EnglishAnalyzer(Version48);
        using var stream = analyzer.GetTokenStream("f", new StringReader(text));
        var term = stream.AddAttribute<Lucene.Net.Analysis.TokenAttributes.ICharTermAttribute>();
        var terms = new List<string>();
        stream.Reset();
        while (stream.IncrementToken())
        {
            terms.Add(term.ToString());
        }

        stream.End();
        return terms;
    }

    private static BooleanQuery Terms(string[] terms)
    {
        var q = new BooleanQuery();
        foreach (var t in terms)
        {
            q.Add(new TermQuery(new Term("text", t)), Occur.SHOULD);
        }

        return q;
    }

    // The BEIR "match" semantics: the text is analyzed and the resulting terms are OR-ed per field.
    private Query MatchToLucene(MatchQuery m)
    {
        var builder = new QueryBuilder(_analyzer);
        Query Field(string field) => builder.CreateBooleanQuery(field, m.Text, Occur.SHOULD) ?? new BooleanQuery();
        switch (m.Layout)
        {
            case FieldLayout.Flat:
                return Field("all");
            case FieldLayout.MultiFieldMax:
                var dm = new DisjunctionMaxQuery(0f);
                dm.Add(Field("title"));
                dm.Add(Field("text"));
                return dm;
            case FieldLayout.Bm25F:
                throw new NotSupportedException("Lucene.NET has no BM25F query in 4.8.");
            default:
                var sum = new BooleanQuery();
                sum.Add(Field("title"), Occur.SHOULD);
                sum.Add(Field("text"), Occur.SHOULD);
                return sum;
        }
    }

    private sealed class StatsCollector : ICollector
    {
        private NumericDocValues? _values;

        public long Count { get; private set; }

        public long Min { get; private set; } = long.MaxValue;

        public long Max { get; private set; } = long.MinValue;

        public long Sum { get; private set; }

        public bool AcceptsDocsOutOfOrder => true;

        public void SetScorer(Scorer scorer)
        {
        }

        public void SetNextReader(AtomicReaderContext context) => _values = context.AtomicReader.GetNumericDocValues("lenDV");

        public void Collect(int doc)
        {
            var v = _values!.Get(doc);
            Count++;
            Sum += v;
            Min = Math.Min(Min, v);
            Max = Math.Max(Max, v);
        }
    }
}
