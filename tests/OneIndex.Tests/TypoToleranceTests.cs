using OneIndex.Index;
using OneIndex.Queries;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

[TypoTolerance(Enabled = false)]
public class NoTypoDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;
}

[TypoTolerance(MinWordSizeForOneTypo = 3, MinWordSizeForTwoTypos = 6, DisableOnWords = ["shrek"], DisableOnFields = ["Code"])]
[Synonyms("tv", "television")]
[OneWaySynonyms("laptop", "notebook")]
[StopWords("the", "of")]
public class ConfiguredDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Code { get; set; } = string.Empty;
}

[PrefixSearch(false)]
public class NoPrefixDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;
}

public class PlainDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;
}

public sealed class TypoToleranceTests
{
    // An independent formulation of the optimal string alignment distance: recursion over suffixes with memoization (the production
    // code fills a table over prefixes).
    private static int Osa(int[] a, int[] b)
    {
        var memo = new Dictionary<(int, int), int>();
        int Go(int i, int j)
        {
            if (i == a.Length)
            {
                return b.Length - j;
            }

            if (j == b.Length)
            {
                return a.Length - i;
            }

            if (memo.TryGetValue((i, j), out int v))
            {
                return v;
            }

            int best = Math.Min(Math.Min(Go(i + 1, j) + 1, Go(i, j + 1) + 1), Go(i + 1, j + 1) + (a[i] == b[j] ? 0 : 1));
            if (i + 1 < a.Length && j + 1 < b.Length && a[i] == b[j + 1] && a[i + 1] == b[j])
            {
                best = Math.Min(best, Go(i + 2, j + 2) + 1);
            }

            return memo[(i, j)] = best;
        }

        return Go(0, 0);
    }

    private static readonly bool[] Both = [false, true];

    private static string RandomWord(Random r, int alphabet, int min, int max)
    {
        int n = r.Next(min, max + 1);
        var c = new char[n];
        for (int i = 0; i < n; i++)
        {
            c[i] = (char)('a' + r.Next(alphabet));
        }

        return new string(c);
    }

    [Fact]
    public void TheCostIsTheAlignmentDistancePlusOneForADifferentFirstCharacter()
    {
        var r = new Random(7);
        for (int i = 0; i < 3000; i++)
        {
            string q = RandomWord(r, 3, 1, 8);
            string w = RandomWord(r, 3, 1, 8);
            int[] qa = q.Select(static c => (int)c).ToArray();
            int[] wa = w.Select(static c => (int)c).ToArray();
            Assert.Equal(Osa(qa, wa) + (q[0] != w[0] ? 1 : 0), TypoRanking.Typos(q, w));
            int best = int.MaxValue;
            for (int n = 1; n <= w.Length; n++)
            {
                best = Math.Min(best, Osa(qa, wa[..n]) + (q[0] != w[0] ? 1 : 0));
            }

            Assert.Equal(best, TypoRanking.PrefixTypos(q, w).Typos);
        }
    }

    [Theory]
    [InlineData("saturday", "saturday", 1, true)]
    [InlineData("saturday", "satuday", 1, true)]
    [InlineData("saturday", "sutuday", 1, false)] // two typos
    [InlineData("saturday", "caturday", 1, false)] // a typo on the first letter counts as two
    [InlineData("saturday", "caturday", 2, true)]
    [InlineData("seven", "sevem", 1, true)]
    [InlineData("phnoe", "phone", 1, true)] // a swap is one typo
    [InlineData("beautiful", "biutiful", 2, true)]
    [InlineData("xeautiful", "beautiful", 2, true)]
    [InlineData("tow", "two", 0, false)]
    public void TheDocumentedExamplesHold(string query, string word, int budget, bool matches)
        => Assert.Equal(matches, TypoRanking.Match(query, word, budget, prefix: false) is not null);

    private static (SearchIndexForTest Index, string[] Words) BuildVocabulary(int seed, int words, int alphabet)
    {
        var r = new Random(seed);
        var set = new SortedSet<string>(StringComparer.Ordinal);
        while (set.Count < words)
        {
            set.Add(RandomWord(r, alphabet, 2, 9));
        }

        string[] vocabulary = [.. set];
        return (new SearchIndexForTest(vocabulary, new Random(seed + 1).Next()), vocabulary);
    }

    private sealed class SearchIndexForTest : IDisposable
    {
        private readonly IndexSnapshot _snapshot;

