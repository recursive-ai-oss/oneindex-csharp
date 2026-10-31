using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class TextCodecIntegrationTests
{
    private static readonly Schema TestSchema = new SchemaBuilder()
        .Id("id")
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase", IndexOptions = IndexOptions.Positions })
        .Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "whitespace-lowercase", IndexOptions = IndexOptions.Freqs })
        .Build();

    [Fact]
    public void FlushMergeAndReopenThroughTheIndexWriter()
    {
        var storage = new MemoryStorage();
        var options = new IndexWriterOptions { Codecs = [new TextSegmentCodec()], MaxBufferedDocuments = 50, AutoMerge = false };
        var expected = new Dictionary<string, List<string>>(); // id -> body tokens
        using (IndexWriter writer = IndexWriter.Open(storage, TestSchema, options))
        {
            var rng = new Random(3);
            string[] words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta"];
            for (int i = 0; i < 400; i++)
            {
                string line = string.Join(' ', Enumerable.Range(0, 1 + rng.Next(12)).Select(_ => words[rng.Next(words.Length)]));
                string id = "d" + i;
                writer.Upsert(new Document().Add("id", id).Add("body", line).Add("title", words[i % words.Length]));
                expected[id] = [.. line.Split(' ')];
            }

            writer.Commit();
            for (int i = 0; i < 400; i += 3)
            {
                writer.DeleteById("d" + i);
                expected.Remove("d" + i);
            }

            writer.Commit();
            writer.ForceMerge(1);
            writer.Commit();
        }

        using IndexSnapshot snapshot = IndexSnapshot.Open(storage, [new TextSegmentCodec()]);
        Assert.Equal(1, snapshot.SegmentCount);
        SegmentReader segment = snapshot.GetSegment(0);
        ITextFieldSource text = segment.GetPart<ITextFieldSource>()!;
        ITextFieldReader body = text.GetTextField("body")!;
        ITextFieldReader title = text.GetTextField("title")!;
        Assert.Equal(PostingsFeatures.Positions, body.IndexedFeatures);
        Assert.Equal(PostingsFeatures.Frequencies, title.IndexedFeatures);

        // The merged segment keeps the deleted documents' slots only if the merge copied them; a forced merge drops them, so its
        // postings hold exactly the surviving documents.
        Assert.Equal(expected.Count, segment.MaxDoc);
        long occurrences = 0;
        foreach (string word in new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta" })
        {
            Assert.True(body.TryGetTerm(System.Text.Encoding.UTF8.GetBytes(word), out TermInfo info));
            PostingsIterator it = body.GetPostings(info, PostingsFeatures.Positions);
            int docs = 0;
            for (int d = it.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
            {
                docs++;
                occurrences += it.Frequency;
                for (int k = 0; k < it.Frequency; k++)
                {
                    it.NextPosition();
                }
            }

            Assert.Equal(info.DocFreq, docs);
        }

        Assert.Equal(expected.Values.Sum(static b => b.Count), occurrences);
        Assert.Equal(expected.Count, body.Statistics.DocCount);
        snapshot.Verify();
    }
}
