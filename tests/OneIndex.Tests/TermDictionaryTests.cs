using System.Text;
using System.Text.RegularExpressions;
using OneIndex.Codecs;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class TermDictionaryTests
{
    // Distance by dynamic programming over code points: Levenshtein, or optimal string alignment when swaps count.
    internal static int Distance(string a, string b, bool transpositions)
    {
        int[] x = [.. a.EnumerateRunes().Select(static r => r.Value)];
        int[] y = [.. b.EnumerateRunes().Select(static r => r.Value)];
        var d = new int[x.Length + 1, y.Length + 1];
        for (int i = 0; i <= x.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= y.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= x.Length; i++)
        {
            for (int j = 1; j <= y.Length; j++)
            {
                int cost = x[i - 1] == y[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (transpositions && i > 1 && j > 1 && x[i - 1] == y[j - 2] && x[i - 2] == y[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[x.Length, y.Length];
    }

    // The BCL regex works on UTF-16 units, ours on code points: map each supplementary code point to one private-use unit so that '.' means the same.
    private static string ForBcl(string s)
    {
        var sb = new StringBuilder();
        foreach (Rune r in s.EnumerateRunes())
        {
            sb.Append(r.IsBmp ? (char)r.Value : (char)(0xE000 + (r.Value & 0xFFF)));
        }

        return sb.ToString();
    }

    private static bool Accepts(TermAutomaton a, string term)
    {
        var buffers = new[] { new int[a.MaxStateLength], new int[a.MaxStateLength] };
        int len = a.Start(buffers[0]);
        int cur = 0;
        foreach (Rune r in term.EnumerateRunes())
        {
            if (len == 0)
            {
                return false;
            }

            int n = a.Transition(buffers[cur].AsSpan(0, len), r.Value, buffers[1 - cur]);
            cur = 1 - cur;
            len = n;
        }

        return len > 0 && a.IsAccepting(buffers[cur].AsSpan(0, len));
    }

    private static string Mutate(Random rng, string s, int edits)
    {
        var chars = new List<char>(s);
        for (int i = 0; i < edits && chars.Count > 0; i++)
        {
            int p = rng.Next(chars.Count);
            switch (rng.Next(4))
            {
                case 0:
                    chars.RemoveAt(p);
                    break;
                case 1:
                    chars.Insert(p, (char)('a' + rng.Next(26)));
                    break;
                case 2:
                    chars[p] = (char)('a' + rng.Next(26));
                    break;
                default:
                    if (p + 1 < chars.Count)
                    {
                        (chars[p], chars[p + 1]) = (chars[p + 1], chars[p]);
                    }

                    break;
            }
        }

        return new string([.. chars]);
    }

    [Fact]
    public void LevenshteinAutomatonAgreesWithDynamicProgramming()
    {
        var rng = new Random(99);
        string[] pool = [.. TextCorpus.MakeVocabulary(rng, 120).Where(static s => s.Length < 100)];
        int cases = 0;
        for (int i = 0; i < 300; i++)
        {
            string word = pool[rng.Next(pool.Length)];
            string query = rng.Next(3) == 0 ? "" : rng.Next(5) == 0 ? Mutate(rng, word, 3) : word;
            foreach (int k in new[] { 0, 1, 2, 3 })
            {
                foreach (bool t in new[] { false, true })
                {
                    var a = new LevenshteinAutomaton(query, k, t);
                    for (int j = 0; j < 60; j++)
                    {
                        string candidate = j % 3 == 0 ? pool[rng.Next(pool.Length)] : Mutate(rng, rng.Next(2) == 0 ? query : word, rng.Next(0, 5));
                        Assert.Equal(Distance(query, candidate, t) <= k, Accepts(a, candidate));
                        cases++;
                    }
                }
            }
        }

        Assert.True(cases > 140_000);
    }

    [Theory]
    [InlineData("abc", "abc", true)]
    [InlineData("abc", "ab", false)]
    [InlineData("a.c", "a\ncx", false)]
    [InlineData("(ab)+c?", "ababc", true)]
    [InlineData("(ab)+c?", "abac", false)]
    [InlineData("[a-c]{2,3}x", "abx", true)]
    [InlineData("[a-c]{2,3}x", "abcax", false)]
    [InlineData("[^a-c]+", "xyz", true)]
    [InlineData("[^a-c]+", "xbz", false)]
    [InlineData("\\d+\\.\\d", "12.5", true)]
    [InlineData("\\w*\\s", "ab_9 ", true)]
    [InlineData("a|b|", "", true)]
    [InlineData("\\u00e9t\\U{1F600}", "ét\U0001F600", true)]
    [InlineData("é.", "éx", true)]
    [InlineData("(a*)*b", "aaab", true)]
    [InlineData("a{0}", "", true)]
    [InlineData("a{2,}", "a", false)]
    public void RegexAutomatonExamples(string pattern, string term, bool expected)
        => Assert.Equal(expected, Accepts(new RegexAutomaton(pattern), term));

    [Theory]
    [InlineData("(")]
    [InlineData("a)")]
    [InlineData("[a")]
    [InlineData("*a")]
    [InlineData("a{2")]
    [InlineData("a{3,2}")]
    [InlineData("^a")]
    [InlineData("a$")]
    [InlineData("\\q")]
    [InlineData("[z-a]")]
    [InlineData("a{2000}")]
    [InlineData("(a{1000}){1000}")]
    public void RegexAutomatonRejectsBadPatterns(string pattern)
        => Assert.Throws<RegexSyntaxException>(() => new RegexAutomaton(pattern));

    [Fact]
    public void RegexAutomatonAgreesWithTheBclRegexOnRandomPatterns()
    {
        var rng = new Random(5);
        string[] atoms = ["a", "b", "c", "[ab]", "[^a]", ".", "(ab|c)", "(a|bc|)", "é", "[a-c]", "(b+)", "a?"];
        string[] suffixes = ["", "", "*", "+", "?", "{2}", "{1,3}", "{2,}"];
        string[] terms = [.. TextCorpus.MakeVocabulary(rng, 150).Where(static s => s.Length < 20), "", "a", "ab", "abc", "aab", "bca", "abab", "cab", "bbb", "éa"];
        int cases = 0;
        for (int p = 0; p < 400; p++)
        {
            string pattern = string.Concat(Enumerable.Range(0, rng.Next(1, 5)).Select(_ => "(" + atoms[rng.Next(atoms.Length)] + ")" + suffixes[rng.Next(suffixes.Length)]));
            var re = new Regex("^(?:" + pattern + ")$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
            var a = new RegexAutomaton(pattern);
            foreach (string t in terms)
            {
                Assert.True(re.IsMatch(ForBcl(t)) == Accepts(a, t), $"pattern {pattern} term {t}");
                cases++;
            }

            for (int j = 0; j < 40; j++)
            {
                string t = new(Enumerable.Range(0, rng.Next(0, 7)).Select(_ => "abcé"[rng.Next(4)]).ToArray());
                Assert.True(re.IsMatch(t) == Accepts(a, t), $"pattern {pattern} term {t}");
                cases++;
            }
        }

        Assert.True(cases > 30_000);
    }

    private static (TextSegmentReader Reader, List<byte[]> Terms) BuildDictionary(int seed, int vocabulary, bool includeEmpty)
    {
        var rng = new Random(seed);
        string[] words = TextCorpus.MakeVocabulary(rng, vocabulary);
        var sorted = new SortedSet<byte[]>(TextCorpus.ByteOrder);
        foreach (string w in words)
        {
            sorted.Add(Encoding.UTF8.GetBytes(w));
            if (rng.Next(3) == 0 && w.Length > 2)
            {
                // Terms that are prefixes of others exercise final nodes with children.
                sorted.Add(Encoding.UTF8.GetBytes(w[..(w.Length / 2)]));
            }
        }

        if (includeEmpty)
        {
            sorted.Add([]);
        }

        var storage = new MemoryStorage();
        using (var writer = new TextSegmentWriter(storage, "d.terms", "d.postings", "d.lengths", 4))
        {
            writer.BeginField("f", PostingsFeatures.Frequencies, [1, 1, 1, 1]);
            foreach (byte[] t in sorted)
            {
                writer.BeginTerm(t);
                writer.AddDocument(rng.Next(4), 1);
                writer.EndTerm();
            }

            writer.EndField();
            writer.Finish();
        }

        return (TextSegmentReader.Open(storage, "d.terms", "d.postings", "d.lengths"), [.. sorted]);
    }

    private static List<byte[]> Collect(ITermEnumerator e)
    {
        var list = new List<byte[]>();
        using (e)
        {
            while (e.MoveNext())
            {
                list.Add(e.Term.ToArray());
            }
        }

        return list;
    }

    [Theory]
    [InlineData(1, 60, false)]
    [InlineData(2, 1500, true)]
    [InlineData(3, 4000, false)]
    public void PrefixFuzzyAndRegexEqualABruteForceScan(int seed, int vocabulary, bool includeEmpty)
    {
        (TextSegmentReader reader, List<byte[]> terms) = BuildDictionary(seed, vocabulary, includeEmpty);
        using (reader)
        {
            ITextFieldReader f = reader.GetTextField("f")!;
            Assert.True(Collect(f.GetTerms(default)).SequenceEqual(terms, new ByteArrayEquality()));
            var rng = new Random(seed * 31);
            string[] strings = [.. terms.Select(static t => Encoding.UTF8.GetString(t))];
            int cases = 0;

            // Prefixes taken from present terms (cut at any byte, even inside a character) and from nowhere.
            for (int i = 0; i < 300; i++)
            {
                byte[] t = terms[rng.Next(terms.Count)];
                byte[] prefix = rng.Next(5) == 0 ? [(byte)'z', (byte)'q', (byte)'z'] : t[..rng.Next(0, Math.Min(t.Length, 5) + 1)];
                List<byte[]> expected = [.. terms.Where(x => x.AsSpan().StartsWith(prefix))];
                Assert.True(Collect(f.GetTerms(prefix)).SequenceEqual(expected, new ByteArrayEquality()));
                cases++;
            }

            // Fuzzy: distance 1 and 2, with and without transpositions, from mutated terms.
            for (int i = 0; i < 120; i++)
            {
                string word = strings[rng.Next(strings.Length)];
                if (word.Length > 200)
                {
                    continue;
                }

                string query = Mutate(rng, word, rng.Next(0, 3));
                foreach (int k in new[] { 1, 2 })
                {
                    foreach (bool tr in new[] { false, true })
                    {
                        List<byte[]> expected = [.. terms.Where(t => Distance(query, Encoding.UTF8.GetString(t), tr) <= k)];
                        List<byte[]> got = Collect(f.Intersect(new LevenshteinAutomaton(query, k, tr)));
                        Assert.True(got.SequenceEqual(expected, new ByteArrayEquality()), $"fuzzy {query} k={k} t={tr}");
                        cases++;
                    }
                }

                // A required exact first character, as typo tolerance often wants.
                if (query.Length > 0)
                {
                    byte[] first = Encoding.UTF8.GetBytes(query.EnumerateRunes().First().ToString());
                    List<byte[]> expected = [.. terms.Where(t => t.AsSpan().StartsWith(first) && Distance(query, Encoding.UTF8.GetString(t), true) <= 1)];
                    Assert.True(Collect(f.Intersect(new LevenshteinAutomaton(query, 1), first)).SequenceEqual(expected, new ByteArrayEquality()));
                    cases++;
                }
            }

            // Regular expressions against the BCL's.
            string[] patterns = ["a.*", "[a-c]{1,3}", ".*e", "(ab|ba)+.*", "[^a-z]+", ".", "..", "é.*", "[a-z]*s?", "x?y?z?", ".{5,}", "[\u3040-\u9fff]+", ".*"];
            foreach (string pattern in patterns)
            {
                var re = new Regex("^(?:" + pattern + ")$", RegexOptions.Singleline | RegexOptions.CultureInvariant);
                List<byte[]> expected = [.. terms.Where(t => re.IsMatch(ForBcl(Encoding.UTF8.GetString(t))))];
                Assert.True(Collect(f.Intersect(new RegexAutomaton(pattern))).SequenceEqual(expected, new ByteArrayEquality()), pattern);
                cases++;
            }

            Assert.True(cases > 500);
        }
    }

    [Fact]
    public void TermInfoSurvivesRandomAccessAcrossSamples()
    {
        (TextSegmentReader reader, List<byte[]> terms) = BuildDictionary(8, 2000, false);
        using (reader)
        {
            ITextFieldReader f = reader.GetTextField("f")!;
            var rng = new Random(1);
            using ITermEnumerator e = f.GetTerms(default);
            var infos = new List<TermInfo>();
            while (e.MoveNext())
            {
                infos.Add(e.Info);
            }

            for (int i = 0; i < 2000; i++)
            {
                int k = rng.Next(terms.Count);
                Assert.True(f.TryGetTerm(terms[k], out TermInfo info));
                Assert.Equal(infos[k], info);
            }
        }
    }

    private sealed class ByteArrayEquality : IEqualityComparer<byte[]>
    {
        public bool Equals(byte[]? x, byte[]? y) => x!.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => obj.Length;
    }
}
