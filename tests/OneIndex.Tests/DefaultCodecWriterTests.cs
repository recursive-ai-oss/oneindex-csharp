using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class DefaultCodecWriterTests
{
    private static readonly Schema TestSchema = new SchemaBuilder().Id("id").Keyword("tag").Text("body").Integral("n").Build();

    [Fact]
    public void AddUniqueSkipsTheSegmentLookupButStillReplacesBufferedDuplicates()
    {
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 100, AutoMerge = false });
        writer.AddUnique(new Document().Add("id", "a").Add("tag", "x").Add("body", "one").Add("n", 1L));
        writer.AddUnique(new Document().Add("id", "a").Add("tag", "y").Add("body", "two").Add("n", 2L));
        writer.AddUnique(new Document().Add("id", "b").Add("tag", "x").Add("body", "three").Add("n", 3L));
        writer.Commit();
        Assert.Equal(2, writer.LiveDocumentCount);
    }

    [Fact]
    public void DeleteByTermReachesCommittedSegmentsOfTheDefaultCodecs()
    {
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 2, AutoMerge = false });
        for (int i = 0; i < 6; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("tag", i % 2 == 0 ? "even" : "odd").Add("body", "text " + i).Add("n", (long)i));
        }

        writer.Commit();
        Assert.Equal(3, writer.DeleteByTerm("tag", "even"));
        writer.Commit();
        Assert.Equal(3, writer.LiveDocumentCount);
    }

    [Fact]
    public void NumbersAreNotWrittenAsTermsByDefaultAndRangesStillAnswerFromPoints()
    {
        using IndexWriter writer = IndexWriter.Open(new MemoryStorage(), TestSchema, new IndexWriterOptions { MaxBufferedDocuments = 100, AutoMerge = false });
        for (int i = 0; i < 20; i++)
        {
            writer.Upsert(new Document().Add("id", "d" + i).Add("tag", "t").Add("body", "text").Add("n", (long)i));
        }

        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
        Assert.Null(searcher.Context.Leaves[0].Reader.GetTextField("n"));
        TopDocs top = searcher.Search(new RangeQuery("n", FieldValue.FromInt64(5), FieldValue.FromInt64(9)), 100, countExactly: true);
        Assert.Equal(5, top.TotalHits);
    }
}
