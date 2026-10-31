using OneIndex.Analysis;
using OneIndex.Search;

namespace OneIndex.Tests;

/// <summary>A tiny reference evaluator of term, boolean, phrase (slop 0) and match-all queries over documents given as term lists.</summary>
internal static class MiniEval
{
    public static bool Matches(Query q, string[] doc)
    {
        switch (q)
        {
            case MatchAllQuery:
                return true;
            case TermQuery t:
                return Array.IndexOf(doc, t.Term) >= 0;
            case PhraseQuery p:
                if (p.Slop != 0)
                {
                    throw new NotSupportedException("the reference evaluator handles exact phrases only");
                }

                return Contains(doc, p.Terms);
            case BooleanQuery b:
                {
                    bool anyMust = false;
                    int should = 0;
                    int shouldHit = 0;
                    foreach (BooleanClause c in b.Clauses)
                    {
                        bool m = Matches(c.Query, doc);
                        switch (c.Occur)
                        {
                            case Occur.Must:
                            case Occur.Filter:
                                anyMust = true;
                                if (!m)
                                {
                                    return false;
                                }

                                break;
                            case Occur.MustNot:
                                if (m)
                                {
                                    return false;
                                }

                                break;
                            default:
                                should++;
                                if (m)
                                {
                                    shouldHit++;
                                }

                                break;
                        }
                    }

                    int need = b.MinimumShouldMatch > 0 ? b.MinimumShouldMatch : (anyMust ? 0 : 1);
                    return should == 0 ? anyMust || need == 0 : shouldHit >= need;
                }

            default:
                throw new NotSupportedException(q.GetType().Name);
        }
    }

    public static bool Contains(string[] doc, IReadOnlyList<string> phrase)
    {
        for (int i = 0; i + phrase.Count <= doc.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < phrase.Count && ok; j++)
            {
                ok = doc[i + j] == phrase[j];
            }

            if (ok)
            {
                return true;
            }
        }

        return false;
    }
}

public class SynonymTests
{
    private static readonly Analyzer Ws = new WhitespaceLowercaseAnalyzer();
    private static readonly string[] Cv = ["cv"];

    private static Query Words(SynonymRules rules, string text, string field = "f")
        => TokenGraphQueries.ToWords(TokenGraph.FromText(Ws, text, rules.Build(Ws)), field);

    private static Query Phrase(SynonymRules rules, string text, string field = "f")
        => TokenGraphQueries.ToPhrase(TokenGraph.FromText(Ws, text, rules.Build(Ws)), field);

    private static bool Hit(Query q, string doc) => MiniEval.Matches(q, doc.Split(' '));

    // Meilisearch documentation, "Synonyms": great <-> fantastic.
    [Fact]
    public void MutualPairFindsBothWords()
    {
        var rules = new SynonymRules().AddOneWay("great", "fantastic").AddOneWay("fantastic", "great");
        Query q = Words(rules, "great");
        Assert.True(Hit(q, "a fantastic film"));
        Assert.True(Hit(q, "a great film"));
        Assert.False(Hit(q, "a fine film"));
        Assert.True(Hit(Words(rules, "fantastic"), "a great film"));
    }

    // Documentation, "One-way association": phone => iphone.
    [Fact]
    public void OneWayRuleOnlyWorksForwards()
    {
        var rules = new SynonymRules().AddOneWay("phone", "iphone");
        Assert.True(Hit(Words(rules, "phone"), "an iphone case"));
        Assert.False(Hit(Words(rules, "iphone"), "a phone case"));
    }

    // Documentation, "Mutual association": shoe <=> boot <=> slipper <=> sneakers.
    [Fact]
    public void GroupRuleIsMultiWay()
    {
        var rules = new SynonymRules().AddGroup("shoe", "boot", "slipper", "sneakers");
        foreach (string a in new[] { "shoe", "boot", "slipper", "sneakers" })
        {
            foreach (string b in new[] { "shoe", "boot", "slipper", "sneakers" })
            {
                Assert.True(Hit(Words(rules, a), $"red {b} sale"), $"{a} -> {b}");
            }
        }
    }

    [Fact]
    public void RulesAreNotTransitive()
    {
        var rules = new SynonymRules().AddOneWay("a", "b").AddOneWay("b", "c");
        Query q = Words(rules, "a");
        Assert.True(Hit(q, "x b y"));
        Assert.False(Hit(q, "x c y"));
    }

    // Documentation, "Multi-word synonyms": san francisco <-> sf, treated as phrases.
    [Fact]
    public void MultiWordSynonymsAreAPhrase()
    {
        var rules = new SynonymRules().AddOneWay("san francisco", "sf").AddOneWay("sf", "san francisco");
        Query sf = Words(rules, "sf");
        Assert.True(Hit(sf, "visit san francisco today"));
        Assert.False(Hit(sf, "san jose and francisco"), "the two words must be adjacent");
        Assert.False(Hit(sf, "francisco san"));
        Query full = Words(rules, "san francisco");
        Assert.True(Hit(full, "the sf bay"));
        Assert.True(Hit(full, "francisco is near san"), "the original words need not be adjacent in a bag-of-words query");
    }

