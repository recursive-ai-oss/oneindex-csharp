using OneIndex.Aggregations;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

/// <summary>Seeded random corpora (multi-valued and missing values, deletes, several segments, merged or not) for the aggregation, facet and distinct differentials.</summary>
internal sealed class AggCorpus : IDisposable
{
    public static readonly string[] Cats = ["red", "green", "blue", "Café", "cafe", "zebra", "日本"];
    public static readonly string[] TagPool = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "ß", "ss"];
    public static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "eps"];
    public static readonly double[] Prices = [-1.5, 0.0, -0.0, 0.5, 2, 7.25, 40, 1e-3, 12345.678];

    public sealed record Row(string Id, string[] Body, string? Cat, string[] Tags, long? N, long[] Ns, double? Price, long? When, bool? Flag);

    public static readonly Schema Schema = new SchemaBuilder().Id("id")
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "whitespace-lowercase" })
        .Add(new FieldDefinition("cat", FieldType.Keyword) { Facet = true })
        .Add(new FieldDefinition("tags", FieldType.Keyword) { Facet = true, Multi = true })
        .Integral("n")
        .Add(new FieldDefinition("ns", FieldType.Integral) { Column = true, Multi = true })
        .Fractional("price").Date("when").Bool("flag")
        .Build();

    private readonly IndexSnapshot _snapshot;

    private AggCorpus(IndexSnapshot snapshot, List<Row> live, Dictionary<string, int> globalIds, int segments, bool merged)
    {
        _snapshot = snapshot;
        Live = live;
        GlobalIds = globalIds;
        Segments = segments;
        Merged = merged;
        Searcher = SnapshotSearch.CreateSearcher(snapshot);
    }

    public IndexSearcher Searcher { get; }

    public List<Row> Live { get; }

    public Dictionary<string, int> GlobalIds { get; }

    public int Segments { get; }

    public bool Merged { get; }

    public static AggCorpus Build(int seed, int? documents = null, bool? merge = null, int? buffer = null)
    {
        var r = new Random(seed);
        int count = documents ?? r.Next(5, 260);
        var rows = new List<Row>();
        for (int i = 0; i < count; i++)
        {
            var body = new string[r.Next(1, 5)];
            for (int k = 0; k < body.Length; k++)
            {
                body[k] = Words[r.Next(Words.Length)];
            }

            var tags = new string[r.Next(0, 5)];
            for (int k = 0; k < tags.Length; k++)
            {
                tags[k] = TagPool[r.Next(TagPool.Length)]; // duplicates are possible on purpose
            }

            var ns = new long[r.Next(0, 4)];
            for (int k = 0; k < ns.Length; k++)
            {
                ns[k] = r.Next(-20, 40);
            }

            rows.Add(new Row("d" + i, body, r.Next(6) == 0 ? null : Cats[r.Next(Cats.Length)], tags,
                r.Next(5) == 0 ? null : r.Next(-30, 60), ns, r.Next(5) == 0 ? null : Prices[r.Next(Prices.Length)],
                r.Next(5) == 0 ? null : r.NextInt64(-1_000_000_000_000L, 1_800_000_000_000L), r.Next(5) == 0 ? null : r.Next(2) == 0));
        }

        var storage = new MemoryStorage();
        var options = new IndexWriterOptions { MaxBufferedDocuments = buffer ?? r.Next(4, 40), AutoMerge = false };
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        bool merged = merge ?? r.Next(3) == 0;
        using (IndexWriter w = IndexWriter.Open(storage, Schema, options))
        {
            int first = count * 2 / 3;
            for (int i = 0; i < first; i++)
            {
                w.Upsert(ToDocument(rows[i]));
            }

            w.Commit();
            for (int i = 0; i < first; i++)
            {
                if (r.Next(5) == 0)
                {
                    w.DeleteById(rows[i].Id);
                    deleted.Add(rows[i].Id);
                }
            }

            w.Commit();
            for (int i = first; i < count; i++)
            {
                w.Upsert(ToDocument(rows[i]));
            }

            w.Commit();
            if (merged)
            {
                w.ForceMerge(1);
                w.Commit();
            }
        }

        IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        var live = rows.Where(x => !deleted.Contains(x.Id)).ToList();
        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        int docBase = 0;
        for (int s = 0; s < snapshot.SegmentCount; s++)
        {
            SegmentReader seg = snapshot.GetSegment(s);
            foreach (Row row in live)
            {
                if (seg.TryFindId(row.Id, out int local))
                {
                    ids[row.Id] = docBase + local;
                }
            }

            docBase += seg.MaxDoc;
        }

        return new AggCorpus(snapshot, live, ids, snapshot.SegmentCount, merged);
    }

    private static Document ToDocument(Row d)
    {
        var doc = new Document().Add("id", d.Id).Add("body", string.Join(' ', d.Body));
        if (d.Cat is not null)
        {
            doc.Add("cat", d.Cat);
        }

        foreach (string t in d.Tags)
        {
            doc.Add("tags", t);
        }

        if (d.N is { } n)
        {
            doc.Add("n", n);
        }

        foreach (long v in d.Ns)
        {
            doc.Add("ns", v);
        }

        if (d.Price is { } p)
        {
            doc.Add("price", p);
        }

        if (d.When is { } w)
        {
            doc.Add("when", DateTimeOffset.FromUnixTimeMilliseconds(w));
        }

        if (d.Flag is { } f)
        {
            doc.Add("flag", f);
        }

        return doc;
    }

    public void Dispose() => _snapshot.Dispose();
}
