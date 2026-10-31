using BenchmarkDotNet.Attributes;
using OneIndex.Codecs;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Bench;

/// <summary>
/// Indicative timings of the text index primitives on a synthetic Zipf-like segment of a million documents: term lookup, postings
/// decode (documents and frequencies; with positions) and block-max WAND against the same disjunction scored exhaustively. The
/// corpus is generated here from a fixed seed; nothing is read from disk but the index files this builds in memory.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class TextIndexBenchmarks
{
    private const int Docs = 1_000_000;
    private const int Terms = 100_000;
    private MemoryStorage _storage = null!;
    private TextSegmentReader _reader = null!;
    private ITextFieldReader _field = null!;
    private byte[][] _lookupTerms = null!;
    private TermInfo _big;
    private TermPostings _postings = null!;
    private TermInfo[] _queryTerms = null!;
    private float _average;

    private static byte[] Name(int i) => System.Text.Encoding.ASCII.GetBytes("t" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture));

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        var lengths = new int[Docs];
        for (int d = 0; d < Docs; d++)
        {
            lengths[d] = 20 + rng.Next(180);
        }

        _storage = new MemoryStorage();
        using (var writer = new TextSegmentWriter(_storage, "b.terms", "b.postings", "b.lengths", Docs))
        {
            writer.BeginField("body", PostingsFeatures.Positions, lengths);
            var seen = new HashSet<int>();
            for (int t = 0; t < Terms; t++)
            {
                int df = t == 0 ? Docs : Math.Max(1, Docs / (t + 1) / 8);
                writer.BeginTerm(Name(t));
                IEnumerable<int> docs;
                if (df == Docs)
                {
                    docs = Enumerable.Range(0, Docs);
                }
                else
                {
                    seen.Clear();
                    while (seen.Count < df)
                    {
                        seen.Add(rng.Next(Docs));
                    }

                    docs = seen.Order();
                }

                foreach (int d in docs)
                {
                    int freq = 1 + (rng.Next(4) == 0 ? rng.Next(3) : 0);
                    writer.AddDocument(d, freq);
                    int pos = 0;
                    for (int k = 0; k < freq; k++)
                    {
                        pos += rng.Next(30);
                        writer.AddPosition(pos);
                    }
                }

                writer.EndTerm();
            }

            writer.EndField();
            writer.Finish();
        }

        _reader = TextSegmentReader.Open(_storage, "b.terms", "b.postings", "b.lengths");
        _field = _reader.GetTextField("body")!;
        _average = (float)_field.Statistics.SumTotalTermFreq / _field.Statistics.DocCount;
        _lookupTerms = [.. Enumerable.Range(0, 10_000).Select(i => Name(rng.Next(Terms)))];
        _field.TryGetTerm(Name(0), out _big);
        _postings = (TermPostings)_field.GetPostings(_big, PostingsFeatures.Positions);
        _queryTerms = new TermInfo[5];
        for (int i = 0; i < 5; i++)
        {
            _field.TryGetTerm(Name(1 + (i * 3)), out _queryTerms[i]);
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _reader.Dispose();

    /// <summary>10,000 exact lookups in a 100,000-term dictionary.</summary>
    [Benchmark]
    public int TermLookup10k()
    {
        int found = 0;
        foreach (byte[] t in _lookupTerms)
        {
            if (_field.TryGetTerm(t, out _))
            {
                found++;
            }
        }

        return found;
    }

    /// <summary>A list of 1,000,000 documents with frequencies.</summary>
    [Benchmark]
    public long DecodeMillionDocsWithFrequencies()
    {
        _postings.Reset(_big, PostingsFeatures.Frequencies);
        long sum = 0;
        for (int d = _postings.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = _postings.NextDoc())
        {
            sum += _postings.Frequency;
        }

        return sum;
    }

    /// <summary>The same list with every position read.</summary>
    [Benchmark]
    public long DecodeMillionDocsWithPositions()
    {
        _postings.Reset(_big, PostingsFeatures.Positions);
        long sum = 0;
        for (int d = _postings.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = _postings.NextDoc())
        {
            int f = _postings.Frequency;
            for (int k = 0; k < f; k++)
            {
                sum += _postings.NextPosition();
            }
        }

        return sum;
    }

    /// <summary>Five frequent terms, top 10, with block-max WAND.</summary>
    [Benchmark]
    public int BlockMaxWandTop10() => Top10(prune: true);

    /// <summary>The same disjunction, every matching document scored.</summary>
    [Benchmark(Baseline = true)]
    public int ExhaustiveTop10() => Top10(prune: false);

    private int Top10(bool prune)
    {
        var clauses = new Scorer[_queryTerms.Length];
        for (int i = 0; i < clauses.Length; i++)
        {
            float weight = 1f + (i * 0.3f);
            clauses[i] = new PostingsScorer(_field.GetPostings(_queryTerms[i], PostingsFeatures.Frequencies), _field, (tf, len) => weight * (tf * 2.2f) / (tf + (1.2f * (0.25f + (0.75f * len / _average)))));
        }

        var wand = new BlockMaxWandScorer(clauses);
        Span<float> bestScores = stackalloc float[10];
        int kept = 0;
        for (int d = wand.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = wand.NextDoc())
        {
            float s = wand.Score();
            if (kept < 10)
            {
                bestScores[kept++] = s;
                if (kept == 10 && prune)
                {
                    wand.SetMinCompetitiveScore(Min(bestScores));
                }
            }
            else if (s > Min(bestScores))
            {
                int worst = 0;
                for (int i = 1; i < 10; i++)
                {
                    if (bestScores[i] < bestScores[worst])
                    {
                        worst = i;
                    }
                }

                bestScores[worst] = s;
                if (prune)
                {
                    wand.SetMinCompetitiveScore(Min(bestScores));
                }
            }
        }

        return kept;
    }

    private static float Min(ReadOnlySpan<float> v)
    {
        float m = v[0];
        foreach (float x in v)
        {
            m = Math.Min(m, x);
        }

        return m;
    }
}
