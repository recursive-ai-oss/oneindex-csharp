using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Tests;

public sealed class SchemaEvolutionTests
{
    private static Schema V1(bool storedBody = true) => new SchemaBuilder().Id("id")
        .Add(new FieldDefinition("body", FieldType.Text) { Stored = storedBody })
        .Add(new FieldDefinition("n", FieldType.Integral) { Indexed = false, Column = false })
        .Add(new FieldDefinition("note", FieldType.Keyword) { Indexed = false })
        .Add(new FieldDefinition("old", FieldType.Keyword))
        .Build();

    // The analyzer changes to a stemming one, n becomes searchable and a column, note becomes searchable, 'old' goes, 'extra' is new.
    private static Schema V2() => new SchemaBuilder().Id("id")
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "en" })
        .Add(new FieldDefinition("n", FieldType.Integral) { Column = true })
        .Add(new FieldDefinition("note", FieldType.Keyword) { Column = true })
        .Add(new FieldDefinition("extra", FieldType.Keyword))
        .Build();

    private static Document Doc(int i)
        => new Document().Add("id", "d" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture))
            .Add("body", i % 3 == 0 ? "the runner is running fast number " + i : i % 3 == 1 ? "she runs home " + i : "a quiet evening " + i)
            .Add("n", (long)i)
            .Add("note", "note" + (i % 5))
            .Add("old", "o" + (i % 2));

    private static IndexWriterOptions Options(IMergeScheduler? scheduler = null) => new() { MaxBufferedDocuments = 25, AutoMerge = false, MergeScheduler = scheduler };

    private static void Build(IStorage storage, int count = 120)
    {
        using IndexWriter w = IndexWriter.Open(storage, V1(), Options());
        for (int i = 0; i < count; i++)
        {
            w.Upsert(Doc(i));
        }

        for (int i = 0; i < count; i += 10)
        {
            w.DeleteById("d" + i.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        }

        w.Commit();
    }

    private static long Hits(IStorage storage, Query query)
    {
        using IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        return SnapshotSearch.CreateSearcher(snapshot).Search(query, 1, true).TotalHits;
    }

    private static long Expected(Func<int, bool> pick, int count = 120) => Enumerable.Range(0, count).Count(i => i % 10 != 0 && pick(i));

    [Fact]
    public void ChangingAnalyzersAndColumnsRebuildsInTheBackgroundWhileTheOldVersionServes()
    {
        var storage = new MemoryStorage();
        Build(storage);
        Assert.Equal(Expected(i => i % 3 == 0), Hits(storage, new TermQuery("body", "running")));

        using IndexWriter w = IndexWriter.Open(storage, V2(), Options());
        SchemaEvolutionStatus before = w.SchemaEvolution;
        Assert.True(before.InProgress);
        Assert.Equal(5, before.SegmentsAwaitingRebuild);
        Assert.Equal(0, before.SchemaEpoch);

        // The old schema serves every segment until the swap.
        Assert.Equal(Expected(i => i % 3 == 0), Hits(storage, new TermQuery("body", "running")));
        Assert.Equal(0, Hits(storage, new TermQuery("body", "run")));
        Assert.Throws<QueryException>(() => Hits(storage, new RangeQuery("n", FieldValue.FromInt64(50), null)));
        Assert.Throws<QueryException>(() => Hits(storage, new TermQuery("note", "note1")));

        // The rebuild advances a few segments with each commit, written ahead and not served.
        w.Commit();
        Assert.True(w.SchemaEvolution.SegmentsAwaitingRebuild < 5);
        Assert.True(w.SchemaEvolution.InProgress);
        Assert.Equal(0, Hits(storage, new TermQuery("body", "run")));
        Assert.Throws<QueryException>(() => Hits(storage, new RangeQuery("n", FieldValue.FromInt64(50), null)));
        using (IndexSnapshot midway = IndexSnapshot.Open(storage))
        {
            Assert.Equal(V1().Serialize(), midway.Schema.Serialize());
            Assert.All(midway.Commit.Segments, s => Assert.Equal(0, s.SchemaEpoch));
            Assert.NotEmpty(midway.Commit.Pending!.Rebuilt);
            midway.Verify();
        }

        // A new document is written under one schema only, so it ends the rebuild first.
        w.Upsert(new Document().Add("id", "fresh").Add("body", "running late").Add("n", 500L).Add("note", "note1").Add("extra", "x"));
        w.Commit();

        w.CompleteSchemaEvolution();
        Assert.False(w.SchemaEvolution.InProgress);
        Assert.Equal(Expected(i => i % 3 != 2) + 1, Hits(storage, new TermQuery("body", "run")));
        Assert.Equal(Expected(i => i >= 50) + 1, Hits(storage, new RangeQuery("n", FieldValue.FromInt64(50), null)));
        Assert.Equal(Expected(i => i % 5 == 1) + 1, Hits(storage, new TermQuery("note", "note1")));
        Assert.Equal(1, Hits(storage, new TermQuery("extra", "x")));

        using IndexSnapshot snapshot = IndexSnapshot.Open(storage);
        snapshot.Verify();
        Assert.Equal(Expected(_ => true) + 1, snapshot.LiveDocCount);
        Assert.All(snapshot.Commit.Segments, s => Assert.Equal(1, s.SchemaEpoch));
        Assert.Equal(V2().Serialize(), snapshot.Schema.Serialize());
    }

    [Fact]
    public void ReopeningMidwayResumesAndNothingIsLost()
    {
        var storage = new MemoryStorage();
        Build(storage);
        using (IndexWriter w = IndexWriter.Open(storage, V2(), Options()))
        {
            w.Commit(); // runs the first few rebuilds, then the writer goes away as if the process had died
            Assert.True(w.SchemaEvolution.InProgress);
        }

        using (IndexSnapshot s = IndexSnapshot.Open(storage))
        {
            s.Verify();
            Assert.Equal(Expected(_ => true), s.LiveDocCount);
            Assert.All(s.Commit.Segments, x => Assert.Equal(0, x.SchemaEpoch));
            Assert.NotEmpty(s.Commit.Pending!.Rebuilt);
            Assert.Equal(V1().Serialize(), s.Schema.Serialize());
        }

        using (IndexWriter w = IndexWriter.Open(storage, V2(), Options()))
        {
            Assert.True(w.SchemaEvolution.InProgress);
            Assert.Equal(0, w.SchemaEvolution.SchemaEpoch);
            Assert.True(w.SchemaEvolution.SegmentsAwaitingRebuild < 5);
            w.CompleteSchemaEvolution();
            Assert.False(w.SchemaEvolution.InProgress);
        }

        Assert.Equal(Expected(i => i % 3 != 2), Hits(storage, new TermQuery("body", "run")));
        using IndexWriter again = IndexWriter.Open(storage, V2(), Options());
        Assert.False(again.SchemaEvolution.InProgress);
        Assert.Equal(1, again.SchemaEvolution.SchemaEpoch);
    }

    [Fact]
    public void DeletesAndUpsertsDuringTheRebuildAreKept()
    {
        var storage = new MemoryStorage();
        Build(storage);
        using (IndexWriter w = IndexWriter.Open(storage, V2(), Options()))
        {
            w.DeleteById("d001");
            w.Upsert(new Document().Add("id", "d002").Add("body", "replaced running text").Add("extra", "again"));
            w.Upsert(new Document().Add("id", "d1000").Add("body", "another"));
            w.CompleteSchemaEvolution();
            w.Commit();
        }

        using IndexSnapshot s = IndexSnapshot.Open(storage);
        Assert.Equal(Expected(_ => true) - 1 + 1, s.LiveDocCount);
        Assert.False(s.TryFindById("d001", out _, out _));
        Assert.True(s.TryFindById("d1000", out _, out _));
    }

    [Fact]
    public void ChangesItCannotDoFailAtOpenNamingTheField()
    {
        var storage = new MemoryStorage();
        Build(storage);

        SchemaEvolutionException type = Assert.Throws<SchemaEvolutionException>(() =>
            IndexWriter.Open(storage, new SchemaBuilder().Id("id").Text("body").Add(new FieldDefinition("n", FieldType.Keyword)).Build(), Options()));
        Assert.Equal("n", type.Field);
        Assert.Contains("'n'", type.Message);

        SchemaEvolutionException id = Assert.Throws<SchemaEvolutionException>(() =>
            IndexWriter.Open(storage, new SchemaBuilder().Id("other").Text("body").Build(), Options()));
        Assert.Contains("identifier", id.Message);

        // The failed opens left the index as it was.
        using IndexWriter ok = IndexWriter.Open(storage, V1(), Options());
        Assert.False(ok.SchemaEvolution.InProgress);

        // A rebuild is impossible when a searchable field keeps no stored source.
        var unstored = new MemoryStorage();
        using (IndexWriter w = IndexWriter.Open(unstored, V1(storedBody: false), Options()))
        {
            w.Upsert(Doc(1));
            w.Commit();
        }

        SchemaEvolutionException stored = Assert.Throws<SchemaEvolutionException>(() => IndexWriter.Open(unstored, V2(), Options()));
        Assert.Equal("body", stored.Field);
        Assert.Contains("not stored", stored.Message);
    }

    [Fact]
    public void AddingAndRemovingFieldsNeedsNoRebuildAndKeepsTheFirstCommitFormat()
    {
        var storage = new MemoryStorage();
        Build(storage);
        Schema simple = new SchemaBuilder().Id("id")
            .Add(new FieldDefinition("body", FieldType.Text) { Stored = true })
            .Add(new FieldDefinition("n", FieldType.Integral) { Indexed = false, Column = false })
            .Add(new FieldDefinition("note", FieldType.Keyword) { Indexed = false })
            .Add(new FieldDefinition("another", FieldType.Keyword))
            .Build();
        using (IndexWriter w = IndexWriter.Open(storage, simple, Options()))
        {
            Assert.False(w.SchemaEvolution.InProgress);
            Assert.Equal(0, w.SchemaEvolution.SchemaEpoch);
        }

        using IndexSnapshot s = IndexSnapshot.Open(storage);
        Assert.Equal(simple, s.Schema);
        Assert.Equal(Expected(_ => true), s.LiveDocCount);
        using IRangeReader file = storage.OpenRead(CommitFileNames.For(s.Generation));
        var head = new byte[8];
        file.ReadExactly(0, head);
        Assert.Equal(1u, BitConverter.ToUInt32(head, 4));
    }

    [Fact]
    public void CommitsWithAndWithoutEpochsRoundTrip()
    {
        CommitInfo plain = TestHelpers.MakeCommit(3, []);
        byte[] v1 = CommitFileFormat.Serialize(plain);
        Assert.Equal(1u, BitConverter.ToUInt32(v1, 4));
        Assert.Equal(0, CommitFileFormat.Parse(v1, "c").SchemaEpoch);

        var files = new List<SegmentFile>();
        var seg = new SegmentInfo("s0000000000000001", 5, 0, 0, files, schemaEpoch: 2);
        var evolved = new CommitInfo(4, 9, [seg], plain.SchemaBytes, plain.AppliedPosition, null, schemaEpoch: 3);
        byte[] v2 = CommitFileFormat.Serialize(evolved);
        Assert.Equal(2u, BitConverter.ToUInt32(v2, 4));
        CommitInfo back = CommitFileFormat.Parse(v2, "c");
        Assert.Equal(3, back.SchemaEpoch);
        Assert.Equal(2, back.Segments[0].SchemaEpoch);
        Assert.Equal(v2, CommitFileFormat.Serialize(back));

        // A segment cannot be newer than the commit that lists it.
        var impossible = new CommitInfo(4, 9, [new SegmentInfo("s0000000000000001", 5, 0, 0, files, schemaEpoch: 5)], plain.SchemaBytes, plain.AppliedPosition, null, schemaEpoch: 3);
        Assert.Throws<CorruptIndexException>(() => CommitFileFormat.Parse(CommitFileFormat.Serialize(impossible), "c"));
    }

    [Fact]
    public void PendingRebuildsRoundTripAndAreCanonical()
    {
        CommitInfo plain = TestHelpers.MakeCommit(3, []);
        var serving = new SegmentInfo("s0000000000000001", 5, 1, 1, [], schemaEpoch: 0);
        var twin = new SegmentInfo("s0000000000000002", 4, 0, 0, [], schemaEpoch: 1);
        var commit = new CommitInfo(4, 9, [serving], plain.SchemaBytes, plain.AppliedPosition, null, 0, new PendingEvolution(V2().Serialize(), [new RebuiltSegment(twin, "s0000000000000001", 1)]));
        byte[] bytes = CommitFileFormat.Serialize(commit);
        CommitInfo back = CommitFileFormat.Parse(bytes, "c");
        Assert.Single(back.Segments);
        Assert.Equal("s0000000000000002", back.Pending!.Rebuilt[0].Info.Name);
        Assert.Equal(1, back.Pending.Rebuilt[0].SourceDeleted);
        Assert.Equal(bytes, CommitFileFormat.Serialize(back));

        // A rebuilt segment must name a served segment and belong to the next epoch.
        var orphan = new CommitInfo(4, 9, [serving], plain.SchemaBytes, plain.AppliedPosition, null, 0, new PendingEvolution(V2().Serialize(), [new RebuiltSegment(twin, "s00000000000000ff", 1)]));
        Assert.Throws<CorruptIndexException>(() => CommitFileFormat.Parse(CommitFileFormat.Serialize(orphan), "c"));
        var wrongEpoch = new CommitInfo(4, 9, [serving], plain.SchemaBytes, plain.AppliedPosition, null, 0, new PendingEvolution(V2().Serialize(), [new RebuiltSegment(serving, "s0000000000000001", 1)]));
        Assert.Throws<ArgumentException>(() => CommitFileFormat.Serialize(wrongEpoch));
    }

    [Fact]
    public void SchemaSectionsFromANewerBuildAreSkipped()
    {
        // A version 2 record with a section id this build does not define (7) after the fields: the fields still read.
        byte[] bytes = [.. V1().Serialize(), 1, 7, 2, 9, 9];
        bytes[4] = 2;
        Schema read = Schema.Deserialize(bytes);
        Assert.Equal(V1().Fields.Select(f => f.Name), read.Fields.Select(f => f.Name));
        Assert.Equal(V1().Serialize(), read.Serialize());
    }

    private static Dictionary<string, byte[]> Everything(MemoryStorage storage)
        => storage.List().Order(StringComparer.Ordinal).ToDictionary(n => n, n => TestHelpers.ReadAll(storage, n));

    [Fact]
    public void CooperativeAndThreadedRebuildsGiveTheSameIndex()
    {
        Dictionary<string, byte[]> Run(IMergeScheduler? scheduler)
        {
            var storage = new MemoryStorage();
            Build(storage);
            using (IndexWriter w = IndexWriter.Open(storage, V2(), Options(scheduler)))
            {
                w.CompleteSchemaEvolution();
            }

            return Everything(storage);
        }

        Dictionary<string, byte[]> cooperative = Run(null);
        Dictionary<string, byte[]> threaded = Run(new ThreadedMergeScheduler());
        Assert.Equal(cooperative.Keys, threaded.Keys);
        foreach (string name in cooperative.Keys)
        {
            Assert.True(cooperative[name].AsSpan().SequenceEqual(threaded[name]), $"{name} differs between the cooperative and the threaded rebuild");
        }
    }

    [Fact]
    public void AReaderProcessKeepsWorkingAcrossTheSwap()
    {
        string dir = TestHelpers.NewTempDirectory();
        try
        {
            {
                var storage = new FileSystemStorage(dir);
                Build(storage);
            }

            var readerStorage = new FileSystemStorage(dir);
            using IndexReader reader = IndexReader.Open(readerStorage);
            using IndexSnapshot old = reader.Acquire();
            long oldHits = SnapshotSearch.CreateSearcher(old).Search(new TermQuery("body", "running"), 1, true).TotalHits;

            var writerStorage = new FileSystemStorage(dir);
            using (IndexWriter w = IndexWriter.Open(writerStorage, V2(), Options()))
            {
                w.CompleteSchemaEvolution();
            }

            // The snapshot taken before the swap still answers from its own files.
            Assert.Equal(oldHits, SnapshotSearch.CreateSearcher(old).Search(new TermQuery("body", "running"), 1, true).TotalHits);
            old.Verify();
            Assert.True(reader.Refresh());
            using IndexSnapshot fresh = reader.Acquire();
            Assert.Equal(Expected(i => i % 3 != 2), SnapshotSearch.CreateSearcher(fresh).Search(new TermQuery("body", "run"), 1, true).TotalHits);
            fresh.Verify();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
