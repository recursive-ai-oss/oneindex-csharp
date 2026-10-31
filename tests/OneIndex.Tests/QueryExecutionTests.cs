using OneIndex.Fuzz;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class QueryExecutionTests
{
    private static readonly Schema Schema = new SchemaBuilder().Id("id")
        .Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "whitespace-lowercase", Boost = 2f })
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase" })
        .Add(new FieldDefinition("tag", FieldType.Keyword))
        .Integral("num")
        .Build();

    private static (IndexSnapshot Snapshot, IndexSearcher Searcher) Build(IEnumerable<(string Id, string Title, string Body, string Tag, long Num)> docs, QueryEngineOptions? options = null, int bufferedDocs = 100)
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, Schema, new IndexWriterOptions { MaxBufferedDocuments = bufferedDocs, AutoMerge = false }))
        {
            foreach ((string id, string title, string body, string tag, long num) in docs)
            {
                w.Upsert(new Document().Add("id", id).Add("title", title).Add("body", body).Add("tag", tag).Add("num", num));
            }

            w.Commit();
        }

        IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        return (snapshot, SnapshotSearch.CreateSearcher(snapshot, options));
    }

    [Fact]
    public void RandomCorporaAndQueriesAgreeWithTheBruteForceReference()
    {
        long queries = 0;
        long hits = 0;
        int merged = 0;
        int multiSegment = 0;
        int errors = 0;
        int cases = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_QUERY_CASES"), out int n) ? n : 600;
        for (int seed = 0; seed < cases; seed++)
        {
            QueryTarget.CaseStats s = QueryTarget.RunSeed(seed);
            queries += s.Queries;
            hits += s.Hits;
            merged += s.Merged ? 1 : 0;
            multiSegment += s.Segments > 1 ? 1 : 0;
            errors += s.ExpectedErrors;
        }

        Assert.True(multiSegment > cases / 4, $"only {multiSegment} cases had several segments");
        Assert.True(merged > cases / 4);
        Assert.True(hits > 10_000, $"only {hits} hits over {queries} queries");
        Assert.True(errors > 0);
    }

    [Fact]
    public void Bm25FFollowsTheFormulaByHand()
    {
        // Two documents; the term "fox" is in the title of d1 once and in the bodies of d1 and d2.
        var (snap, searcher) = Build([("d1", "fox", "the quick brown fox", "a", 1), ("d2", "dog", "a lazy fox sleeps today", "b", 2)]);
        using (snap)
        {
            Bm25Parameters p = Bm25Parameters.Default;
            var q = new MultiFieldTermQuery(["title", "body"], "fox");
            TopDocs top = searcher.Search(q, 5);
            Assert.Equal(2, top.Hits.Count);
            // N = 2 (both fields: title has 2 docs, body 2 docs), n = 2 (body df).
            double idf = Math.Log(1 + ((2 - 2 + 0.5) / (2 + 0.5)));
            double AvgNorm(int len, double avg) => (1 - p.B) + (p.B * len / avg);
            // d1: title len 1, avg title len 1; body len 4, avg body len 4.5.
            double x1 = (2.0 * 1 / AvgNorm(1, 1.0)) + (1.0 * 1 / AvgNorm(4, 4.5));
            double x2 = 1.0 * 1 / AvgNorm(5, 4.5);
            Assert.Equal(idf * x1 / (p.K1 + x1), top.Hits[0].Score, 4);
            Assert.Equal(idf * x2 / (p.K1 + x2), top.Hits[1].Score, 4);
            Assert.Equal(0, top.Hits[0].DocId);

            Explanation e = searcher.Explain(q, 0)!;
            Assert.Equal(top.Hits[0].Score, e.Value);
            string text = e.ToString();
            Assert.Contains("idf", text);
            Assert.Contains("field title", text);
            Assert.Contains("tf 1", text);
        }
    }

    [Fact]
    public void SaturationIsAppliedToTheSumNotToEachField()
    {
        var (snap, searcher) = Build([("d1", "x", "x", "a", 1), ("d2", "y", "z", "b", 2), ("d3", "y", "z", "b", 3)]);
        using (snap)
        {
            float combined = searcher.Search(new MultiFieldTermQuery(["title", "body"], "x"), 1).Hits[0].Score;
            float summed = searcher.Search(BooleanQuery.Of(new BooleanClause(new TermQuery("title", "x"), Occur.Should), new BooleanClause(new TermQuery("body", "x"), Occur.Should)), 1).Hits[0].Score;
            Assert.True(combined < summed, "BM25F saturates the combined frequency, so it scores below the sum of two saturated fields");
        }
    }

    [Fact]
    public void PhrasesHonourOrderAndSlop()
    {
        var (snap, searcher) = Build([
            ("exact", "t", "the quick brown fox", "a", 1),
            ("gap1", "t", "the quick red brown fox", "a", 2),
            ("swap", "t", "brown quick fox", "a", 3),
            ("far", "t", "quick x x x x brown", "a", 4)]);
        using (snap)
        {
            int[] Matches(int slop) => [.. searcher.Search(new PhraseQuery("body", ["quick", "brown"]) { Slop = slop }, 10).Hits.Select(h => h.DocId)];
            Assert.Equal([0], Matches(0));
            Assert.Equal([0, 1], Matches(1));
            Assert.Equal([0, 1], Matches(3));
            Assert.Equal([0, 1, 3], Matches(4));
            // Exact beats stretched.
            TopDocs top = searcher.Search(new PhraseQuery("body", ["quick", "brown"]) { Slop = 4 }, 10);
            Assert.True(top.Hits[0].Score > top.Hits[1].Score && top.Hits[1].Score > top.Hits[2].Score);
            Assert.Throws<QueryException>(() => searcher.Search(new PhraseQuery("tag", ["a", "b"]), 3));
        }
    }

    [Fact]
    public void ExpansionLimitRaisesATypedErrorOrKeepsTheMostFrequentTerms()
    {
        var docs = new List<(string, string, string, string, long)>();
        for (int i = 0; i < 40; i++)
        {
            docs.Add(("d" + i, "t", "w" + (i % 20) + (i < 8 ? " w0" : string.Empty), "a", i));
        }

        var (snap, searcher) = Build(docs, new QueryEngineOptions { MaxExpandedTerms = 5 });
        using (snap)
        {
            TooManyTermsException e = Assert.Throws<TooManyTermsException>(() => searcher.Search(new PrefixQuery("body", "w"), 3));
            Assert.Equal("body", e.Field);
            Assert.Equal(5, e.Limit);
        }

        var (snap2, searcher2) = Build(docs, new QueryEngineOptions { MaxExpandedTerms = 1, Overflow = ExpansionOverflow.KeepMostFrequent });
        using (snap2)
        {
            // w0 is the most frequent term (documents 0 to 7 and 20): only it is kept.
            TopDocs top = searcher2.Search(new PrefixQuery("body", "w"), 100, countExactly: true);
            Assert.Equal(9, top.TotalHits);
        }
    }

    [Fact]
    public void RangeWorksOnNumbersAndKeywordsWithoutAPointIndex()
    {
        var docs = Enumerable.Range(0, 30).Select(i => ("d" + i, "t", "b", "k" + (i % 5), (long)(i - 10))).ToList();
        var (snap, searcher) = Build(docs, bufferedDocs: 7);
        using (snap)
        {
            long Count(RangeQuery q) => searcher.Search(q, 1, countExactly: true).TotalHits;
            Assert.Equal(11, Count(new RangeQuery("num", FieldValue.FromInt64(-5), FieldValue.FromInt64(5))));
            Assert.Equal(9, Count(new RangeQuery("num", FieldValue.FromInt64(-5), FieldValue.FromInt64(5)) { IncludeLower = false, IncludeUpper = false }));
            Assert.Equal(30, Count(new RangeQuery("num", null, null)));
            Assert.Equal(10, Count(new RangeQuery("num", FieldValue.FromInt64(10), null)));
            Assert.Equal(0, Count(new RangeQuery("num", FieldValue.FromInt64(long.MaxValue), null) { IncludeLower = false }));
            Assert.Equal(18, Count(new RangeQuery("tag", FieldValue.FromText("k1"), FieldValue.FromText("k3"))));
            Assert.Equal(6, Count(new RangeQuery("tag", FieldValue.FromText("k1"), FieldValue.FromText("k3")) { IncludeLower = false, IncludeUpper = false }));
            Assert.Throws<QueryException>(() => Count(new RangeQuery("num", FieldValue.FromText("a"), null)));
        }
    }

    [Fact]
    public void FuzzyAndRegexFindTheTermsTheirDefinitionsSay()
    {
        var (snap, searcher) = Build([("d1", "t", "apple apply ample angle", "a", 1), ("d2", "t", "maple", "a", 2)]);
        using (snap)
        {
            long Total(Query q) => searcher.Search(q, 5, countExactly: true).TotalHits;
            Assert.Equal(2, Total(new FuzzyQuery("body", "aple") { MaxEdits = 1 })); // apple and ample are in d1, maple in d2: one insertion each
            Assert.Equal(2, Total(new FuzzyQuery("body", "aple") { MaxEdits = 2 }));
            Assert.Equal(1, Total(new FuzzyQuery("body", "apple") { MaxEdits = 0 }));
            Assert.Equal(2, Total(new FuzzyQuery("body", "paple") { MaxEdits = 1, Transpositions = true })); // apple by a swap, maple by a substitution
            Assert.Equal(0, Total(new FuzzyQuery("body", "paple") { MaxEdits = 1, Transpositions = false, PrefixLength = 1 }));
            Assert.Equal(1, Total(new RegexQuery("body", "a.ple")));
            Assert.Equal(1, Total(new RegexQuery("body", "m.*")));
            Assert.Throws<QueryException>(() => Total(new RegexQuery("body", "(")));
            Assert.Throws<QueryException>(() => Total(new FuzzyQuery("body", "x") { MaxEdits = 3 }));
        }
    }

    [Fact]
    public void BooleanSemantics()
    {
        var (snap, searcher) = Build([("d0", "t", "a b c", "x", 0), ("d1", "t", "a b", "x", 1), ("d2", "t", "a", "x", 2), ("d3", "t", "c", "x", 3)]);
        using (snap)
        {
            static BooleanClause C(string term, Occur o) => new(new TermQuery("body", term), o);
            int[] Docs(BooleanQuery q) => [.. searcher.Search(q, 10, countExactly: true).Hits.Select(h => h.DocId).Order()];
            Assert.Equal([0, 1, 2, 3], Docs(BooleanQuery.Of(C("a", Occur.Should), C("c", Occur.Should))));
            Assert.Equal([0], Docs(BooleanQuery.Of(C("a", Occur.Should), C("b", Occur.Should), C("c", Occur.Should)) with { MinimumShouldMatch = 3 }));
            Assert.Equal([0, 1], Docs(BooleanQuery.Of(C("a", Occur.Must), C("b", Occur.Filter))));
            Assert.Equal([2], Docs(BooleanQuery.Of(C("a", Occur.Must), C("b", Occur.MustNot))));
            Assert.Equal([0, 1, 2], Docs(BooleanQuery.Of(C("a", Occur.Must), C("c", Occur.Should))));
            Assert.Equal([0], Docs(BooleanQuery.Of(C("a", Occur.Must), C("c", Occur.Should)) with { MinimumShouldMatch = 1 }));
            Assert.Empty(Docs(BooleanQuery.Of(C("a", Occur.MustNot))));
            Assert.Empty(Docs(BooleanQuery.Of()));
            // A must clause's score counts; a filter's does not.
            float must = searcher.Search(BooleanQuery.Of(C("b", Occur.Must)), 1).Hits[0].Score;
            float filter = searcher.Search(BooleanQuery.Of(C("b", Occur.Filter)), 1).Hits[0].Score;
            Assert.True(must > 0 && filter == 0);
        }
    }

    [Fact]
    public void TopKByBlockMaxWandEqualsExhaustiveAndSkipsDocuments()
    {
        // 6,000 documents over a Zipf-like vocabulary so that postings span many blocks and WAND has something to skip.
        var rng = new Random(11);
        var docs = new List<(string, string, string, string, long)>();
        for (int i = 0; i < 6000; i++)
        {
            int len = 5 + rng.Next(60);
            docs.Add(("d" + i, "t", string.Join(' ', Enumerable.Range(0, len).Select(_ => "w" + (int)(300 * Math.Pow(rng.NextDouble(), 3)))), "a", i));
        }

        var (snap, searcher) = Build(docs, bufferedDocs: 2000);
        using (snap)
        {
            foreach (string[] terms in new string[][] { ["w0"], ["w0", "w1"], ["w0", "w2", "w5", "w40", "w120"] })
            {
                Query q = terms.Length == 1 ? new TermQuery("body", terms[0]) : new BooleanQuery([.. terms.Select(t => new BooleanClause(new TermQuery("body", t), Occur.Should))]);
                TopDocs exact = searcher.Search(q, 10, countExactly: true);
                TopDocs pruned = searcher.Search(q, 10, countExactly: false);
                Assert.Equal(exact.Hits, pruned.Hits);
                Assert.True(pruned.TotalHits <= exact.TotalHits);
            }
        }
    }

    [Fact]
    public void ScoringAllocatesNothingPerDocument()
    {
        // The same query over 1,500 and over 12,000 matching documents: the allocation is the scorer setup, not a cost per document.
        long Measure(int documents)
        {
            var rng = new Random(5);
            var docs = Enumerable.Range(0, documents).Select(i => ("d" + i, "t", string.Join(' ', Enumerable.Range(0, 30).Select(_ => "w" + rng.Next(40))), "a", (long)i)).ToList();
            var (snap, searcher) = Build(docs, bufferedDocs: documents);
            using (snap)
            {
                var q = new BooleanQuery([new BooleanClause(new TermQuery("body", "w1"), Occur.Should), new BooleanClause(new TermQuery("body", "w2"), Occur.Should)]);
                for (int i = 0; i < 20; i++)
                {
                    searcher.Search(q, 10, countExactly: true);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                TopDocs t = searcher.Search(q, 10, countExactly: true);
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.True(t.TotalHits > documents / 2);
                return bytes;
            }
        }

        long small = Measure(1500);
        long large = Measure(12000);
        Assert.True(large - small < 2048, $"{small} bytes for 1,500 documents, {large} for 12,000");
    }
}
