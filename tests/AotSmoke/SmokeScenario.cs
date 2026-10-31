using OneIndex.Analysis;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.AotSmoke;

/// <summary>
/// Exercises the contracts the way a published app would, with nothing the trimmer or the AOT compiler could drop: write a
/// segment file and a commit to a storage, reopen the storage, read the commit back, round-trip the schema, tokenize, collect.
/// Shared by the NativeAOT app and the browser app.
/// </summary>
internal static class SmokeScenario
{
    public static void Run(IStorage storage)
    {
        Schema schema = new SchemaBuilder().Id("id").Text("body").Integral("year").Build();

        IndexFileSummary summary;
        using (var writer = new IndexFileWriter(storage.Create("seg0.postings"), IndexFile.Magic("SMKE"), 1))
        {
            writer.Write("hello search"u8);
            summary = writer.Finish();
        }

        var file = new SegmentFile(SegmentParts.Postings, "seg0.postings", new CodecRef(SegmentParts.Postings, 1), summary.Length, summary.Checksum);
        var commit = new CommitInfo(1, 1, [new SegmentInfo("seg0", 3, 1, 0, [file])], schema.Serialize(), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, new Dictionary<string, string> { ["origin"] = "smoke" });
        CommitProtocol.Commit(storage, commit);

        CommitInfo? reopened = CommitProtocol.OpenLatest(storage);
        Require(reopened is not null && reopened.Generation == 1 && reopened.Segments.Count == 1 && reopened.Segments[0].LiveCount == 2, "commit round trip");
        CommitProtocol.VerifyFiles(storage, reopened!);
        Require(Schema.Deserialize(reopened!.SchemaBytes.Span).Equals(schema), "schema round trip");
        Require(reopened.AppliedPosition.Span.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), "applied position");

        var tokens = AnalyzerRegistry.Create(WhitespaceLowercaseAnalyzer.RegisteredName).Analyze("Hello  Search");
        Require(tokens.Count == 2 && tokens[1].Term == "search" && tokens[1].Position == 1, "analysis");

        var collector = new TopScoreCollector(2);
        Require(collector.GetTopDocs().Hits.Count == 0, "collector");
        Require(new TermQuery("body", "hello") == new TermQuery("body", "hello"), "query equality");
    }

    /// <summary>The writer lifecycle: buffer, flush, delete, merge, commit, snapshot lookup; threads only when asked for.</summary>
    public static void RunWriter(IStorage storage, bool threadedMerges)
    {
        Schema schema = new SchemaBuilder().Id("id").Text("body").Integral("year").Build();
        var options = new IndexWriterOptions
        {
            MaxBufferedDocuments = 3,
            MergePolicy = new TieredMergePolicy { SegmentsPerTier = 2, MaxMergeAtOnce = 2, FloorSegmentDocs = 2 },
            MergeScheduler = threadedMerges ? new ThreadedMergeScheduler() : new CooperativeMergeScheduler(),
        };
        using (IndexWriter writer = IndexWriter.Open(storage, schema, options))
        {
            for (int i = 0; i < 10; i++)
            {
                writer.Upsert(new Document().Add("id", "doc" + i).Add("body", "text " + i).Add("year", 2000L + i));
            }

            writer.DeleteById("doc3");
            writer.Commit();
            writer.WaitForMerges();
            writer.Commit();
            using IndexSnapshot snapshot = writer.OpenSnapshot();
            Require(snapshot.LiveDocCount == 9 && snapshot.TryFindById("doc4", out _, out _) && !snapshot.TryFindById("doc3", out _, out _), "writer lifecycle");
        }

        using IndexSnapshot reopened = IndexSnapshot.Open(storage);
        Require(reopened.LiveDocCount == 9, "writer reopen");
        RequireDocValues(reopened);
    }

    // Columns, the point index and stored source, read back through a reopened snapshot (the merged segments included).
    private static void RequireDocValues(IndexSnapshot snapshot)
    {
        int matches = 0;
        int stored = 0;
        for (int s = 0; s < snapshot.SegmentCount; s++)
        {
            SegmentReader segment = snapshot.GetSegment(s);
            IColumnReader year = segment.GetPart<IColumnSource>()!.GetColumn("year")!;
            IPointIndexReader points = segment.GetPart<IPointSource>()!.GetPoints("year")!;
            IStoredFieldsReader fields = segment.GetPart<IStoredFieldsSource>()!.StoredFields;
            for (DocIdSetIterator it = points.GetRange(2004, 2006); it.NextDoc() != DocIdSetIterator.NoMoreDocs;)
            {
                matches += segment.Live.IsDeleted(it.DocId) ? 0 : 1;
                Require(year.GetInt64(it.DocId) is >= 2004 and <= 2006, "column value in range");
            }

            for (int d = 0; d < segment.MaxDoc; d++)
            {
                if (!segment.Live.IsDeleted(d))
                {
                    Require(fields.GetDocument(d).Get("year").AsInt64() == year.GetInt64(d), "stored source matches column");
                    stored++;
                }
            }
        }

        Require(matches == 3 && stored == 9, "doc values");
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("smoke check failed: " + what);
        }
    }
}
