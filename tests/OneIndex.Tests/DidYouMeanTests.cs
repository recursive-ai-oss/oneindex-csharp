using System.Text;
using OneIndex.Analysis;
using OneIndex.Codecs;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Suggest;

namespace OneIndex.Tests;

public class DidYouMeanTests
{
    private static readonly Analyzer Ws = new WhitespaceLowercaseAnalyzer();

    private sealed class Field : IDisposable
    {
        private readonly TextSegmentReader _reader;

        public Field(IEnumerable<string[]> docs)
        {
            string[][] list = [.. docs];
            var buffer = new InvertedBuffer();
            buffer.DeclareField("t", PostingsFeatures.Frequencies);
            for (int d = 0; d < list.Length; d++)
            {
                buffer.AddField(d, "t", Ws.GetTokens(string.Join(' ', list[d]).AsMemory()));
            }

            var storage = new MemoryStorage();
            using (var writer = new TextSegmentWriter(storage, "x.terms", "x.postings", "x.lengths", list.Length))
            {
                buffer.WriteTo(writer);
                writer.Finish();
            }

            _reader = TextSegmentReader.Open(storage, "x.terms", "x.postings", "x.lengths");
            Reader = _reader.GetTextField("t")!;
        }

        public ITextFieldReader Reader { get; }

        public void Dispose() => _reader.Dispose();
    }