    // Documentation, "Synonym term length limitation": only runs of 1 to 3 query words are looked up.
    [Fact]
    public void QueryRunsLongerThanThreeWordsGetNoSynonyms()
    {
        var rules = new SynonymRules().AddOneWay("lotr", "lord of the rings").AddOneWay("lord of the rings", "lotr");
        Assert.True(Hit(Words(rules, "lotr"), "the lord of the rings trilogy"));
        Query q = Words(rules, "lord of the rings");
        Assert.False(Hit(q, "lotr"), "four words are not looked up");
        Assert.Empty(rules.Build(Ws).GetSynonyms("lord", "of", "the", "rings"));
    }

    // Documentation, "Maximum number of synonyms per term": 50 synonyms, and 100 words together.
    [Fact]
    public void PerTermLimitsDropTheRest()
    {
        var rules = new SynonymRules();
        string[] many = [.. Enumerable.Range(0, 60).Select(i => "s" + i)];
        rules.AddOneWay("book", many);
        SynonymMap map = rules.Build(Ws);
        Assert.Equal(50, map.GetSynonyms("book").Count);
        Assert.Equal(10, map.DroppedSynonyms);
        Assert.Equal("s49", map.GetSynonyms("book")[49][0]);

        var words = new SynonymRules().AddOneWay("computer", [.. Enumerable.Range(0, 40).Select(i => $"a{i} b{i} c{i}")]);
        SynonymMap wordMap = words.Build(Ws);
        Assert.Equal(33, wordMap.GetSynonyms("computer").Count); // 33 x 3 = 99 words; the 34th would make 102
    }

    // Documentation, "Normalization": synonyms are processed with the index's settings (stop words included).
    [Fact]
    public void SynonymsAreNormalizedWithTheAnalyzer()
    {
        Analyzer en = AnalyzerRegistry.Create("en");
        SynonymMap map = new SynonymRules().AddGroup("Résumé", "CV").Build(en);
        TokenGraph g = TokenGraph.FromText(en, "cv", map);
        string[] expected = [.. en.Analyze("Résumé").Select(t => t.Term)];
        Assert.Contains(g.Edges, e => e.IsSynonym && e.Terms.SequenceEqual(expected));
        TokenGraph back = TokenGraph.FromText(en, "Résumé", map);
        Assert.Contains(back.Edges, e => e.IsSynonym && e.Terms.SequenceEqual(Cv));

        // A synonym of nothing but stop words vanishes.
        SynonymMap gone = new SynonymRules().AddOneWay("movie", "the", "film").Build(en);
        Assert.Single(gone.GetSynonyms("movi"));
    }

    // Documentation, "Relevancy": the exact query takes precedence over its synonyms.
    [Fact]
    public void SynonymReadingsScoreLowerThanTheOriginal()
    {
        var rules = new SynonymRules().AddOneWay("spirit", "ghost");
        var q = Assert.IsType<BooleanQuery>(Words(rules, "spirit"));
        Assert.Equal(1f, Assert.IsType<TermQuery>(q.Clauses[0].Query).Boost);
        Assert.Equal("ghost", Assert.IsType<TermQuery>(q.Clauses[1].Query).Term);
        Assert.True(q.Clauses[1].Query.Boost < 1f);
        Assert.All(q.Clauses, c => Assert.Equal(Occur.Should, c.Occur));
    }

    [Fact]
    public void OverlappingRulesGiveEveryReading()
    {
        var rules = new SynonymRules().AddOneWay("a b", "x").AddOneWay("b c", "y").AddOneWay("a b c", "z");
        TokenGraph g = TokenGraph.FromText(Ws, "a b c", rules.Build(Ws));
        var readings = g.Paths().Select(p => string.Join(' ', p.SelectMany(e => e.Terms))).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(["a b c", "a y", "x c", "z"], readings);
        Assert.Equal([0, 3], g.CutNodes());
    }

    [Fact]
    public void ExpansionIsBounded()
    {
        // Ten words, each with three synonyms: 4^10 readings without a cap.
        var rules = new SynonymRules();
        for (int i = 0; i < 10; i++)
        {
            rules.AddOneWay("w" + i, "p" + i, "q" + i, "r" + i);
        }

        string text = string.Join(' ', Enumerable.Range(0, 10).Select(i => "w" + i));
        TokenGraph g = TokenGraph.FromText(Ws, text, rules.Build(Ws));
        Assert.True(g.IsTruncated);
        Assert.InRange(g.Paths().Count, 1, 64);
        Assert.Equal(text, string.Join(' ', g.Paths()[0].SelectMany(e => e.Terms)));

        // The bag-of-words query does not multiply: a clause per stretch, not per reading.
        var q = Assert.IsType<BooleanQuery>(TokenGraphQueries.ToWords(TokenGraph.FromText(Ws, text, rules.Build(Ws)), "f"));
        Assert.True(q.Clauses.Count <= 10);
    }

