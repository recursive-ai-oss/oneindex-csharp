using System.Text;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Samples.ReadsLike;

// The code of docs/reads-like.md: each OneIndex layer next to the .NET API it is shaped after. Compiled and run by the sample tests.

public static class ReadsLikeExamples
{
    /// <summary>The BCL's reader: a forward-only loop over tokens that never allocates for what it skips.</summary>
    public static double SumPrices(ReadOnlySpan<byte> json)
    {
        double total = 0;
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("price"u8))
            {
                reader.Read();
                total += reader.GetDouble();
            }
        }

        return total;
    }

    /// <summary>The same shape one layer down: a forward-only loop over the terms of a field and, for each, over its postings. The terms start with <paramref name="prefix"/>.</summary>
    public static List<string> WalkTerms(ITextFieldReader field, string prefix)
    {
        var found = new List<string>();
        ITermEnumerator terms = field.GetTerms(Encoding.UTF8.GetBytes(prefix));
        while (terms.MoveNext())
        {
            TermInfo info = terms.Info;
            long occurrences = 0;
            PostingsIterator postings = field.GetPostings(info, PostingsFeatures.Frequencies);
            while (postings.NextDoc() != DocIdSetIterator.NoMoreDocs)
            {
                occurrences += postings.Frequency;
            }

            found.Add($"{Encoding.UTF8.GetString(terms.Term)}: {info.DocFreq} documents, {occurrences} occurrences");
        }

        return found;
    }

    /// <summary>Builds a one-segment index of three short documents and walks the terms of its title field.</summary>
    public static List<string> WalkTitleTerms()
    {
        Schema schema = new SchemaBuilder().Id("id").Text("title").Build();
        using var writer = IndexWriter.Open(new MemoryStorage(), schema);
        writer.Upsert(new Document().Add("id", "1").Add("title", "head over heels"));
        writer.Upsert(new Document().Add("id", "2").Add("title", "headphones and headsets"));
        writer.Upsert(new Document().Add("id", "3").Add("title", "head head"));
        writer.Commit();
        using IndexSnapshot snapshot = writer.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        return WalkTerms(context.Leaves[0].Reader.GetTextField("title") ?? throw new InvalidOperationException("no title field"), "head");
    }
}
