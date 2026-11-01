using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Tests;

/// <summary>Collecting a match that does not enter the kept set allocates nothing, for column, distance and identifier-tied sorts.</summary>
[Collection(AllocationMeasurement.Name)]
public sealed class SortAllocationTests(ITestOutputHelper output)
{
    [Fact]
    public void CollectingNonCompetitiveMatchesAllocatesNothing()
    {
        Schema schema = new SchemaBuilder().Id("id").Add(new FieldDefinition("tag", FieldType.Keyword) { Column = true }).Integral("n").Fractional("f").GeoPoint("where").Build();
        using IndexWriter w = IndexWriter.Open(new Storage.MemoryStorage(), schema, new IndexWriterOptions { MaxBufferedDocuments = 100_000, AutoMerge = false });
        for (int i = 0; i < 4000; i++)
        {
            w.Upsert(new Document().Add("id", "d" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)).Add("tag", "t" + (i % 7)).Add("n", i / 3).Add("f", i * 0.5).Add("where", FieldValue.FromGeoPoint(10 + (i / 100.0), 20 + (i / 200.0))));
        }

        w.Commit();
        using IndexSnapshot snap = w.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snap);
        LeafContext leaf = context.Leaves[0];

        SortCriterion[][] sorts =
        [
            [new FieldSort("n")],
            [new FieldSort("tag"), new FieldSort("f")],
            [new GeoDistanceSort("where", 10, 20)],
            [new FieldSort("n") { Direction = SortDirection.Descending, Missing = MissingValuePlacement.First }, new FieldSort("tag")],
        ];
        foreach (SortCriterion[] keys in sorts)
        {
            // Documents arrive in the order that is best for the first ten and worse after: nothing but the first ten enters the heap.
            // (The descending sort sees the worst first, so it is warmed up with the best before the measured window.)
            var collector = new TopFieldCollector(keys, 10, tieBreakField: "id");
            LeafCollector lc = collector.GetLeafCollector(leaf);
            int[] order = keys[0].Direction == SortDirection.Descending ? [.. Enumerable.Range(0, 4000).Reverse()] : [.. Enumerable.Range(0, 4000)];
            for (int i = 0; i < 100; i++)
            {
                lc.Collect(order[i]);
            }

            int next = 100;
            int windows = TestHelpers.AssertNoAllocation("sort collect " + keys[0].GetType().Name, () =>
            {
                for (int i = 0; i < 20; i++)
                {
                    lc.Collect(order[next]);
                    next = next + 1 < order.Length ? next + 1 : 100;
                }
            }, windows: 30);
            output.WriteLine($"{keys[0].GetType().Name} x{keys.Length}: 0 bytes over {windows} windows of 20 matches");
        }
    }
}
