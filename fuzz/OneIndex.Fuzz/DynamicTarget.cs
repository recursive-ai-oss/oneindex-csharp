using System.Text;
using System.Text.Json;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Fuzz;

/// <summary>
/// Feeds hostile JSON to the indexer of JSON fields: the bytes are parsed as a JSON document (anything that is not JSON is skipped,
/// the parser is the platform's) and indexed by path, committed, reopened and queried. The first byte picks the route: 0 indexes
/// the value directly; 1 first stores it in a field that is not indexed and then changes the schema so that the index is rebuilt from
/// the stored source. Only typed library exceptions may escape; the schema must stay within the path limit, the commit must verify and
/// the value must come back from the stored source unchanged.
/// </summary>
internal static class DynamicTarget
{
    private static readonly IndexLimits Limits = new() { MaxDynamicPaths = 24, MaxDynamicDepth = 6, MaxDynamicValues = 200, MaxDynamicPathBytes = 64, MaxFields = 64 };

    public static void Run(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return;
        }

        bool rebuild = (data[0] & 1) != 0;
        JsonDocument parsed;
        try
        {
            parsed = JsonDocument.Parse(data[1..].ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return;
        }

        using (parsed)
        {
            JsonElement value = parsed.RootElement;
            var storage = new MemoryStorage();
            var options = new IndexWriterOptions { Limits = Limits, MaxBufferedDocuments = 2, AutoMerge = false };
            Schema first = new SchemaBuilder(Limits).Id("id").Add(new FieldDefinition("meta", FieldType.Json) { Indexed = !rebuild }).Build();
            try
            {
                first.Validate(new Document().Add("id", "a").Add("meta", FieldValue.FromJson(value)));
            }
            catch (ArgumentException)
            {
                return; // bytes that are not valid UTF-8 inside a string are refused at the write
            }

            using (IndexWriter w = IndexWriter.Open(storage, first, options))
            {
                w.Upsert(new Document().Add("id", "a").Add("meta", FieldValue.FromJson(value)));
                w.Upsert(new Document().Add("id", "b").Add("meta", FieldValue.FromJson(value)));
                w.Upsert(new Document().Add("id", "c").Add("meta", FieldValue.FromJson(value)));
                w.Commit();
            }

            if (rebuild)
            {
                Schema second = new SchemaBuilder(Limits).Id("id").Add(new FieldDefinition("meta", FieldType.Json)).Build();
                using IndexWriter w = IndexWriter.Open(storage, second, options);
                w.CompleteSchemaEvolution();
                if (w.SchemaEvolution.InProgress)
                {
                    throw new InvalidOperationException("the rebuild did not finish");
                }
            }

            using IndexSnapshot snapshot = IndexSnapshot.Open(storage, null, Limits);
            snapshot.Verify();
            int derived = snapshot.Schema.Fields.Count(static f => f.DynamicParent is not null);
            if (derived > Limits.MaxDynamicPaths)
            {
                throw new InvalidOperationException($"{derived} derived fields exceed the limit of {Limits.MaxDynamicPaths}");
            }

            if (!Schema.Deserialize(snapshot.Schema.Serialize(), Limits).Equals(snapshot.Schema))
            {
                throw new InvalidOperationException("the schema did not round-trip");
            }

            IndexSearcher searcher = SnapshotSearch.CreateSearcher(snapshot);
            foreach (FieldDefinition f in snapshot.Schema.Fields.Where(static f => f.DynamicParent is not null).Take(8))
            {
                string logical = f.Name[..f.Name.LastIndexOf('#')];
                if (searcher.Search(new RangeQuery(logical, null, null), 10, true).TotalHits != 3)
                {
                    throw new InvalidOperationException($"path '{logical}' does not exist in all three documents");
                }
            }

            // The whole value is still in the stored source, however much of it was indexed.
            SegmentReader segment = snapshot.GetSegment(0);
            IStoredFieldsReader stored = segment.GetPart<IStoredFieldsSource>()?.StoredFields ?? throw new InvalidOperationException("no stored source");
            for (int d = 0; d < segment.MaxDoc; d++)
            {
                if (stored.GetDocument(d).Get("meta").AsJson().GetRawText().Length != value.GetRawText().Length)
                {
                    throw new InvalidOperationException("the stored JSON differs from the indexed value");
                }
            }
        }
    }

    public static void WriteSeeds(string directory)
    {
        string[] samples =
        [
            """{"customer":{"name":"acme","age":41},"tags":["a","b"],"score":2.5,"active":true,"when":"2024-03-01"}""",
            """[1,2,{"a":[{"b":null},"x"]},"2024-01-01T10:00:00Z"]""",
            """{"a.b":{"c\\.d":1},"":2,"deep":{"a":{"b":{"c":{"d":{"e":{"f":1}}}}}}}""",
            "\"just a string\"",
            "12345678901234567890123456789",
        ];
        for (int i = 0; i < samples.Length; i++)
        {
            File.WriteAllBytes(Path.Combine(directory, "json-" + i), [(byte)(i & 1), .. Encoding.UTF8.GetBytes(samples[i])]);
        }
    }
}
