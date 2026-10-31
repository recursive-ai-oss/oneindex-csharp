using System.Text;
using OneIndex.Codecs;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class TextIndexTests
{
    private static int _cases;

    internal static void Verify(TextCorpus corpus, TextSegmentReader reader, int seed)
    {
        var rng = new Random(seed);
        Assert.Equal(corpus.MaxDoc, reader.MaxDoc);
        var expectedFields = corpus.Fields.Where(static f => f.Value.Count > 0).Select(static f => f.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expectedFields, reader.FieldNames.ToList());
        foreach (string field in expectedFields)
        {
            ITextFieldReader fr = reader.GetTextField(field)!;
            PostingsFeatures features = TextCorpus.FeaturesOf(field);
            Assert.Equal(features, fr.IndexedFeatures);
            SortedDictionary<byte[], List<TextCorpus.DocPostings>> terms = corpus.Fields[field];
            long sumTtf = 0;
            long sumDf = 0;
            foreach (List<TextCorpus.DocPostings> l in terms.Values)
            {
                sumDf += l.Count;
                sumTtf += l.Sum(static d => d.Occurrences.Count);
            }

            Assert.Equal(new FieldStatistics(corpus.Lengths[field].Count(static x => x > 0), sumTtf, sumDf), fr.Statistics);
            for (int d = 0; d < corpus.MaxDoc; d++)
            {
                Assert.Equal(corpus.Lengths[field][d], fr.GetFieldLength(d));
            }

            // Ordered iteration equals the sorted term list, and each term's statistics match.
            using (ITermEnumerator e = fr.GetTerms(default))
            {
                foreach ((byte[] term, List<TextCorpus.DocPostings> postings) in terms)
                {
                    Assert.True(e.MoveNext());
                    Assert.True(e.Term.SequenceEqual(term));
                    Assert.Equal(postings.Count, e.Info.DocFreq);
                    Assert.Equal(postings.Sum(static p => (long)p.Occurrences.Count), e.Info.TotalTermFreq);
                }

                Assert.False(e.MoveNext());
            }

            int checkedTerms = 0;
            foreach ((byte[] term, List<TextCorpus.DocPostings> postings) in terms)
            {
                // Every term is read in full for the small and the sampled; large corpora check a random third in full.
                bool full = terms.Count < 400 || rng.Next(3) == 0 || postings.Count > 100;
                Assert.True(fr.TryGetTerm(term, out TermInfo info));
                Assert.Equal(postings.Count, info.DocFreq);
                if (full)
                {
                    VerifyPostings(fr, info, postings, features, rng);
                    checkedTerms++;
                }
            }

            _ = checkedTerms;
            // Absent terms are absent, including prefixes and extensions of present ones.
            for (int i = 0; i < 200; i++)
            {
                byte[] t = [.. terms.Keys.ElementAt(rng.Next(terms.Count))];
                byte[] absent = rng.Next(3) switch
                {
                    0 => [.. t, (byte)'~'],
                    1 => t.Length > 1 ? t[..^1] : [(byte)'#'],
                    _ => [(byte)'!', .. t],
                };
                Assert.Equal(terms.ContainsKey(absent), fr.TryGetTerm(absent, out _));
            }

            Assert.Equal(terms.ContainsKey([]), fr.TryGetTerm([], out _));
        }
    }

    private static void VerifyPostings(ITextFieldReader fr, TermInfo info, List<TextCorpus.DocPostings> expected, PostingsFeatures features, Random rng)
    {
        bool positions = (features & PostingsFeatures.Positions) == PostingsFeatures.Positions;
        bool offsets = (features & PostingsFeatures.Offsets) == PostingsFeatures.Offsets;

        // Documents and frequencies only.
        PostingsIterator it = fr.GetPostings(info, PostingsFeatures.Frequencies);
        Assert.Equal(-1, it.DocId);
        foreach (TextCorpus.DocPostings dp in expected)
        {
            Assert.Equal(dp.Doc, it.NextDoc());
            Assert.Equal(dp.Occurrences.Count, it.Frequency);
        }

        Assert.Equal(DocIdSetIterator.NoMoreDocs, it.NextDoc());

        // Positions and offsets, reading them for some documents and skipping others.
        if (positions)
        {
            it = fr.GetPostings(info, offsets ? PostingsFeatures.Offsets : PostingsFeatures.Positions);
            foreach (TextCorpus.DocPostings dp in expected)
            {
                Assert.Equal(dp.Doc, it.NextDoc());
                if (rng.Next(3) == 0)
                {
                    continue;
                }

                Assert.Equal(dp.Occurrences.Count, it.Frequency);
                int toRead = rng.Next(4) == 0 ? rng.Next(dp.Occurrences.Count + 1) : dp.Occurrences.Count;
                for (int k = 0; k < toRead; k++)
                {
                    Assert.Equal(dp.Occurrences[k].Position, it.NextPosition());
                    if (offsets)
                    {
                        Assert.Equal(dp.Occurrences[k].Start, it.StartOffset);
                        Assert.Equal(dp.Occurrences[k].End, it.EndOffset);
                    }
                }
            }
        }

        // Advance to random targets, with positions checked where it lands.
        for (int round = 0; round < 3; round++)
        {
            it = fr.GetPostings(info, positions ? PostingsFeatures.Positions : PostingsFeatures.Frequencies);
            int index = -1;
            int doc = -1;
            while (true)
            {
                int target = doc + 1 + (rng.Next(3) == 0 ? rng.Next(400) : rng.Next(3));
                int want = expected.FindIndex(Math.Max(index, 0), p => p.Doc >= target);
                int got = rng.Next(5) == 0 && doc >= 0 && want == index + 1 ? it.NextDoc() : it.Advance(target);
                if (want < 0)
                {
                    Assert.Equal(DocIdSetIterator.NoMoreDocs, got);
                    break;
                }

                Assert.Equal(expected[want].Doc, got);
                Assert.Equal(expected[want].Occurrences.Count, it.Frequency);
                if (positions && rng.Next(2) == 0)
                {
                    for (int k = 0; k < expected[want].Occurrences.Count; k++)
                    {
                        Assert.Equal(expected[want].Occurrences[k].Position, it.NextPosition());
                    }
                }

                index = want;
                doc = got;
            }
        }

        // Impacts bound every document of their block.
        BlockImpact term = it.TermImpact;
        Assert.Equal(expected[^1].Doc, term.LastDocId);
        Assert.Equal(expected.Max(static p => p.Occurrences.Count), term.MaxFrequency);
        it = fr.GetPostings(info, PostingsFeatures.Frequencies);
        for (int i = 0; i < 5; i++)
        {
            int target = rng.Next(expected[^1].Doc + 1);
            BlockImpact b = it.ShallowAdvance(target);
            int first = expected.FindIndex(p => p.Doc >= target);
            Assert.True(b.LastDocId >= expected[first].Doc);
            Assert.True(b.MaxFrequency >= expected[first].Occurrences.Count);
            for (int j = first; j < expected.Count && expected[j].Doc <= b.LastDocId; j++)
            {
                Assert.True(b.MaxFrequency >= expected[j].Occurrences.Count);
                Assert.True(b.MinFieldLength >= 1);
            }
        }

        Assert.Equal(DocIdSetIterator.NoMoreDocs, it.ShallowAdvance(expected[^1].Doc + 1).LastDocId);
    }

    [Theory]
    [InlineData(1, 1, 5)]
    [InlineData(2, 40, 30)]
    [InlineData(3, 700, 400)]
    [InlineData(4, 3000, 1500)]
    [InlineData(5, 300, 3000)]
    public void RoundTripsSeededCorpora(int seed, int docs, int vocabulary)
    {
        var corpus = TextCorpus.Generate(seed, docs, vocabulary);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        reader.VerifyPostingsChecksum();
        Verify(corpus, reader, seed);
        Interlocked.Add(ref _cases, 1);
    }

    [Fact]
    public void RoundTripsThroughTheFileSystemAndAsyncOpen()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            var storage = new FileSystemStorage(dir);
            var corpus = TextCorpus.Generate(11, 500, 600);
            corpus.Write(storage);
            using TextSegmentReader reader = TextSegmentReader.OpenAsync(storage, "seg.terms", "seg.postings", "seg.lengths").AsTask().GetAwaiter().GetResult();
            Verify(corpus, reader, 11);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void WrittenBytesAreDeterministic()
    {
        var a = new MemoryStorage();
        var b = new MemoryStorage();
        TextSegmentFiles fa = TextCorpus.Generate(7, 400, 500).Write(a);
        TextSegmentFiles fb = TextCorpus.Generate(7, 400, 500).Write(b);
        Assert.Equal(fa, fb);
        foreach (string name in new[] { "seg.terms", "seg.postings", "seg.lengths" })
        {
            Assert.True(TestHelpers.ReadAll(a, name).AsSpan().SequenceEqual(TestHelpers.ReadAll(b, name)));
        }
    }

    [Fact]
    public void SingleDocumentAndHugeDocumentFrequencyTerms()
    {
        var corpus = TextCorpus.Generate(21, 2500, 50);
        var storage = new MemoryStorage();
        corpus.Write(storage);
        using TextSegmentReader reader = TextCorpus.Open(storage);
        ITextFieldReader body = reader.GetTextField(TextCorpus.Body)!;
        // The most common term is in every body document: a list of 2,500 documents is 20 blocks.
        byte[] top = corpus.Fields[TextCorpus.Body].OrderByDescending(static kv => kv.Value.Count).First().Key;
        Assert.True(body.TryGetTerm(top, out TermInfo info));
        Assert.Equal(2500, info.DocFreq);
    }

    [Fact]
    public void EmptyAndWhitespaceAnalyzedDocumentsThroughTheAnalyzerPath()
    {
        var analyzer = Analysis.AnalyzerRegistry.Create("whitespace-lowercase");
        var buffer = new InvertedBuffer();
        buffer.DeclareField("t", PostingsFeatures.Offsets);
        string[] docs = ["Hello World hello", "", "world of WORLD", "Ünïcode ünïcode"];
        for (int d = 0; d < docs.Length; d++)
        {
            buffer.AddField(d, "t", analyzer.GetTokens(docs[d].AsMemory()));
        }

        var storage = new MemoryStorage();
        using (var writer = new TextSegmentWriter(storage, "x.terms", "x.postings", "x.lengths", docs.Length))
        {
            buffer.WriteTo(writer);
            writer.Finish();
        }

        using TextSegmentReader r = TextSegmentReader.Open(storage, "x.terms", "x.postings", "x.lengths");
        ITextFieldReader f = r.GetTextField("t")!;
        Assert.Equal(3, f.GetFieldLength(0));
        Assert.Equal(0, f.GetFieldLength(1));
        Assert.Equal(3, f.GetFieldLength(2));
        Assert.True(f.TryGetTerm("world"u8, out TermInfo world));
        Assert.Equal(2, world.DocFreq);
        Assert.Equal(3, world.TotalTermFreq);
        PostingsIterator it = f.GetPostings(world, PostingsFeatures.Offsets);
        Assert.Equal(0, it.NextDoc());
        Assert.Equal(1, it.NextPosition());
        Assert.Equal(6, it.StartOffset);
        Assert.Equal(11, it.EndOffset);
        Assert.Equal(2, it.NextDoc());
        Assert.Equal(0, it.NextPosition());
        Assert.Equal(2, it.NextPosition());
        Assert.True(f.TryGetTerm(Encoding.UTF8.GetBytes("ünïcode"), out TermInfo u));
        Assert.Equal(2, u.TotalTermFreq);
    }

    [Fact]
    public void MultiValuedFieldsKeepAPositionGap()
    {
        var analyzer = Analysis.AnalyzerRegistry.Create("whitespace-lowercase");
        var buffer = new InvertedBuffer { PositionGap = 10 };
        buffer.AddField(0, "t", analyzer.GetTokens("a b".AsMemory()));
        buffer.AddField(0, "t", analyzer.GetTokens("c a".AsMemory()));
        var storage = new MemoryStorage();
        using (var writer = new TextSegmentWriter(storage, "x.terms", "x.postings", "x.lengths", 1))
        {
            buffer.WriteTo(writer);
            writer.Finish();
        }

        using TextSegmentReader r = TextSegmentReader.Open(storage, "x.terms", "x.postings", "x.lengths");
        ITextFieldReader f = r.GetTextField("t")!;
        Assert.Equal(4, f.GetFieldLength(0));
        f.TryGetTerm("a"u8, out TermInfo a);
        PostingsIterator it = f.GetPostings(a, PostingsFeatures.Positions);
        it.NextDoc();
        Assert.Equal(0, it.NextPosition());
        Assert.Equal(13, it.NextPosition());
    }

    [Fact]
    public void WriterRefusesBadInput()
    {
        var storage = new MemoryStorage();
        using var writer = new TextSegmentWriter(storage, "x.terms", "x.postings", "x.lengths", 10);
        writer.BeginField("f", PostingsFeatures.Positions, new int[10]);
        writer.BeginTerm("b"u8);
        writer.AddDocument(3, 1);
        writer.AddPosition(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddDocument(3, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => writer.AddDocument(10, 1));
        writer.AddDocument(4, 2);
        writer.AddPosition(0);
        Assert.Throws<InvalidOperationException>(() => writer.EndTerm()); // the document promised a second position
        writer.BeginTerm("b"u8);
        writer.AddDocument(3, 1);
        writer.AddPosition(0);
        Assert.Throws<InvalidOperationException>(() => writer.AddPosition(1));
        writer.EndTerm();
        writer.BeginTerm("a"u8);
        writer.AddDocument(1, 1);
        writer.AddPosition(0);
        Assert.Throws<ArgumentException>(() => writer.EndTerm()); // out of order
    }

    [Fact]
    public void LongTermsAreSkippedByTheBufferAndRefusedByTheWriter()
    {
        var buffer = new InvertedBuffer();
        buffer.AddToken(0, "f", new byte[40_000], 0);
        buffer.AddToken(0, "f", "ok"u8, 1);
        Assert.Equal(1, buffer.SkippedTokens);
        var storage = new MemoryStorage();
        using var writer = new TextSegmentWriter(storage, "x.terms", "x.postings", "x.lengths", 1);
        writer.BeginField("f", PostingsFeatures.Positions, new int[1]);
        Assert.Throws<ArgumentException>(() => writer.BeginTerm(new byte[40_000]));
    }
}