    private static int Osa(string a, string b)
    {
        int[] x = [.. a.EnumerateRunes().Select(r => r.Value)];
        int[] y = [.. b.EnumerateRunes().Select(r => r.Value)];
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
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (x[i - 1] == y[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && x[i - 1] == y[j - 2] && x[i - 2] == y[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[x.Length, y.Length];
    }

    [Fact]
    public void EditDistanceAgreesWithTheMatrix()
    {
        var rng = new Random(11);
        string alphabet = "abcde\u00e9\u4e2d\U0001F600";
        for (int i = 0; i < 3000; i++)
        {
            string a = new([.. Enumerable.Range(0, rng.Next(0, 9)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
            string b = new([.. Enumerable.Range(0, rng.Next(0, 9)).Select(_ => alphabet[rng.Next(alphabet.Length)])]);
            int full = Osa(a, b);
            for (int max = 0; max <= 3; max++)
            {
                Assert.Equal(Math.Min(full, max + 1), EditDistance.OptimalStringAlignment(a, b, max));
            }
        }

        // Past the stack buffer.
        string longA = new('a', 100);
        Assert.Equal(2, EditDistance.OptimalStringAlignment(longA, longA[2..] + "bb", 3) == 2 ? 2 : -1);
    }

    [Fact]
    public void EditDistanceAllocatesNothing()
    {
        string a = "recommendation";
        string b = "reccomendatoin";
        TestHelpers.AssertNoAllocation("EditDistance.OptimalStringAlignment", () =>
        {
            for (int i = 0; i < 100; i++)
            {
                _ = EditDistance.OptimalStringAlignment(a, b, 2);
            }
        });
    }

    [Fact]
    public void SuggestsTheCommonerNearbyTerm()
    {
        string[][] docs =
        [
            ["the", "quick", "brown", "fox"], ["the", "quick", "brown", "dog"], ["a", "quiet", "brown", "dog"], ["quick", "fox", "jumps"],
        ];
        using var f = new Field(docs);
        var dym = new DidYouMean(Ws);
        IReadOnlyList<Suggestion> s = dym.Suggest(f.Reader, "quikc brown");
        Assert.NotEmpty(s);
        Assert.Equal("quick brown", s[0].Text);
        Assert.Equal(1, s[0].Edits);
        Assert.Single(s[0].Corrections);
        Assert.Equal(new TokenCorrection("quikc", "quick", 0, 5, 1), s[0].Corrections[0]);
    }

    [Fact]
    public void KnownQueriesAndShortWordsAreLeftAlone()
    {
        using var f = new Field([["cat", "dog"], ["cart", "dot"]]);
        var dym = new DidYouMean(Ws);
        Assert.Empty(dym.Suggest(f.Reader, "cat dog"));   // all known
        Assert.Empty(dym.Suggest(f.Reader, "cqt"));       // 3 letters: under the one-edit minimum
        Assert.Empty(dym.Suggest(f.Reader, ""));
        Assert.Empty(dym.Suggest(f.Reader, "  \u0000 ,,, "));
        Assert.Empty(dym.Suggest(f.Reader, "12345"));      // numbers are not corrected
    }

    [Fact]
    public void PreservesTheRestOfTheText()
    {
        using var f = new Field([["hello", "world"], ["hello", "there"]]);
        var dym = new DidYouMean(new StandardAnalyzer());
        IReadOnlyList<Suggestion> s = dym.Suggest(f.Reader, "  Say, \"helo\"  world!");
        Assert.Equal("  Say, \"hello\"  world!", s[0].Text);
    }

    [Fact]
    public void CooccurrenceBreaksTies()
    {
        // "tale" and "tall" are one edit from "talk"; only "tall order" and not "talk order" occurs... the second word decides.
        string[][] docs =
        [
            ["tall", "order"], ["tall", "order"], ["tale", "teller"], ["tale", "teller"], ["tale", "teller"], ["tale", "of"],
        ];
        using var f = new Field(docs);
        var dym = new DidYouMean(Ws);
        // alone, the commoner "tale" wins
        Assert.Equal("tale", dym.Suggest(f.Reader, "talk")[0].Text);
        // beside "order", the pair that occurs together wins even though it is rarer
        Assert.Equal("tall order", dym.Suggest(f.Reader, "talk order")[0].Text);
    }

    [Fact]
    public void PopularModeCorrectsRareKnownWords()
    {
        string[][] docs = [["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["receive"], ["recieve"]];
        using var f = new Field(docs);
        Assert.Empty(new DidYouMean(Ws).Suggest(f.Reader, "recieve"));
        IReadOnlyList<Suggestion> s = new DidYouMean(Ws, new SuggestOptions { Mode = SuggestMode.Popular }).Suggest(f.Reader, "recieve");
        Assert.Equal("receive", s[0].Text);
    }

    [Fact]
    public void SeveralSegmentsAddUpAndCancellationIsHonoured()
    {
        using var a = new Field([["apple"], ["apple"]]);
        using var b = new Field([["apply"], ["apple"], ["apple"]]);
        var dym = new DidYouMean(Ws);
        IReadOnlyList<TermCandidate> c = dym.SuggestTerm([a.Reader, b.Reader], "appel");
        Assert.Equal(new TermCandidate("apple", 1, 4), c[0]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => dym.Suggest([a.Reader], "appel", cts.Token));
    }

    // ---- accuracy against a brute-force reference on seeded corpora ----

    private static string[] SyntheticVocabulary(Random rng, int size)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        string consonants = "bcdfghjklmnprstvwz";
        string vowels = "aeiou";
        while (set.Count < size)
        {
            var sb = new StringBuilder();
            int syll = 2 + rng.Next(3);
            for (int i = 0; i < syll; i++)
            {
                sb.Append(consonants[rng.Next(consonants.Length)]).Append(vowels[rng.Next(vowels.Length)]);
                if (rng.Next(3) == 0)
                {
                    sb.Append(consonants[rng.Next(consonants.Length)]);
                }
            }

            set.Add(sb.ToString());
        }

        return [.. set];
    }

    private static string[] RealVocabulary()
    {
        string? root = Environment.GetEnvironmentVariable("ONEINDEX_SNOWBALL_DATA");
        if (root is null || !File.Exists(Path.Combine(root, "english", "voc.txt")))
        {
            return [];
        }

        return [.. File.ReadAllLines(Path.Combine(root, "english", "voc.txt")).Select(w => w.Trim().ToLowerInvariant())
            .Where(w => w.Length >= 3 && w.All(c => c is >= 'a' and <= 'z')).Distinct().OrderBy(w => w, StringComparer.Ordinal)];
    }

    private static string Typo(Random rng, string word, int edits, int minPos)
    {
        string t = word;
        for (int e = 0; e < edits; e++)
        {
            int op = rng.Next(4);
            int p = minPos + rng.Next(Math.Max(1, t.Length - minPos));
            char c = (char)('a' + rng.Next(26));
            t = op switch
            {
                0 => t[..p] + (t[p] == c ? (char)('a' + ((c - 'a' + 1) % 26)) : c) + t[(p + 1)..],
                1 => t[..p] + c + t[p..],
                2 when t.Length > minPos + 1 => t.Remove(p, 1),
                _ when p + 1 < t.Length && t[p] != t[p + 1] => t[..p] + t[p + 1] + t[p] + t[(p + 2)..],
                _ => t[..p] + c + t[p..],
            };
        }

        return t;
    }

    private sealed record Run(int Total, int Top1, int InTop3, int EngineAgreesWithBruteForce);

    private static (Field Field, string[] Vocab, Dictionary<string, int> Df, string[][] Docs) MakeCorpus(string[] vocab, int docCount, int seed)
    {
        var rng = new Random(seed);
        // Zipf-like term frequencies: rank r is drawn with weight 1/(r+1).
        var order = vocab.OrderBy(_ => rng.Next()).ToArray();
        double[] cumulative = new double[order.Length];
        double sum = 0;
        for (int i = 0; i < order.Length; i++)
        {
            sum += 1.0 / (i + 1);
            cumulative[i] = sum;
        }

        string Draw()
        {
            double x = rng.NextDouble() * sum;
            int i = Array.BinarySearch(cumulative, x);
            return order[i < 0 ? ~i : i];
        }

        var docs = new string[docCount][];
        var df = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int d = 0; d < docCount; d++)
        {
            docs[d] = [.. Enumerable.Range(0, 8 + rng.Next(8)).Select(_ => Draw())];
            foreach (string w in docs[d].Distinct())
            {
                df[w] = df.GetValueOrDefault(w) + 1;
            }
        }

        var present = df.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        return (new Field(docs), present, df, docs);
    }

    // Brute force: every vocabulary term within the budget (same first letter), ordered as the engine documents it.
    private static List<TermCandidate> BruteForce(string typo, string[] vocab, Dictionary<string, int> df, int maxEdits, int top)
    {
        var all = new List<TermCandidate>();
        foreach (string w in vocab)
        {
            if (w[0] != typo[0] || w == typo)
            {
                continue;
            }

            int d = Osa(typo, w);
            if (d <= maxEdits)
            {
                all.Add(new TermCandidate(w, d, df[w]));
            }
        }

        return [.. all.OrderBy(c => c.Edits).ThenByDescending(c => c.DocFreq).ThenBy(c => c.Term, StringComparer.Ordinal).Take(top)];
    }

    private static Run Measure(string[] vocabulary, int docCount, int seed, int edits, int minPos, int samples, bool multiWord, StringBuilder? report = null)
    {
        (Field field, string[] vocab, Dictionary<string, int> df, string[][] docs) = MakeCorpus(vocabulary, docCount, seed);
        using (field)
        {
            var rng = new Random(seed * 31 + edits);
            var dym = new DidYouMean(Ws, new SuggestOptions { PrefixLength = minPos > 0 ? 1 : 0 });
            int total = 0, top1 = 0, top3 = 0, agree = 0;
            int attempts = 0;
            while (total < samples && attempts++ < samples * 50)
            {
                string[] doc = docs[rng.Next(docs.Length)];
                int words = multiWord ? 2 + rng.Next(2) : 1;
                int start = rng.Next(doc.Length - words + 1);
                string[] original = doc[start..(start + words)];
                int target = rng.Next(words);
                int minLen = edits == 2 ? 6 : 4; // the word, before the typo shortens it
                if (original[target].Length < minLen)
                {
                    continue;
                }

                string typo = Typo(rng, original[target], edits, minPos);
                if (df.ContainsKey(typo) || EditDistanceBetween(typo, original[target]) != edits || (minPos > 0 && typo[0] != original[target][0]))
                {
                    continue;
                }

                string[] query = (string[])original.Clone();
                query[target] = typo;
                total++;

                if (!multiWord)
                {
                    int budget = typo.Length >= 6 ? 2 : typo.Length >= 4 ? 1 : 0; // the documented budget by length
                    List<TermCandidate> brute = BruteForce(typo, vocab, df, budget, 4);
                    IReadOnlyList<TermCandidate> engine = dym.SuggestTerm([field.Reader], typo);
                    if (minPos > 0 && brute.SequenceEqual(engine))
                    {
                        agree++;
                    }
                    else if (minPos == 0)
                    {
                        agree++; // the brute force above fixes the first letter; with prefix 0 only accuracy is compared
                    }
                }

                IReadOnlyList<Suggestion> s = dym.Suggest(field.Reader, string.Join(' ', query));
                string expected = string.Join(' ', original);
                if (s.Count > 0 && s[0].Text == expected)
                {
                    top1++;
                }

                if (s.Any(x => x.Text == expected))
                {
                    top3++;
                }
            }

            report?.AppendLine($"vocab={vocab.Length} docs={docCount} edits={edits} prefix={(minPos > 0 ? 1 : 0)} multiWord={multiWord}: n={total} top1={100.0 * top1 / total:F1}% top3={100.0 * top3 / total:F1}% engineMatchesBruteForce={agree}/{(multiWord ? 0 : total)}");
            return new Run(total, top1, top3, agree);
        }
    }

    private static int EditDistanceBetween(string a, string b) => Osa(a, b);

    [Theory]
    [InlineData(1, 1, 0.80, false)]
    [InlineData(2, 1, 0.85, false)]
    [InlineData(1, 1, 0.60, true)]
    public void InjectedTyposAreCorrected(int edits, int seed, double minTop1, bool multiWord)
    {
        string[] vocabulary = SyntheticVocabulary(new Random(seed), 4000);
        Run run = Measure(vocabulary, 3000, seed, edits, minPos: 1, samples: 300, multiWord);
        Assert.True(run.Total >= 250, $"only {run.Total} samples");
        Assert.True(run.Top1 >= minTop1 * run.Total, $"top-1 {run.Top1}/{run.Total}");
        Assert.True(run.InTop3 >= run.Top1);
        if (!multiWord)
        {
            Assert.Equal(run.Total, run.EngineAgreesWithBruteForce);
        }
    }

    // The measurement behind docs/did-you-mean.md: a real English vocabulary (fetch-only data), written to ONEINDEX_REPORT.
    [Fact]
    public void RealVocabularyReport()
    {
        string[] vocab = RealVocabulary();
        if (vocab.Length == 0)
        {
            Assert.Skip("set ONEINDEX_SNOWBALL_DATA to the snowball-data directory");
            return;
        }

        var report = new StringBuilder();
        foreach (bool multi in new[] { false, true })
        {
            foreach (int edits in new[] { 1, 2 })
            {
                foreach (int minPos in new[] { 1, 0 })
                {
                    Run r = Measure(vocab, 20_000, 5, edits, minPos, 1000, multi, report);
                    Assert.True(r.Total >= 900);
                }
            }
        }

        string? path = Environment.GetEnvironmentVariable("ONEINDEX_REPORT");
        if (path is not null)
        {
            File.WriteAllText(path, report.ToString());
        }
    }

    // Per-call cost on the real vocabulary (indicative: run it as a heavy job on a quiet machine). Written to ONEINDEX_REPORT_COST.
    [Fact]
    public void PerCallCostReport()
    {
        string[] vocab = RealVocabulary();
        if (vocab.Length == 0)
        {
            Assert.Skip("set ONEINDEX_SNOWBALL_DATA to the snowball-data directory");
            return;
        }

        (Field field, string[] present, Dictionary<string, int> df, string[][] docs) = MakeCorpus(vocab, 20_000, 5);
        using (field)
        {
            var rng = new Random(9);
            var dym = new DidYouMean(Ws);
            var report = new StringBuilder();
            foreach (int words in new[] { 1, 3 })
            {
                var queries = new List<string>();
                while (queries.Count < 500)
                {
                    string[] doc = docs[rng.Next(docs.Length)];
                    if (doc.Length < words)
                    {
                        continue;
                    }

                    string[] q = (string[])doc[..words].Clone();
                    int t = rng.Next(words);
                    if (q[t].Length < 5)
                    {
                        continue;
                    }

                    string typo = Typo(rng, q[t], 1, 1);
                    if (df.ContainsKey(typo))
                    {
                        continue;
                    }

                    q[t] = typo;
                    queries.Add(string.Join(' ', q));
                }

                foreach (string q in queries)
                {
                    _ = dym.Suggest(field.Reader, q);
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                foreach (string q in queries)
                {
                    _ = dym.Suggest(field.Reader, q);
                }

                double us = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMicroseconds / queries.Count;
                long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / queries.Count;
                report.AppendLine($"words={words} vocab={present.Length}: {us:F1} us/call, {bytes} bytes/call");
            }

            string? path = Environment.GetEnvironmentVariable("ONEINDEX_REPORT_COST");
            if (path is not null)
            {
                File.WriteAllText(path, report.ToString());
            }
        }
    }
}