    [Fact]
    public void EmptyQueryMatchesAll()
        => Assert.IsType<MatchAllQuery>(Words(new SynonymRules(), "   "));

    // Seeded random rules, documents and queries against an independent recursive reading enumerator.
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RandomRulesAgreeWithBruteForce(int seed)
    {
        var rng = new Random(seed);
        string[] vocab = ["a", "b", "c", "d", "e", "f"];
        string RandomPhrase(int max) => string.Join(' ', Enumerable.Range(0, 1 + rng.Next(max)).Select(_ => vocab[rng.Next(vocab.Length)]));
        int checkedQueries = 0;
        for (int round = 0; round < 60; round++)
        {
            var rules = new SynonymRules();
            var reference = new Dictionary<string, List<string[]>>();
            int ruleCount = rng.Next(1, 6);
            for (int r = 0; r < ruleCount; r++)
            {
                string key = RandomPhrase(3);
                string[] alts = [.. Enumerable.Range(0, 1 + rng.Next(3)).Select(_ => RandomPhrase(3))];
                if (rng.Next(3) == 0)
                {
                    rules.AddGroup([key, .. alts]);
                    string[] all = [key, .. alts];
                    for (int i = 0; i < all.Length; i++)
                    {
                        AddRef(reference, all[i], all.Where((_, j) => j != i));
                    }
                }
                else
                {
                    rules.AddOneWay(key, alts);
                    AddRef(reference, key, alts);
                }
            }

            SynonymMap map = rules.Build(Ws, new SynonymLimits { MaxPaths = 100_000 });
            string text = RandomPhrase(5);
            TokenGraph g = TokenGraph.FromText(Ws, text, map);
            Assert.False(g.IsTruncated);
            Query words = TokenGraphQueries.ToWords(g, "f", 1f);
            Query phrase = TokenGraphQueries.ToPhrase(g, "f", 0, 1f);
            string[] qw = text.Split(' ');
            for (int d = 0; d < 40; d++)
            {
                string[] doc = [.. Enumerable.Range(0, rng.Next(0, 9)).Select(_ => vocab[rng.Next(vocab.Length)])];
                Assert.Equal(RefWords(reference, qw, 0, doc), MiniEval.Matches(words, doc));
                Assert.Equal(RefPhrase(reference, qw, 0, [], doc), MiniEval.Matches(phrase, doc));
                checkedQueries++;
            }
        }

        Assert.Equal(2400, checkedQueries);
    }

    private static void AddRef(Dictionary<string, List<string[]>> reference, string key, IEnumerable<string> alts)
    {
        if (!reference.TryGetValue(key, out List<string[]>? list))
        {
            reference[key] = list = [];
        }

        list.AddRange(alts.Select(a => a.Split(' ')));
    }

    // Reference: does the document match the query words qw[i..] when each span may be replaced by a synonym? In a bag-of-words
    // query the original words match anywhere and a synonym matches as a phrase.
    private static bool RefWords(Dictionary<string, List<string[]>> rules, string[] qw, int i, string[] doc)
    {
        if (i == qw.Length)
        {
            return true;
        }

        if (Array.IndexOf(doc, qw[i]) >= 0 && RefWords(rules, qw, i + 1, doc))
        {
            return true;
        }

        for (int len = 1; len <= 3 && i + len <= qw.Length; len++)
        {
            if (rules.TryGetValue(string.Join(' ', qw[i..(i + len)]), out List<string[]>? alts))
            {
                foreach (string[] alt in alts)
                {
                    if (MiniEval.Contains(doc, alt) && RefWords(rules, qw, i + len, doc))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static bool RefPhrase(Dictionary<string, List<string[]>> rules, string[] qw, int i, List<string> sofar, string[] doc)
    {
        if (i == qw.Length)
        {
            return MiniEval.Contains(doc, sofar);
        }

        if (RefPhrase(rules, qw, i + 1, [.. sofar, qw[i]], doc))
        {
            return true;
        }

        for (int len = 1; len <= 3 && i + len <= qw.Length; len++)
        {
            if (rules.TryGetValue(string.Join(' ', qw[i..(i + len)]), out List<string[]>? alts))
            {
                foreach (string[] alt in alts)
                {
                    if (RefPhrase(rules, qw, i + len, [.. sofar, .. alt], doc))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
