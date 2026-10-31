using System.Buffers;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public class SearchContractTests
{
    /// <summary>A segment that only has live docs; enough to drive the searcher.</summary>
    private sealed class FakeSegment(int maxDoc, HashSet<int>? deleted = null) : ISegmentReader
    {
        public SegmentInfo Info { get; } = new("seg", maxDoc, 0, 0, []);

        public int MaxDoc => maxDoc;

        public ILiveDocs? LiveDocs { get; } = deleted is null ? null : new Deletes(deleted);

        public IStoredFieldsReader? StoredFields => null;

        public ITextFieldReader? GetTextField(string field) => null;

        public IColumnReader? GetColumn(string field) => null;

        public IPointIndexReader? GetPoints(string field) => null;

        public void Dispose()
        {
        }

        private sealed class Deletes(HashSet<int> set) : ILiveDocs
        {
            public int DeletedCount => set.Count;

            public bool IsLive(int docId) => !set.Contains(docId);
        }
    }

    /// <summary>Scores each doc with a function of its id; matches every doc of the segment.</summary>
    private sealed class FakeScorer(int maxDoc, Func<int, float> score) : Scorer
    {
        private int _doc = -1;

        public float MinCompetitive { get; private set; } = float.NegativeInfinity;

        public override int DocId => _doc;

        public override long Cost => maxDoc;

        public override int NextDoc() => _doc = _doc + 1 < maxDoc ? _doc + 1 : NoMoreDocs;

        public override int Advance(int target) => _doc = target < maxDoc ? target : NoMoreDocs;

        public override float Score() => score(_doc);

        public override void SetMinCompetitiveScore(float minScore) => MinCompetitive = minScore;
    }

    private sealed class FakeFactory(Func<int, float> score) : IWeightFactory
    {
        public int Created;

        public Weight CreateWeight(Query query, SearchContext context, ScoreMode scoreMode)
        {
            Created++;
            return new FakeWeight(score);
        }

        private sealed class FakeWeight(Func<int, float> score) : Weight
        {
            public override Scorer? GetScorer(LeafContext leaf) => leaf.Reader.MaxDoc == 0 ? null : new FakeScorer(leaf.Reader.MaxDoc, d => score(leaf.DocBase + d));
        }
    }

    private static IndexSearcher Searcher(Func<int, float> score, params ISegmentReader[] segments)
        => new(new SearchContext(Schema.Empty, segments), new FakeFactory(score));

    [Fact]
    public void TopKOrdersByScoreThenLowerDocIdAcrossSegments()
    {
        // Docs 0..29 over three segments; scores repeat every 5 docs so that ties are everywhere.
        IndexSearcher searcher = Searcher(d => d % 5, new FakeSegment(10), new FakeSegment(0), new FakeSegment(12), new FakeSegment(8));
        TopDocs top = searcher.Search(new MatchAllQuery(), 7);
        Assert.Equal(30, top.TotalHits);
        Assert.True(top.TotalHitsExact);
        Assert.Equal([4, 9, 14, 19, 24, 29, 3], top.Hits.Select(h => h.DocId));
        Assert.Equal([4f, 4f, 4f, 4f, 4f, 4f, 3f], top.Hits.Select(h => h.Score));
    }

    [Fact]
    public void TopKMatchesABruteForceSortOnRandomScores()
    {
        var rng = new Random(77);
        for (int round = 0; round < 200; round++)
        {
            int segments = rng.Next(1, 5);
            ISegmentReader[] readers = Enumerable.Range(0, segments).Select(_ => (ISegmentReader)new FakeSegment(rng.Next(0, 60))).ToArray();
            int total = readers.Sum(r => r.MaxDoc);
            float[] scores = Enumerable.Range(0, total).Select(_ => (float)rng.Next(0, 20)).ToArray();
            int k = rng.Next(1, 25);
            TopDocs top = Searcher(d => scores[d], readers).Search(new MatchAllQuery(), k);
            var expected = Enumerable.Range(0, total).OrderByDescending(d => scores[d]).ThenBy(d => d).Take(k).ToArray();
            Assert.Equal(expected, top.Hits.Select(h => h.DocId));
            Assert.Equal(total, top.TotalHits);
        }
    }

    [Fact]
    public void DeletedDocumentsAreSkippedAndCounted()
    {
        IndexSearcher searcher = Searcher(d => d, new FakeSegment(10, [9, 8, 3]));
        TopDocs top = searcher.Search(new MatchAllQuery(), 3);
        Assert.Equal([7, 6, 5], top.Hits.Select(h => h.DocId));
        var count = new CountCollector();
        searcher.Search(new MatchAllQuery(), count);
        Assert.Equal(7, count.Count);
        Assert.Equal(ScoreMode.CompleteNoScores, count.ScoreMode);
    }

    [Fact]
    public void TheCollectorHandsTheScorerARisingThreshold()
    {
        var seg = new FakeSegment(100);
        var scorer = new FakeScorer(100, d => d);
        var collector = new TopScoreCollector(3, countExactly: false);
        LeafCollector leaf = collector.GetLeafCollector(new LeafContext(seg, 0, 0));
        leaf.SetScorer(scorer);
        float last = float.NegativeInfinity;
        for (int doc = scorer.NextDoc(); doc != DocIdSetIterator.NoMoreDocs; doc = scorer.NextDoc())
        {
            leaf.Collect(doc);
            Assert.True(scorer.MinCompetitive >= last);
            last = scorer.MinCompetitive;
        }

        Assert.Equal(97f, scorer.MinCompetitive);
    }

    [Fact]
    public void TopKCollectionAllocatesNothingPerDocument()
    {
        var seg = new FakeSegment(50_000);
        var scorer = new FakeScorer(50_000, d => (d * 2654435761u) % 1000);
        var collector = new TopScoreCollector(10);
        LeafCollector leaf = collector.GetLeafCollector(new LeafContext(seg, 0, 0));
        leaf.SetScorer(scorer);
        for (int i = 0; i < 1000; i++)
        {
            leaf.Collect(scorer.NextDoc());
        }

        // Each window collects a stretch of documents; the scorer is not rewound, so the windows walk it through.
        TestHelpers.AssertNoAllocation("top-k collection", () =>
        {
            for (int n = 0; n < 100; n++)
            {
                int doc = scorer.NextDoc();
                if (doc != DocIdSetIterator.NoMoreDocs)
                {
                    leaf.Collect(doc);
                }
            }
        }, windows: 30);
    }

    [Fact]
    public void NaNScoresRankLastAndCancellationIsHonoured()
    {
        IndexSearcher searcher = Searcher(d => d == 2 ? float.NaN : d, new FakeSegment(5));
        Assert.Equal([4, 3, 1, 0, 2], searcher.Search(new MatchAllQuery(), 5).Hits.Select(h => h.DocId));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => searcher.Search(new MatchAllQuery(), new CountCollector(), cts.Token));
    }

    [Fact]
    public void ContextNumbersDocumentsAcrossSegments()
    {
        var context = new SearchContext(Schema.Empty, [new FakeSegment(10), new FakeSegment(5), new FakeSegment(7)]);
        Assert.Equal([0, 10, 15], context.Leaves.Select(l => l.DocBase));
        Assert.Equal([0, 1, 2], context.Leaves.Select(l => l.Ordinal));
        Assert.Equal(22, context.MaxDoc);
        Assert.Equal(0, context.GetDocFreq("f", "t"u8));
        Assert.Equal(new FieldStatistics(0, 0, 0), context.GetFieldStatistics("f"));
    }

    private sealed class Describe : QueryVisitor<string>
    {
        public override string VisitTerm(TermQuery query) => $"term({query.Field}:{query.Term})";

        public override string VisitBoolean(BooleanQuery query) => "bool(" + string.Join(" ", query.Clauses.Select(c => c.Occur + ":" + c.Query.Accept(this))) + ")";

        public override string VisitMatchAll(MatchAllQuery query) => "all";
    }

    [Fact]
    public void QueriesAreValuesAndDispatchThroughTheVisitor()
    {
        var q = BooleanQuery.Of(new BooleanClause(new TermQuery("title", "cat"), Occur.Must), new BooleanClause(new MatchAllQuery(), Occur.Filter));
        Assert.Equal("bool(Must:term(title:cat) Filter:all)", q.Accept(new Describe()));
        var same = BooleanQuery.Of(new BooleanClause(new TermQuery("title", "cat"), Occur.Must), new BooleanClause(new MatchAllQuery(), Occur.Filter));
        Assert.Equal(q, same);
        Assert.Equal(q.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(q, same with { MinimumShouldMatch = 1 });
        Assert.Equal(new PhraseQuery("f", ["a", "b"]) { Slop = 2 }, new PhraseQuery("f", ["a", "b"]) { Slop = 2 });
        Assert.NotEqual(new PhraseQuery("f", ["a", "b"]), new PhraseQuery("f", ["b", "a"]));
        Assert.Equal(2f, (new TermQuery("f", "t") { Boost = 2 }).Boost);

        var range = new RangeQuery("year", FieldValue.FromInt64(2000), null) { IncludeLower = false };
        Assert.Throws<NotSupportedException>(() => range.Accept(new Describe()));
        Assert.IsType<FuzzyQuery>(new FuzzyQuery("f", "t"));
        Assert.Equal(2, new FuzzyQuery("f", "t").MaxEdits);
        Assert.NotNull(new PrefixQuery("f", "p"));
    }
}
