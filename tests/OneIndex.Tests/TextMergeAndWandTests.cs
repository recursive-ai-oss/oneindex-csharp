using System.Text;
using OneIndex.Codecs;
using OneIndex.Fuzz;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class TextMergeAndWandTests
{
    [Theory]
    [InlineData(1, 0.0)]
    [InlineData(2, 0.3)]
    [InlineData(3, 0.8)]
    public void MergedSegmentEqualsOneBuiltFromTheSurvivingDocuments(int seed, double deleteFraction)
    {
        var rng = new Random(seed);
        var corpora = new List<TextCorpus> { TextCorpus.Generate(seed * 10 + 1, 600, 500), TextCorpus.Generate(seed * 10 + 2, 1, 40), TextCorpus.Generate(seed * 10 + 3, 400, 900), TextCorpus.Generate(seed * 10 + 4, 300, 300) };
        var sources = new List<TextMergeSource>();
        var survivors = new List<TextCorpus>();
        var storage = new MemoryStorage();
        var readers = new List<TextSegmentReader>();
        int newDoc = 0;
        for (int i = 0; i < corpora.Count; i++)
        {
            corpora[i].Write(storage, "src" + i);
            TextSegmentReader reader = TextCorpus.Open(storage, "src" + i);
            readers.Add(reader);
            var deleted = new HashSet<int>();
            for (int d = 0; d < corpora[i].MaxDoc; d++)
            {
                if (rng.NextDouble() < deleteFraction)
                {
                    deleted.Add(d);
                }
            }

            survivors.Add(corpora[i].Without(deleted, out int[] map));
            int[] merged = [.. map.Select(m => m < 0 ? -1 : m + newDoc)];
            newDoc += survivors[^1].MaxDoc;
            sources.Add(new TextMergeSource(reader, merged));
        }

        using (var writer = new TextSegmentWriter(storage, "m.terms", "m.postings", "m.lengths", newDoc))
        {
            TextSegmentMerger.Merge(writer, sources);
            writer.Finish();
        }

        TextCorpus direct = TextCorpus.Concat(survivors);
        direct.Write(storage, "d");
        // The format is canonical, so a merge and a direct build produce the same bytes, not just the same postings.
        foreach (string ext in new[] { "terms", "postings", "lengths" })
        {
            Assert.True(TestHelpers.ReadAll(storage, "m." + ext).AsSpan().SequenceEqual(TestHelpers.ReadAll(storage, "d." + ext)), ext);
        }

        using TextSegmentReader mergedReader = TextCorpus.Open(storage, "m");
        TextIndexTests.Verify(direct, mergedReader, seed);
        foreach (TextSegmentReader r in readers)
        {
            r.Dispose();
        }
    }

    [Fact]
    public void MergeHonoursCancellationAndRefusesMismatchedFeatures()
    {
        var storage = new MemoryStorage();
        TextCorpus.Generate(1, 50, 100).Write(storage, "a");
        using TextSegmentReader a = TextCorpus.Open(storage, "a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var writer = new TextSegmentWriter(storage, "m.terms", "m.postings", "m.lengths", 50);
        Assert.Throws<OperationCanceledException>(() => TextSegmentMerger.Merge(writer, [new TextMergeSource(a, [.. Enumerable.Range(0, 50)])], cts.Token));

        var buffer = new InvertedBuffer();
        buffer.DeclareField("body", PostingsFeatures.Frequencies);
        buffer.AddToken(0, "body", "x"u8, 0);
        using (var w2 = new TextSegmentWriter(storage, "b.terms", "b.postings", "b.lengths", 1))
        {
            buffer.WriteTo(w2);
            w2.Finish();
        }

        using TextSegmentReader b = TextSegmentReader.Open(storage, "b.terms", "b.postings", "b.lengths");
        using var w3 = new TextSegmentWriter(storage, "n.terms", "n.postings", "n.lengths", 51);
        Assert.Throws<InvalidOperationException>(() => TextSegmentMerger.Merge(w3, [new TextMergeSource(a, [.. Enumerable.Range(0, 50)]), new TextMergeSource(b, [50])]));
    }

    private static float Weight(long docFreq, int maxDoc) => (float)Math.Log(1 + (maxDoc / (double)docFreq));

    // A saturating tf, shorter-is-better score: monotone as block-max WAND needs.
    private static TermScoreFunction Bm25Like(float weight, float averageLength) => (tf, len) => weight * (tf * 2.2f) / (tf + (1.2f * (0.25f + (0.75f * len / averageLength))));

    [Theory]
    [InlineData(1, 6000, 800, 10)]
    [InlineData(2, 12000, 1500, 10)]
    [InlineData(3, 3000, 300, 3)]
    [InlineData(4, 3000, 300, 100)]
    public void WandTopKEqualsExhaustiveTopK(int seed, int docs, int vocabulary, int k)
    {
        var corpus = TextCorpus.Generate(seed, docs, vocabulary);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        ITextFieldReader field = reader.GetTextField(TextCorpus.Body)!;
        byte[][] terms = [.. corpus.Fields[TextCorpus.Body].Keys];
        // Frequent terms make the lists long enough for blocks to matter; mix with rare ones.
        byte[][] frequent = [.. corpus.Fields[TextCorpus.Body].OrderByDescending(static kv => kv.Value.Count).Take(25).Select(static kv => kv.Key)];
        var rng = new Random(seed);
        float avg = (float)field.Statistics.SumTotalTermFreq / field.Statistics.DocCount;
        long exhaustiveDocs = 0;
        long wandDocs = 0;
        long wandNoBlockDocs = 0;
        int queries = 0;
        foreach (int termCount in new[] { 1, 2, 5, 5 })
        {
            for (int q = 0; q < 60; q++)
            {
                var distinct = new HashSet<string>();
                var picked = new List<byte[]>();
                while (picked.Count < termCount)
                {
                    byte[] t = rng.Next(3) == 0 ? terms[rng.Next(terms.Length)] : frequent[rng.Next(frequent.Length)];
                    if (distinct.Add(Convert.ToBase64String(t)))
                    {
                        picked.Add(t);
                    }
                }

                Scorer[] Build(PostingsFeatures f)
                {
                    var clauses = new Scorer[picked.Count];
                    for (int i = 0; i < picked.Count; i++)
                    {
                        Assert.True(field.TryGetTerm(picked[i], out TermInfo info));
                        clauses[i] = new PostingsScorer(field.GetPostings(info, f), field, Bm25Like(Weight(info.DocFreq, docs), avg));
                    }

                    return clauses;
                }

                // Exhaustive reference: score every matching document, adding clause scores in clause order.
                var perClause = new Dictionary<int, float>[picked.Count];
                var union = new SortedSet<int>();
                for (int i = 0; i < picked.Count; i++)
                {
                    Assert.True(field.TryGetTerm(picked[i], out TermInfo info));
                    Scorer c = new PostingsScorer(field.GetPostings(info, PostingsFeatures.Frequencies), field, Bm25Like(Weight(info.DocFreq, docs), avg));
                    perClause[i] = [];
                    for (int d = c.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = c.NextDoc())
                    {
                        perClause[i][d] = c.Score();
                        union.Add(d);
                    }
                }

                var expected = new SortedDictionary<int, float>();
                foreach (int d in union)
                {
                    float sum = 0f;
                    foreach (Dictionary<int, float> m in perClause)
                    {
                        if (m.TryGetValue(d, out float v))
                        {
                            sum += v;
                        }
                    }

                    expected[d] = sum;
                }

                var exhaustiveTop = expected.Select(static kv => (Doc: kv.Key, Score: kv.Value)).OrderByDescending(static x => x.Score).ThenBy(static x => x.Doc).Take(k).ToList();
                exhaustiveDocs += union.Count;

                foreach (bool blockMax in new[] { true, false })
                {
                    var wand = new BlockMaxWandScorer(Build(PostingsFeatures.Frequencies)) { UseBlockMax = blockMax };
                    List<(int Doc, float Score)> top = TopK(wand, k);
                    Assert.Equal(exhaustiveTop.Count, top.Count);
                    for (int i = 0; i < top.Count; i++)
                    {
                        Assert.Equal(exhaustiveTop[i].Doc, top[i].Doc);
                        Assert.Equal(exhaustiveTop[i].Score, top[i].Score);
                    }

                    if (blockMax)
                    {
                        wandDocs += wand.DocumentsReturned;
                    }
                    else
                    {
                        wandNoBlockDocs += wand.DocumentsReturned;
                    }
                }

                queries++;
            }
        }

        Assert.Equal(240, queries);
        Assert.True(wandDocs <= wandNoBlockDocs, "block bounds should never evaluate more documents than whole-list bounds");
        Assert.True(wandDocs < exhaustiveDocs);
        System.Diagnostics.Trace.WriteLine($"wand seed={seed} k={k}: queries={queries} exhaustiveDocs={exhaustiveDocs} wandPlain={wandNoBlockDocs} wandBlockMax={wandDocs} fraction={wandDocs / (double)exhaustiveDocs:P1}");
        WandReport.Add($"seed={seed} docs={docs} k={k}: {queries} queries, documents scored: exhaustive {exhaustiveDocs}, WAND {wandNoBlockDocs}, block-max WAND {wandDocs} ({wandDocs / (double)exhaustiveDocs:P1})");
    }

    private static List<(int Doc, float Score)> TopK(Scorer scorer, int k)
    {
        // Sorted ascending: index 0 is the worst kept entry (lowest score; among equals, the highest document id).
        var heap = new List<(int Doc, float Score)>();
        for (int d = scorer.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = scorer.NextDoc())
        {
            float score = scorer.Score();
            if (heap.Count < k)
            {
                heap.Add((d, score));
                heap.Sort(Worse);
            }
            else if (Worse((d, score), heap[0]) > 0)
            {
                heap[0] = (d, score);
                heap.Sort(Worse);
            }

            if (heap.Count == k)
            {
                scorer.SetMinCompetitiveScore(heap[0].Score);
            }
        }

        return [.. heap.OrderByDescending(static x => x.Score).ThenBy(static x => x.Doc)];
    }

    // Positive when a is better than b.
    private static int Worse((int Doc, float Score) a, (int Doc, float Score) b)
        => a.Score != b.Score ? a.Score.CompareTo(b.Score) : b.Doc.CompareTo(a.Doc);

    [Fact]
    public void WandAdvanceMatchesExhaustiveAdvance()
    {
        var corpus = TextCorpus.Generate(9, 5000, 500);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        ITextFieldReader field = reader.GetTextField(TextCorpus.Body)!;
        byte[][] frequent = [.. corpus.Fields[TextCorpus.Body].OrderByDescending(static kv => kv.Value.Count).Take(6).Select(static kv => kv.Key)];
        Scorer[] Make() => [.. frequent.Take(3).Select(t =>
        {
            field.TryGetTerm(t, out TermInfo info);
            return (Scorer)new PostingsScorer(field.GetPostings(info, PostingsFeatures.Frequencies), field, Bm25Like(1f, 10f));
        })];
        var a = new BlockMaxWandScorer(Make());
        var b = new BlockMaxWandScorer(Make()) { UseBlockMax = false };
        var rng = new Random(2);
        int doc = -1;
        while (doc != DocIdSetIterator.NoMoreDocs)
        {
            int target = doc + 1 + rng.Next(50);
            int da = a.Advance(target);
            int db = b.Advance(target);
            Assert.Equal(db, da);
            if (da != DocIdSetIterator.NoMoreDocs)
            {
                Assert.Equal(b.Score(), a.Score());
            }

            doc = da;
        }
    }

    [Fact]
    public void HotPathsAllocateNothing()
    {
        var corpus = TextCorpus.Generate(31, 3000, 600);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        ITextFieldReader field = reader.GetTextField(TextCorpus.Body)!;
        byte[][] terms = [.. corpus.Fields[TextCorpus.Body].Keys.Take(200)];
        byte[] top = corpus.Fields[TextCorpus.Body].OrderByDescending(static kv => kv.Value.Count).First().Key;
        field.TryGetTerm(top, out TermInfo info);
        var postings = (TermPostings)field.GetPostings(info, PostingsFeatures.Offsets);

        // Warm up (JIT) then measure.
        for (int i = 0; i < 3; i++)
        {
            Lookup(field, terms);
            Iterate(postings, info);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        long lookups = 0;
        for (int i = 0; i < 50; i++)
        {
            lookups += Lookup(field, terms);
        }

        long afterLookups = GC.GetAllocatedBytesForCurrentThread();
        long docsRead = 0;
        for (int i = 0; i < 50; i++)
        {
            docsRead += Iterate(postings, info);
        }

        long afterPostings = GC.GetAllocatedBytesForCurrentThread();
        long beforeCreate = GC.GetAllocatedBytesForCurrentThread();
        _ = field.GetPostings(info, PostingsFeatures.Offsets);
        long perIterator = GC.GetAllocatedBytesForCurrentThread() - beforeCreate;
        Assert.True(lookups >= 50 * 200);
        Assert.Equal(50 * 3000, docsRead);
        Assert.Equal(0, afterLookups - before);
        Assert.Equal(0, afterPostings - afterLookups);
        System.Diagnostics.Trace.WriteLine($"allocation: {lookups} lookups 0 bytes, {docsRead} documents with positions and offsets 0 bytes, creating one iterator {perIterator} bytes");
        WandReport.Add($"allocation: {lookups} term lookups and {docsRead} postings iterated with positions and offsets allocated 0 bytes; creating an iterator {perIterator} bytes");
    }

    private static long Lookup(ITextFieldReader field, byte[][] terms)
    {
        long found = 0;
        foreach (byte[] t in terms)
        {
            if (field.TryGetTerm(t, out TermInfo _))
            {
                found++;
            }
        }

        return found;
    }

    private static long Iterate(TermPostings postings, TermInfo info)
    {
        postings.Reset(info, PostingsFeatures.Offsets);
        long docs = 0;
        long sum = 0;
        for (int d = postings.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = postings.NextDoc())
        {
            docs++;
            int freq = postings.Frequency;
            for (int k = 0; k < freq; k++)
            {
                sum += postings.NextPosition() + postings.StartOffset + postings.EndOffset;
            }
        }

        return sum >= 0 ? docs : -1;
    }

    [Fact]
    public void DamagedFilesRaiseOnlyTypedExceptions()
    {
        // A seeded mini-fuzz: flip bytes, truncate, splice, with and without repairing the footer, in each of the two files.
        var rng = new Random(77);
        int runs = 0;
        foreach (bool terms in new[] { true, false })
        {
            var seedDir = Path.Combine(TestHelpers.NewTempDirectory(), "seeds");
            Directory.CreateDirectory(seedDir);
            try
            {
                TextIndexTarget.WriteSeeds(seedDir, terms);
                byte[] valid = File.ReadAllBytes(Path.Combine(seedDir, "valid"));
                for (int i = 0; i < 1500; i++)
                {
                    byte[] input = [.. valid];
                    int edits = 1 + rng.Next(4);
                    for (int e = 0; e < edits; e++)
                    {
                        int p = 1 + rng.Next(input.Length - 1);
                        switch (rng.Next(4))
                        {
                            case 0:
                                input[p] ^= (byte)(1 << rng.Next(8));
                                break;
                            case 1:
                                input[p] = (byte)rng.Next(256);
                                break;
                            case 2:
                                input[p] = 0xFF;
                                break;
                            default:
                                input[p] = 0x80;
                                break;
                        }
                    }

                    if (rng.Next(10) == 0)
                    {
                        input = input[..rng.Next(1, input.Length)];
                    }

                    input[0] = (byte)(rng.Next(4) == 0 ? 0 : 1);
                    if (terms)
                    {
                        TextIndexTarget.RunTerms(input);
                    }
                    else
                    {
                        TextIndexTarget.RunPostings(input);
                    }

                    runs++;
                }

                // The undamaged input passes through untouched.
                if (terms)
                {
                    TextIndexTarget.RunTerms(valid);
                }
                else
                {
                    TextIndexTarget.RunPostings(valid);
                }
            }
            finally
            {
                Directory.Delete(Path.GetDirectoryName(seedDir)!, recursive: true);
            }
        }

        Assert.Equal(3000, runs);
    }

    [Fact]
    public void HostileTermStatisticsAreRefused()
    {
        var corpus = TextCorpus.Generate(5, 200, 100);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        ITextFieldReader field = reader.GetTextField(TextCorpus.Body)!;
        field.TryGetTerm(corpus.Fields[TextCorpus.Body].Keys.First(), out TermInfo good);
        foreach (TermInfo bad in new[]
        {
            good with { DocFreq = 0 },
            good with { DocFreq = int.MaxValue },
            good with { TotalTermFreq = 0 },
            good with { PostingsOffset = -5 },
            good with { PostingsOffset = long.MaxValue },
            good with { PostingsOffset = 1 },
        })
        {
            Assert.Throws<CorruptIndexException>(() => field.GetPostings(bad, PostingsFeatures.Frequencies));
        }

        Assert.Throws<ArgumentException>(() => reader.GetTextField(TextCorpus.Title)!.GetPostings(default, PostingsFeatures.Offsets));
    }
}

internal static class WandReport
{
    private static readonly object Gate = new();

    public static void Add(string line)
    {
        string? path = Environment.GetEnvironmentVariable("ONEINDEX_TEST_REPORT");
        if (path is null)
        {
            return;
        }

        lock (Gate)
        {
            File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
        }
    }
}
