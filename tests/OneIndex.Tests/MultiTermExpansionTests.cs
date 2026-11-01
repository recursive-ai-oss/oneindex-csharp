using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class MultiTermExpansionTests
{
    private static readonly Schema BodySchema = new SchemaBuilder().Id("id")
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase" })
        .Build();

    private static (IndexSnapshot Snapshot, IndexSearcher Searcher) Build(List<string> bodies, int bufferedDocs)
    {
        var storage = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(storage, BodySchema, new IndexWriterOptions { MaxBufferedDocuments = bufferedDocs, AutoMerge = false }))
        {
            for (int i = 0; i < bodies.Count; i++)
            {
                w.Upsert(new Document().Add("id", "d" + i).Add("body", bodies[i]));
            }

            w.Commit();
        }

        IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        return (snapshot, SnapshotSearch.CreateSearcher(snapshot));
    }

    [Fact]
    public void TheSinglePassMatchAgreesWithTheDefinition()
    {
        var r = new Random(19001);
        const string alphabet = "abcd";
        string Word() => new(Enumerable.Range(0, r.Next(1, 9)).Select(_ => alphabet[r.Next(alphabet.Length)]).ToArray());
        int matched = 0;
        for (int i = 0; i < 20_000; i++)
        {
            string q = Word();
            string w = Word();
            int budget = r.Next(3);
            bool prefix = r.Next(2) == 0;
            TypoMatch? fast = TypoRanking.Match(q, w, budget, prefix);
            TypoMatch? slow = TypoRanking.MatchByDefinition(q, w, budget, prefix);
            Assert.Equal(slow, fast);
            matched += fast is null ? 0 : 1;
        }

        Assert.True(matched > 1_000, $"only {matched} matches");
    }

    [Fact]
    public void ConstantScorePrefixMatchesTheSetOfDocumentsAcrossWindowsAndSegments()
    {
        // Long lists (more than 512 documents) are merged window by window, short ones through one bit set; the documents span
        // several windows of 4,096 and several segments.
        var r = new Random(19002);
        var bodies = new List<string>();
        var expected = new List<int>();
        for (int i = 0; i < 13_000; i++)
        {
            var terms = new List<string>();
            bool hit = false;
            if (r.Next(3) == 0)
            {
                terms.Add("pa"); // a long list
                hit = true;
            }

            if (r.Next(40) == 0)
            {
                terms.Add("pb" + r.Next(300)); // many short lists
                hit = true;
            }

            if (i is 12_500 or 7)
            {
                terms.Add("pz");
                hit = true;
            }

            terms.Add("other" + (i % 5));
            bodies.Add(string.Join(' ', terms));
            if (hit)
            {
                expected.Add(i);
            }
        }

        var (snap, searcher) = Build(bodies, 3_000);
        using (snap)
        {
            var q = new PrefixQuery("body", "p");
            TopDocs all = searcher.Search(q, expected.Count + 10, countExactly: true);
            Assert.Equal(expected.Count, all.TotalHits);
            Assert.Equal(expected, all.Hits.Select(h => h.DocId).Order().ToList());
            Assert.All(all.Hits, h => Assert.Equal(1f, h.Score));

            // Equal scores go to the lower ids, and the search may stop early.
            TopDocs top = searcher.Search(q, 5, countExactly: false);
            Assert.Equal(expected.Take(5), top.Hits.Select(h => h.DocId));
            Assert.Equal(expected.Count, searcher.Search(new PrefixQuery("body", "p") { Boost = 2f }, 1, countExactly: true).TotalHits);

            // Scoring the terms is the other mode: scores vary with the terms' idf and frequencies.
            TopDocs scored = searcher.Search(new PrefixQuery("body", "pb") { ScoreTerms = true }, 20, countExactly: true);
            Assert.Equal(all.Hits.Count(h => bodies[h.DocId].Contains("pb", StringComparison.Ordinal)), scored.TotalHits);
            Assert.All(scored.Hits, h => Assert.NotEqual(1f, h.Score));
        }
    }

    [Fact]
    public void FuzzyKeepsTheClosestTermsAndNeverFails()
    {
        var bodies = new List<string>();
        for (char c = 'a'; c <= 'z'; c++)
        {
            for (int n = 0; n < 1 + (c - 'a'); n++)
            {
                bodies.Add("word" + c); // one edit from "word", more documents for later letters
            }
        }

        bodies.Add("word");
        bodies.Add("wor"); // one edit as well
        var (snap, searcher) = Build(bodies, 100);
        using (snap)
        {
            TopDocs everything = searcher.Search(new FuzzyQuery("body", "word") { MaxEdits = 1, MaxExpansions = 100 }, 500, countExactly: true);
            Assert.Equal(bodies.Count, everything.TotalHits);

            // Three expansions: "word" itself (no edits) and the two terms with the most documents among the one-edit terms.
            TopDocs closest = searcher.Search(new FuzzyQuery("body", "word") { MaxEdits = 1, MaxExpansions = 3 }, 500, countExactly: true);
            int expected = bodies.Count(b => b is "word" or "wordz" or "wordy");
            Assert.Equal(expected, closest.TotalHits);
            Assert.Throws<QueryException>(() => searcher.Search(new FuzzyQuery("body", "word") { MaxExpansions = 0 }, 5));

            // Scoring is the scoring of the plain terms: an expansion that is the whole word scores like a term query.
            Explanation? e = searcher.Explain(new FuzzyQuery("body", "word") { MaxEdits = 1, MaxExpansions = 3 }, bodies.Count - 2);
            Assert.NotNull(e);
        }
    }
}
