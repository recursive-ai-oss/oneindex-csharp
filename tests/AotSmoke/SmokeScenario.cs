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

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("smoke check failed: " + what);
        }
    }
}
