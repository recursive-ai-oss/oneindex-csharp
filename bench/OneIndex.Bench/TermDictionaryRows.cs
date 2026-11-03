using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using OneIndex.Codecs;
using OneIndex.Storage;
using OneIndex.Search;

namespace OneIndex.Bench;

/// <summary>
/// Term expansion over a term dictionary alone (no postings, no scoring): fuzzy with one and two edits, the typo plan, prefix.
/// The terms come from a file of "term, tab, document frequency" lines in byte order (ONEINDEX_BENCH_TERMS); the query words are
/// seeded picks of the dictionary's own terms with one substitution. Run through BenchmarkDotNet (<c>--filter '*TermDictionaryRows*'</c>)
/// or <c>ONEINDEX_TERMS_QUICK=1</c> for a stopwatch pass that prints the matched-term checksum.
/// </summary>
[MemoryDiagnoser]
public class TermDictionaryRows
{
    private const int Queries = 100;
    private FieldTermDictionary[] _dictionaries = [];
    private string[] _fuzzy1 = [];
    private string[] _fuzzy2 = [];
    private string[] _typo = [];
    private string[] _prefix = [];
    private int _dictionaryBytes;

    /// <summary>Builds the dictionary and the query words.</summary>
    [GlobalSetup]
    public void Setup()
    {
        string path = Environment.GetEnvironmentVariable("ONEINDEX_BENCH_TERMS") ?? throw new InvalidOperationException("Set ONEINDEX_BENCH_TERMS to a terms file.");
        var terms = new List<(byte[] Term, int DocFreq)>();
        foreach (string line in File.ReadLines(path))
        {
            int tab = line.IndexOf('\t');
            terms.Add((Encoding.UTF8.GetBytes(line[..tab]), int.Parse(line.AsSpan(tab + 1), System.Globalization.CultureInfo.InvariantCulture)));
        }

        terms.Sort(static (a, b) => a.Term.AsSpan().SequenceCompareTo(b.Term));
        int version = int.Parse(Environment.GetEnvironmentVariable("ONEINDEX_TERMS_VERSION") ?? "2");
        int segments = int.Parse(Environment.GetEnvironmentVariable("ONEINDEX_TERMS_SEGMENTS") ?? "1");
        _dictionaries = new FieldTermDictionary[segments];
        for (int g = 0; g < segments; g++)
        {
            // Every segment holds a share of the terms (an index of several segments repeats the common ones in each).
            _dictionaries[g] = Build([.. terms.Where((_, i) => i % segments == g)], version, out int size);
            _dictionaryBytes += size;
        }
        var rng = new Random(1);
        // Query words are drawn like the text rows draw them: from the terms of documents, so common words are likelier than rare ones.
        var eligible = terms.Where(static t => t.Term.Length >= 6 && t.Term.All(static b => b is >= (byte)'a' and <= (byte)'z')).ToArray();
        long[] cumulative = new long[eligible.Length];
        long running = 0;
        for (int i = 0; i < eligible.Length; i++)
        {
            running += eligible[i].DocFreq;
            cumulative[i] = running;
        }

        string Pick(int min)
        {
            while (true)
            {
                int at = Array.BinarySearch(cumulative, rng.NextInt64(running));
                string w = Encoding.UTF8.GetString(eligible[at < 0 ? ~at : at].Term);
                if (w.Length >= min)
                {
                    return w;
                }
            }
        }

        string Sub(string w, int from) { var c = w.ToCharArray(); int p = from + rng.Next(c.Length - from); char o; do { o = (char)('a' + rng.Next(26)); } while (o == c[p]); c[p] = o; return new string(c); }
        _fuzzy1 = [.. Enumerable.Range(0, Queries).Select(_ => Sub(Pick(5), 0))];
        _fuzzy2 = [.. Enumerable.Range(0, Queries).Select(_ => { string s = Sub(Pick(6), 0); return s.Remove(rng.Next(s.Length), 1); })];
        _typo = [.. Enumerable.Range(0, Queries).Select(_ => Sub(Pick(6), 1))];
        if (Environment.GetEnvironmentVariable("ONEINDEX_BENCH_QUERIES") is { } queryFile)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(queryFile));
            string[] Words(string row, string property) => [.. doc.RootElement.GetProperty(row).EnumerateArray().Select(q => q.GetProperty(property).GetString()!)];
            _fuzzy1 = Words("Fuzzy1", "term");
            _fuzzy2 = Words("Fuzzy2", "term");
            _typo = Words("Typo", "word");
        }

        _prefix = [.. Enumerable.Range(0, Queries).Select(_ => Pick(5)[..3])];
    }

    private static FieldTermDictionary Build(List<(byte[] Term, int DocFreq)> terms, int version, out int size)
    {
        var fst = new FstBuilder(IndexLimits.Default.MaxTermBytes, version);
        var info = new TermInfoBuilder();
        long offset = 0;
        int max = 0;
        foreach ((byte[] term, int df) in terms)
        {
            fst.Add(term);
            info.Add(df, df, offset);
            offset += df;
            max = Math.Max(max, term.Length);
        }

        (byte[] bytes, int root) = fst.Finish();
        byte[] all = [.. bytes, .. info.Table, .. info.Samples];
        size = bytes.Length;
        return new FieldTermDictionary("bench.terms", version, all, 0, bytes.Length, root, bytes.Length, info.Table.Length, bytes.Length + info.Table.Length, (terms.Count + 31) / 32, terms.Count, fst.TotalTermBytes, max);
    }

    private static long Drain(ITermEnumerator e)
    {
        long n = 0;
        using (e)
        {
            while (e.MoveNext())
            {
                n += e.Term.Length + e.Info.DocFreq;
            }
        }

        return n;
    }

    /// <summary>Fuzzy, one edit, no fixed prefix.</summary>
    [Benchmark(OperationsPerInvoke = Queries)]
    public long Fuzzy1() => RunFuzzy(_fuzzy1, 1);

    /// <summary>Fuzzy, two edits, no fixed prefix.</summary>
    [Benchmark(OperationsPerInvoke = Queries)]
    public long Fuzzy2() => RunFuzzy(_fuzzy2, 2);

    /// <summary>The typo plan's passes (first letter fixed with one typo, then free with one edit for a budget of two): budget one here.</summary>
    [Benchmark(OperationsPerInvoke = Queries)]
    public long Typo()
    {
        long n = 0;
        foreach (string w in _typo)
        {
            var automaton = new LevenshteinAutomaton(w, 1);
            foreach (FieldTermDictionary d in _dictionaries)
            {
                n += Drain(d.Enumerate(Encoding.UTF8.GetBytes(w[..1]), automaton));
            }
        }

        return n;
    }

    /// <summary>Prefix of three letters.</summary>
    [Benchmark(OperationsPerInvoke = Queries)]
    public long Prefix()
    {
        long n = 0;
        foreach (string w in _prefix)
        {
            foreach (FieldTermDictionary d in _dictionaries)
            {
                n += Drain(d.GetTerms(Encoding.UTF8.GetBytes(w)));
            }
        }

        return n;
    }

    private long RunFuzzy(string[] words, int edits)
    {
        long n = 0;
        foreach (string w in words)
        {
            var automaton = new LevenshteinAutomaton(w, edits);
            foreach (FieldTermDictionary d in _dictionaries)
            {
                n += Drain(d.Enumerate([], automaton));
            }
        }

        return n;
    }

    /// <summary>A stopwatch pass for quick comparisons: microseconds per query and a checksum of what matched.</summary>
    public static void Quick()
    {
        var rows = new TermDictionaryRows();
        rows.Setup();
        Console.WriteLine($"dictionary automaton bytes: {rows._dictionaryBytes}");
        foreach ((string name, Func<long> run) in new (string, Func<long>)[] { ("Fuzzy1", rows.Fuzzy1), ("Fuzzy2", rows.Fuzzy2), ("Typo", rows.Typo), ("Prefix", rows.Prefix) })
        {
            string? only = Environment.GetEnvironmentVariable("ONEINDEX_TERMS_ROWS");
            if (only is not null && !only.Split(',').Contains(name))
            {
                continue;
            }

            long sum = run();
            double best = double.MaxValue;
            for (int i = 0; i < int.Parse(Environment.GetEnvironmentVariable("ONEINDEX_TERMS_REPS") ?? "5"); i++)
            {
                long t = Stopwatch.GetTimestamp();
                run();
                best = Math.Min(best, Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            }

            Console.WriteLine($"{name,-7} {best * 1000 / Queries,9:F1} us/query  checksum {sum}");
        }
    }
}
