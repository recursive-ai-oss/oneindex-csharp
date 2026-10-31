using OneIndex.Highlighting;
using OneIndex.Index;
using OneIndex.Queries;

namespace OneIndex.Tests;

public class Article
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 3)]
    public string Title { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Body { get; set; } = string.Empty;
}

public class Note
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Analyzer = "cjk")]
    public string Text { get; set; } = string.Empty;
}

public class HappyPathTests : IDisposable
{
    private static readonly DateTime Cutoff = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (string d in _directories)
        {
            try
            {
                Directory.Delete(d, true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static Invoice Inv(string id, string title, string body, DateTime modified, params string[] tags)
        => new() { Id = id, Title = title, Body = body, Modified = modified, Tags = tags };

    private static SearchIndex<Invoice> SampleIndex()
    {
        SearchIndex<Invoice> index = SearchIndex.InMemory<Invoice>();
        index.Upsert(Inv("inv-1", "Overdue notice", "Payment from Acme Corp is overdue since March.", new DateTime(2024, 3, 1, 0, 0, 0, DateTimeKind.Utc), "invoice", "overdue"));
        index.Upsert(Inv("inv-2", "Overdue draft", "Draft of the acme corp reminder, overdue.", new DateTime(2024, 3, 2, 0, 0, 0, DateTimeKind.Utc), "invoice", "draft"));
        index.Upsert(Inv("inv-3", "Old overdue notice", "Acme Corp, overdue in 2022.", new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Utc), "invoice"));
        index.Upsert(Inv("inv-4", "Receipt", "Acme paid; a corp-level overdue receipt.", new DateTime(2024, 4, 1, 0, 0, 0, DateTimeKind.Utc), "receipt"));
        index.Commit();
        return index;
    }

    [Fact]
    public void TheSampleFromTheDesignWorksAsWritten()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        var r = index.Search("overdue \"acme corp\" -draft")
            .Where(x => x.Tags.Contains("invoice") && x.Modified > Cutoff)
            .Take(10);
        List<string> ids = [];
        foreach (var hit in r)
        {
            _ = hit.Document;
            Assert.True(hit.Score > 0);
            _ = hit.Highlights;
            ids.Add(hit.Id);
        }

        Assert.Equal(["inv-1"], ids);
    }

    [Fact]
    public void HitsReturnTheStoredDocument()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        index.Upsert(new Invoice { Id = "n", Title = "Stored fields", Note = "kept", Modified = Cutoff, Tags = ["x"] });
        index.Commit();
        Invoice doc = index.Search("stored").Single().Document;
        Assert.Equal("kept", doc.Note);
        Assert.Equal(Cutoff, doc.Modified);
        Assert.Equal(["x"], doc.Tags);
    }

