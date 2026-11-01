using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>
/// Scoring documents with the proximity bonus allocates nothing once the buffers are warm. (A term of several blocks makes its postings
/// iterator build a block table after a few block reads, once; the warm-up covers it.)
/// </summary>
[Collection(AllocationMeasurement.Name)]
public sealed class ProximityAllocationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScoringWithProximityAllocatesNothingPerDocument(bool withProximity)
    {
        string[] vocabulary = ["red", "apple", "pie", "big", "tree", "green", "farm", "juicy"];
        var rng = new Random(5);
        Schema schema = new SchemaBuilder().Id("id").Add(new FieldDefinition("t", FieldType.Text) { Analyzer = "whitespace-lowercase" }).Build();
        var storage = new MemoryStorage();
        const int docs = 9000;
        using (IndexWriter w = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 100_000, AutoMerge = false }))
        {
            for (int i = 0; i < docs; i++)
            {
                string text = string.Join(' ', Enumerable.Range(0, 12).Select(_ => vocabulary[rng.Next(vocabulary.Length)]));
                w.Upsert(new Document().Add("id", "d" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).Add("t", text));
            }

            w.Commit();
        }

        using IndexSnapshot snap = IndexSnapshot.Open(storage);
        SearchContext context = SnapshotSearch.CreateContext(snap);
        string[] words = ["red", "apple", "tree"];
        Search.Query inner = new BooleanQuery([.. words.Select(x => new BooleanClause(new TermQuery("t", x), Occur.Should))]);
        Search.Query query = !withProximity ? inner : new ProximityQuery(inner, [.. words.Select(x => (Search.Query)new TermQuery("t", x))]) { Weight = 1f };
        Weight weight = new QueryEngine().CreateWeight(query, context, ScoreMode.Complete);
        Scorer scorer = weight.GetScorer(context.Leaves[0])!;
        float sink = 0;
        int scored = 0;
        int windows = TestHelpers.AssertNoAllocation("proximity score", () =>
        {
            for (int i = 0; i < 60; i++)
            {
                if (scorer.NextDoc() == DocIdSetIterator.NoMoreDocs)
                {
                    Assert.Fail("the corpus ran out before the measurement did");
                }

                sink += scorer.Score();
                scored++;
            }
        }, windows: 30);
        Assert.True(sink > 0 && scored > 0);
        output.WriteLine($"0 bytes over {windows} windows of 60 scored documents");
    }
}