        public SearchIndexForTest(string[] words, int unused)
        {
            _ = unused;
            Schema schema = new SchemaBuilder().Id("id").Add(new FieldDefinition("w", FieldType.Text) { Analyzer = "whitespace-lowercase" }).Build();
            var storage = new MemoryStorage();
            using (IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 97, AutoMerge = false }))
            {
                for (int i = 0; i < words.Length; i++)
                {
                    writer.Upsert(new Document().Add("id", "d" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).Add("w", words[i]));
                }

                writer.Commit();
            }

            _snapshot = IndexSnapshot.Open(storage);
            Searcher = SnapshotSearch.CreateSearcher(_snapshot, new QueryEngineOptions { MaxExpandedTerms = 100_000 });
        }

        public IndexSearcher Searcher { get; }

        public void Dispose() => _snapshot.Dispose();
    }

    // The automaton-driven matcher against a naive scan of every indexed word with the reference cost: every difference would fail the test.
    [Fact]
    public void AutomatonMatchingAgreesWithANaiveScanOfTheDictionary()
    {
        int vocabularies = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_TYPO_CASES"), out int n) ? n : 12;
        int wordsPer = int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_TYPO_WORDS"), out int wp) ? wp : 400;
        long cases = 0;
        long matched = 0;
        long typoMatches = 0;
        for (int seed = 0; seed < vocabularies; seed++)
        {
            (SearchIndexForTest index, string[] words) = BuildVocabulary(seed, wordsPer, 2 + (seed % 3));
            using (index)
            {
                var r = new Random(1000 + seed);
                for (int qi = 0; qi < 40; qi++)
                {
                    string q = qi % 2 == 0 ? words[r.Next(words.Length)] : RandomWord(r, 2 + (seed % 3), 1, 9);
                    if (qi % 4 == 0 && q.Length > 2)
                    {
                        q = q.Remove(r.Next(q.Length), 1); // a deletion typo
                    }

                    foreach (bool prefix in Both)
                    {
                        for (int budget = 0; budget <= 2; budget++)
                        {
                            TopDocs top = index.Searcher.Search(new TypoTermQuery(["w"], q) { MaxTypos = budget, Prefix = prefix }, words.Length + 1);
                            var actual = top.Hits.Select(static h => h.DocId).Order().ToArray();
                            int[] expected = Enumerable.Range(0, words.Length).Where(i => TypoRanking.Match(q, words[i], budget, prefix) is not null).ToArray();
                            Assert.True(expected.SequenceEqual(actual), $"seed {seed} query '{q}' budget {budget} prefix {prefix}: expected {expected.Length} words, got {actual.Length}");
                            cases++;
                            matched += actual.Length;
                            typoMatches += expected.Count(i => TypoRanking.Match(q, words[i], budget, prefix) is { Typos: > 0 });
                        }
                    }
                }
            }
        }

        Assert.True(matched > 1000 && typoMatches > 100, $"only {matched} matches and {typoMatches} with typos over {cases} cases");
    }

    [Fact]
    public void FewerTyposAndWholeWordsRankHigher()
    {
        string[] words = ["planet", "plante", "plnaet", "planets", "plan"];
        using var index = new SearchIndexForTest(words, 0);
        TopDocs top = index.Searcher.Search(new TypoTermQuery(["w"], "planet") { MaxTypos = 1, Prefix = true }, 10);
        // planet (exact) > plnaet / plante (one typo, same word length) ; planets is a longer word (exactness factor 0.8)
        Assert.Equal(0, top.Hits[0].DocId);
        Assert.Equal([0, 1, 2, 3], top.Hits.Select(static h => h.DocId).Order());
        int rankExact = top.Hits.ToList().FindIndex(static h => h.DocId == 0);
        int rankPrefix = top.Hits.ToList().FindIndex(static h => h.DocId == 3);
        int rankTypo = top.Hits.ToList().FindIndex(static h => h.DocId == 1);
        Assert.True(rankExact < rankPrefix && rankPrefix < rankTypo, "exact, then a longer word that begins like the query, then a word with a typo");
    }

    [Fact]
    public void ExplainNamesTheTypoAndExactnessFactors()
    {
        using var index = new SearchIndexForTest(["planet", "plnaet"], 0);
        var q = new TypoTermQuery(["w"], "planet") { MaxTypos = 1 };
        string text = index.Searcher.Explain(q, 1)!.ToString();
        Assert.Contains("typo factor 0.5", text);
        Assert.Contains("exactness factor 1", text);
        Assert.Contains("1 typos", text);
    }

    [Fact]
    public void TheTypeSettingsRoundTripThroughTheSchemaAndDefaultsKeepVersionOneBytes()
    {
        Schema plain = new SchemaBuilder().Id("id").Text("t").Build();
        Assert.Equal(1u, BitConverter.ToUInt32(plain.Serialize().AsSpan(4, 4)));
        var settings = new TextSettings
        {
            Typo = new TypoSettings { Enabled = false, MinWordSizeForOneTypo = 3, MinWordSizeForTwoTypos = 7, DisableOnNumbers = true, DisableOnWords = ["Shrek"], DisableOnFields = ["t"] },
            PrefixSearch = false,
            Synonyms = [new SynonymRule(["a", "b"], []), new SynonymRule(["c"], ["d", "e"])],
            StopWords = ["the"],
        };
        Schema configured = new SchemaBuilder { Settings = settings }.Id("id").Text("t").Build();
        Schema back = Schema.Deserialize(configured.Serialize());
        Assert.Equal(configured, back);
        Assert.False(back.Settings.Typo.Enabled);
        Assert.Equal(7, back.Settings.Typo.MinWordSizeForTwoTypos);
        Assert.Equal("Shrek", back.Settings.Typo.DisableOnWords[0]);
        Assert.False(back.Settings.PrefixSearch);
        Assert.Equal(2, back.Settings.Synonyms.Count);
        Assert.Equal(["d", "e"], back.Settings.Synonyms[1].To);
        Assert.True(plain.HasSameFields(configured));
        Assert.False(plain.Equals(configured));
        Assert.Throws<ArgumentException>(() => new SchemaBuilder { Settings = new TextSettings { Typo = new TypoSettings { MinWordSizeForOneTypo = 9, MinWordSizeForTwoTypos = 5 } } }.Id("id").Build());
    }

    [Fact]
    public void ChangedSettingsApplyOnReopenWithoutARebuild()
    {
        var storage = new MemoryStorage();
        Schema first = new SchemaBuilder().Id("id").Text("t").Build();
        using (IndexWriter w = IndexWriter.Open(storage, first))
        {
            w.Upsert(new Document().Add("id", "1").Add("t", "hello"));
            w.Commit();
        }

        Schema withSettings = new SchemaBuilder { Settings = new TextSettings { PrefixSearch = false } }.Id("id").Text("t").Build();
        using (IndexWriter w = IndexWriter.Open(storage, withSettings))
        {
            Assert.True(w.SchemaSettingsChanged);
            w.Upsert(new Document().Add("id", "2").Add("t", "world"));
            w.Commit();
        }

        using (IndexWriter w = IndexWriter.Open(storage, withSettings))
        {
            Assert.False(w.SchemaSettingsChanged);
        }

        using IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        Assert.False(snapshot.Schema.Settings.PrefixSearch);
        Assert.True(snapshot.Schema.HasSameFields(first)); // settings never change the fields, so nothing is rebuilt
    }

    [Fact]
    public void ASchemaWithAnUnknownSectionWritesWithoutItAndReadsBackToTheSame()
    {
        // Found by the commit fuzz target: sections this build does not define are skipped when read and dropped when written.
        string path = Path.Combine(Path.GetDirectoryName(ThisFile())!, "..", "..", "testdata", "fuzz", "commit-schema-skipped-section.bin");
        byte[] data = File.ReadAllBytes(path);
        Assert.True(Schema.TryDeserialize(data.AsSpan(1), IndexLimits.Default, out Schema? schema, out _)); // the first byte is the fuzz target's mode
        byte[] written = schema.Serialize();
        Assert.True(Schema.TryDeserialize(written, IndexLimits.Default, out Schema? again, out _));
        Assert.True(again.Serialize().AsSpan().SequenceEqual(written));
        Assert.False(written.AsSpan().SequenceEqual(data.AsSpan(1))); // the unknown sections are gone
    }

    private static string ThisFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;

    private static List<string> Titles<T>(SearchIndex<T> index, string text, Func<T, string> title)
        where T : class
        => index.Search(text).Take(20).ToList().Select(h => title(h.Document)).ToList();

    [Fact]
    public void UserTextToleratesTyposAndMatchesTheWordBeingTyped()
    {
        using SearchIndex<PlainDoc> index = SearchIndex.InMemory<PlainDoc>();
        foreach ((string id, string title) in new[] { ("1", "phone case"), ("2", "telephone book"), ("3", "phoneme chart"), ("4", "garden hose") })
        {
            index.Upsert(new PlainDoc { Id = id, Title = title });
        }

        index.Commit();
        Assert.Equal(["phone case"], Titles(index, "phnoe case", static d => d.Title)); // a swap is one typo
        Assert.Contains("phone case", Titles(index, "pho", static d => d.Title)); // the last word is a beginning
        Assert.DoesNotContain("garden hose", Titles(index, "pho", static d => d.Title));
        Assert.Empty(Titles(index, "pho ", static d => d.Title)); // a space ends the word: no beginnings, no typos at 3 characters
        Assert.Equal("phone case", Titles(index, "phone", static d => d.Title)[0]); // whole words before longer ones
        Assert.Empty(Titles(index, "xarden", static d => d.Title)); // a typo on the first letter counts as two
        Assert.Equal(["garden hose"], Titles(index, "gardan hose ", static d => d.Title));
    }

    [Fact]
    public void AttributesTurnTypoToleranceAndPrefixSearchOff()
    {
        using SearchIndex<NoTypoDoc> off = SearchIndex.InMemory<NoTypoDoc>();
        off.Upsert(new NoTypoDoc { Id = "1", Title = "telephone" });
        off.Commit();
        Assert.Empty(Titles(off, "telephon ", static d => d.Title)); // would be one typo
        Assert.Single(Titles(off, "telephon", static d => d.Title)); // still a beginning

        using SearchIndex<NoPrefixDoc> noPrefix = SearchIndex.InMemory<NoPrefixDoc>();
        noPrefix.Upsert(new NoPrefixDoc { Id = "1", Title = "telephone" });
        noPrefix.Commit();
        Assert.Empty(Titles(noPrefix, "tele", static d => d.Title));
        Assert.Single(Titles(noPrefix, "telephon", static d => d.Title)); // typos are still on
    }

    [Fact]
    public void ConfiguredThresholdsWordsFieldsSynonymsAndStopWordsApply()
    {
        using SearchIndex<ConfiguredDoc> index = SearchIndex.InMemory<ConfiguredDoc>();
        index.Upsert(new ConfiguredDoc { Id = "1", Title = "shrek forever", Code = "alpha" });
        index.Upsert(new ConfiguredDoc { Id = "2", Title = "television guide", Code = "beta" });
        index.Upsert(new ConfiguredDoc { Id = "3", Title = "notebook stand", Code = "gamma" });
        index.Upsert(new ConfiguredDoc { Id = "5", Title = "shreks", Code = "omega" });
        index.Upsert(new ConfiguredDoc { Id = "4", Title = "history of the world", Code = "delta" });
        index.Commit();
        Assert.Single(Titles(index, "forevar ", static d => d.Title)); // one typo from 3 characters
        Assert.Single(Titles(index, "shrex ", static d => d.Title)); // not listed: one typo is fine
        Assert.Single(Titles(index, "shrek ", static d => d.Title)); // listed: exact only, so "shreks" is not found
        Assert.Empty(Titles(index, "alpah ", static d => d.Title)); // typos are off in the Code field
        Assert.Single(Titles(index, "tv guide", static d => d.Title)); // synonyms: tv = television
        Assert.Equal(["notebook stand"], Titles(index, "laptop", static d => d.Title)); // one way: laptop also finds notebook
        Assert.DoesNotContain("notebook stand", Titles(index, "notebook xylophone", static d => d.Title));
        Assert.Equal(["history of the world"], Titles(index, "the history of world", static d => d.Title)); // stop words dropped
    }

    [Fact]
    public void TheRankingStaysStableWhileAWordIsTypedOut()
    {
        string[] others = ["elegant", "element", "elevator", "electric", "eleven", "apple", "orange", "banana", "grape", "lemon"];
        using SearchIndex<PlainDoc> index = SearchIndex.InMemory<PlainDoc>();
        for (int i = 0; i < others.Length; i++)
        {
            index.Upsert(new PlainDoc { Id = "o" + i, Title = others[i] });
        }

        index.Upsert(new PlainDoc { Id = "target", Title = "elephant" });
        index.Commit();
        const string word = "elephant";
        int previous = int.MaxValue;
        for (int k = 1; k <= word.Length; k++)
        {
            List<SearchHit<PlainDoc>> hits = index.Search(word[..k]).Take(20).ToList();
            int rank = hits.FindIndex(static h => h.Document.Id == "target");
            Assert.True(rank >= 0, $"'{word[..k]}' lost the target");
            Assert.True(rank <= previous, $"'{word[..k]}': rank {rank} is worse than {previous} one character earlier");
            if (k >= 4)
            {
                Assert.Equal(0, rank);
            }

            previous = rank;
        }
    }
}