    [Fact]
    public void SearchesSeeTheLastCommitOnly()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        index.Upsert(Inv("inv-9", "Brand new", "zebra", Cutoff));
        Assert.Empty(index.Search("zebra"));
        index.Commit();
        Assert.Single(index.Search("zebra"));
        index.Delete("inv-9");
        Assert.Single(index.Search("zebra"));
        index.Commit();
        Assert.Empty(index.Search("zebra"));
        Assert.Equal(4, index.Count);
    }

    [Fact]
    public void UpsertReplacesAndDeleteReportsWhetherItDeleted()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        index.Upsert(Inv("inv-1", "Changed", "completely different words", Cutoff));
        index.Commit();
        Assert.Empty(index.Search("payment"));
        Assert.Equal("Changed", index.Search("different").Single().Document.Title);
        Assert.True(index.Delete("inv-1"));
        Assert.False(index.Delete("never-there"));
    }

    [Fact]
    public void ABlankSearchMatchesEverythingAndFiltersApplyToIt()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        Assert.Equal(4, index.Search().Count());
        Assert.Equal(4, index.Search("   ").Count());
        Assert.Equal(["inv-1", "inv-2", "inv-4"], index.Search().Where(x => x.Modified > Cutoff).Take(10).Select(h => h.Id).Order().ToArray());
    }

    [Fact]
    public void TakeSkipAndTheTotalsBehave()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        List<string> all = index.Search("overdue").Take(10).Select(h => h.Id).ToList();
        Assert.Equal(4, all.Count);
        Assert.Equal(all.Skip(1).Take(2), index.Search("overdue").Skip(1).Take(2).Select(h => h.Id));
        Assert.Empty(index.Search("overdue").Take(0));
        SearchResults<Invoice> exact = index.Search("overdue").Take(1).CountExactly().Execute();
        Assert.Single(exact);
        Assert.Equal(4, exact.TotalHits);
        Assert.True(exact.TotalHitsExact);
        Assert.Equal(4, index.Search("overdue").Take(1).CountExactly().Count());
        Assert.Equal(4, index.Search("overdue").Take(0).Execute().TotalHits);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search().Take(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search().Skip(10_001));
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search().Skip(9_000).Take(2_000));
    }

    [Fact]
    public void ABoostedTitleOutranksABodyMention()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "body", Title = "Weather", Body = "A long text that mentions the quokka once." });
        index.Upsert(new Article { Id = "title", Title = "Quokka", Body = "A long text that is about something quite different." });
        index.Commit();
        Assert.Equal(["title", "body"], index.Search("quokka").Select(h => h.Id).ToArray());
    }

    [Fact]
    public async Task TheAsyncTwinsMatchTheSyncOnes()
    {
        await using SearchIndex<Invoice> index = SearchIndex.InMemory<Invoice>();
        await index.UpsertAsync(Inv("a", "Alpha", "first", Cutoff, "t"));
        await index.UpsertAsync(Inv("b", "Beta", "second", Cutoff, "t"));
        Assert.Equal(1, await index.CommitAsync());
        Assert.Equal(["a"], (await index.Search("alpha").ExecuteAsync()).Select(h => h.Id).ToArray());
        Assert.True(await index.DeleteAsync("a"));
        await index.CommitAsync();
        Assert.Empty(await index.Search("alpha").ExecuteAsync());
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await index.Search("beta").ExecuteAsync(new CancellationToken(true)).AsTask());
    }

    [Fact]
    public void DisposalIsCleanAndFinal()
    {
        SearchIndex<Invoice> index = SampleIndex();
        var pending = index.Search("overdue");
        index.Dispose();
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.Search("x").Execute());
        Assert.Throws<ObjectDisposedException>(() => index.Upsert(new Invoice { Id = "z" }));
        Assert.Throws<ObjectDisposedException>(() => pending.Execute());
    }

    [Fact]
    public void SearchesRunWhileCommitsHappen()
    {
        using SearchIndex<Invoice> index = SampleIndex();
        var stop = new CancellationTokenSource();
        Exception? failure = null;
        Task reader = Task.Run(() =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    Assert.True(index.Search("overdue").Take(100).Count() >= 3);
                }
            }
            catch (Exception e)
            {
                failure = e;
            }
        });
        for (int i = 0; i < 40; i++)
        {
            index.Upsert(Inv("extra-" + i, "More", "overdue " + i, Cutoff));
            index.Commit();
        }

        stop.Cancel();
        reader.Wait();
        Assert.Null(failure);
        Assert.Equal(44, index.Search("overdue").CountExactly().Count());
    }

    [Fact]
    public void ADirectoryIndexSurvivesBeingClosedAndReopened()
    {
        Assert.SkipUnless(new IndexWriterOptions().Codecs.Any(c => c.Parts.Contains(SegmentParts.Stored)), "needs the stored source codec");
        string dir = TestHelpers.NewTempDirectory();
        _directories.Add(dir);
        using (SearchIndex<Invoice> index = SearchIndex.Open<Invoice>(dir))
        {
            index.Upsert(Inv("p1", "Persisted", "stays after reopening", Cutoff, "k"));
            index.Commit();
            index.Upsert(Inv("p2", "Lost", "never committed", Cutoff));
        }

        using SearchIndex<Invoice> reopened = SearchIndex.Open<Invoice>(dir);
        Assert.Equal(["p1"], reopened.Search().Select(h => h.Id).ToArray());
        Assert.Equal("Persisted", reopened.Search("reopening").Single().Document.Title);
    }

    [Fact]
    public void ADirectoryIndexCommitsAndSearches()
    {
        string dir = TestHelpers.NewTempDirectory();
        _directories.Add(dir);
        using SearchIndex<Invoice> index = SearchIndex.Open<Invoice>(dir);
        index.Upsert(Inv("p1", "Persisted", "in a directory", Cutoff));
        index.Commit();
        Assert.Equal(["p1"], index.Search("directory").Select(h => h.Id).ToArray());
        Assert.NotEmpty(Directory.GetFiles(dir));
    }

    // ---- highlighting ----

    private static string Marked(Fragment f) => f.Marked("[", "]");

    [Fact]
    public void APhraseIsHighlightedOnlyWhereItsWordsAreAdjacent()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = "t", Body = "Acme makes things. The corp of acme corp is large and the corp agrees with acme." });
        index.Commit();
        SearchHit<Article> hit = index.Search("\"acme corp\"").Single();
        Fragment f = Assert.Single(hit.Highlights["Body"]);
        Assert.Equal("Acme makes things. The corp of [acme corp] is large and the corp agrees with acme.", Marked(f));
    }

    [Fact]
    public void StemmedFormsAreHighlightedInTheirOriginalSpelling()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = "t", Body = "The runners were running quickly; she runs daily." });
        index.Commit();
        SearchHit<Article> hit = index.Search("run").Single();
        Assert.Equal("The runners were [running] quickly; she [runs] daily.", Marked(hit.Highlights["Body"][0]));
    }

    [Fact]
    public void CjkBigramsAreHighlightedAsOneRun()
    {
        using SearchIndex<Note> index = SearchIndex.InMemory<Note>();
        index.Upsert(new Note { Id = "1", Text = "我住在东京都新宿区" });
        index.Commit();
        SearchHit<Note> hit = index.Search("东京都").Single();
        Assert.Equal("我住在[东京都]新宿区", Marked(hit.Highlights["Text"][0]));
    }

    [Fact]
    public void OffsetsStayOnCharacterBoundariesWithSurrogatePairsAndCombiningMarks()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        string text = "😀😀 emoji before. Cafe\u0301 au lait, then 𠮷野家 and a quokka 😀 at the end";
        index.Upsert(new Article { Id = "1", Title = "t", Body = text });
        index.Commit();
        SearchHit<Article> hit = index.Search("quokka").Highlight(new HighlightOptions { FragmentLength = 12 }).Single();
        Fragment f = Assert.Single(hit.Highlights["Body"]);
        Assert.Equal("quokka", f.Text.Substring(f.Matches[0].Start, f.Matches[0].Length));
        Assert.Equal(text.Substring(f.Offset, f.Text.Length), f.Text);
        Assert.All(f.Text.Select((c, i) => (c, i)), p =>
        {
            if (char.IsHighSurrogate(p.c))
            {
                Assert.True(p.i + 1 < f.Text.Length && char.IsLowSurrogate(f.Text[p.i + 1]));
            }

            if (char.IsLowSurrogate(p.c))
            {
                Assert.True(p.i > 0 && char.IsHighSurrogate(f.Text[p.i - 1]));
            }
        });
    }

    [Fact]
    public void LongTextGivesFragmentsOfTheRequestedSizeAndCount()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        string filler = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 40));
        index.Upsert(new Article { Id = "1", Title = "t", Body = $"{filler} needle one. {filler} needle two. {filler} needle three. {filler} needle four." });
        index.Commit();
        SearchHit<Article> defaults = index.Search("needle").Single();
        IReadOnlyList<Fragment> fragments = defaults.Highlights["Body"];
        Assert.Equal(3, fragments.Count);
        Assert.All(fragments, f =>
        {
            Assert.Contains("[needle]", Marked(f));
            Assert.InRange(f.Text.Length, 1, 200);
        });
        Assert.Equal(2, index.Search("needle").Highlight(new HighlightOptions { MaxFragments = 2, FragmentLength = 40 }).Single().Highlights["Body"].Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => index.Search("needle").Highlight(new HighlightOptions { MaxFragments = 0 }).Single().Highlights.Keys.ToArray());
    }

    [Fact]
    public void ExcludedAndFilterOnlyTermsAreNotHighlighted()
    {
        using SearchIndex<Article> index = SearchIndex.InMemory<Article>();
        index.Upsert(new Article { Id = "1", Title = "t", Body = "good words but not the odd ones" });
        index.Upsert(new Article { Id = "2", Title = "t", Body = "good words and a bad apple" });
        index.Commit();
        SearchHit<Article> hit = index.Search("good -bad").Single();
        Assert.Equal("[good] words but not the odd ones", Marked(hit.Highlights["Body"][0]));
        Assert.Empty(index.Search().First().Highlights);
    }

    [Fact]
    public void FragmentsMarkWithCustomTags()
    {
        var f = Highlighter.Highlight(
            SearchMappings.Require<Article>().Schema,
            SearchMappings.Require<Article>().ToDocument(new Article { Id = "1", Body = "alpha beta alpha" }),
            HighlightTerms.From(new UserTextCompiler(SearchMappings.Require<Article>().Schema).Compile("alpha")));
        Assert.Equal("<b>alpha</b> beta <b>alpha</b>", f["Body"][0].Marked("<b>", "</b>"));
        Assert.Equal("<em>alpha</em> beta <em>alpha</em>", f["Body"][0].ToString());
    }
}
