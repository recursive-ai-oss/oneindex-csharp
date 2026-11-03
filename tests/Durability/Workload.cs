using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.Durability;

/// <summary>The schema, codecs and a seeded operation script shared by the fault sweeps and the kill harness.</summary>
public static class Workload
{
    public static readonly Schema Schema = new SchemaBuilder()
        .Id("id")
        .Keyword("tag")
        .Text("body")
        .Integral("n")
        .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = VectorDimensions, FlatThreshold = 3, HnswM = 4, EfConstruction = 16, Quantization = VectorQuantization.Scalar4 })
        .Build();

    /// <summary>The length of the vector every document carries; its content is a function of the document's number.</summary>
    public const int VectorDimensions = 8;

    /// <summary>The vector of a document: seeded by its number, so a recovered index can be checked without storing a model of it.</summary>
    public static float[] VectorOf(long n)
    {
        var rng = new Random(unchecked((int)(n % int.MaxValue)));
        var v = new float[VectorDimensions];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = (float)(rng.NextDouble() - 0.5);
        }

        return v;
    }

    /// <summary>
    /// The schema the evolution campaigns switch to: a stemming analyzer on the body, a column on the tag, a new field and a different graph size for the vectors. Moving between
    /// <see cref="Schema"/> and this one in either direction rebuilds the segments from stored source.
    /// </summary>
    public static readonly Schema SchemaV2 = new SchemaBuilder()
        .Id("id")
        .Add(new FieldDefinition("tag", FieldType.Keyword) { Column = true })
        .Add(new FieldDefinition("body", FieldType.Text) { Analyzer = "en" })
        .Integral("n")
        .Keyword("extra")
        .Add(new FieldDefinition("v", FieldType.Vector) { Dimensions = VectorDimensions, FlatThreshold = 3, HnswM = 6, EfConstruction = 16 })
        .Build();

    /// <summary>The schema of an evolution campaign cycle: 1 is <see cref="Schema"/>, 2 is <see cref="SchemaV2"/>.</summary>
    public static Schema ForVersion(int version) => version == 2 ? SchemaV2 : Schema;

    /// <summary>Options for the evolution campaigns: the real codecs, because the rebuild reads the real stored source.</summary>
    public static IndexWriterOptions EvolveOptions(IMergeScheduler? scheduler = null, int maxBuffered = 6) => new()
    {
        MaxBufferedDocuments = maxBuffered,
        MergePolicy = new TieredMergePolicy { SegmentsPerTier = 3, MaxMergeAtOnce = 3, FloorSegmentDocs = 4, DeletesPctAllowed = 50 },
        MergeScheduler = scheduler,
    };

    /// <summary>
    /// The engine's default codec set (text, columns, points, stored source) with small buffers and a low merge threshold, so that a
    /// short workload flushes and merges often. The schema has a keyword, a text and a numeric field, all stored.
    /// </summary>
    public static IndexWriterOptions Options(IMergeScheduler? scheduler = null, int maxBuffered = 6, bool autoMerge = true) => new()
    {
        MaxBufferedDocuments = maxBuffered,
        MergePolicy = new TieredMergePolicy { SegmentsPerTier = 3, MaxMergeAtOnce = 3, FloorSegmentDocs = 4, DeletesPctAllowed = 50 },
        MergeScheduler = scheduler,
        AutoMerge = autoMerge,
    };

    public static IReadOnlyList<ISegmentCodec> Codecs => OneIndex.Codecs.SegmentCodecs.CreateDefault();

    public enum Kind
    {
        Upsert,
        DeleteId,
        DeleteTag,
        Commit,
        Merge,
    }

    public readonly record struct Op(Kind Kind, string Id, string Tag, string Body, long N);

    public static Document ToDocument(Op op) => new Document().Add("id", op.Id).Add("tag", op.Tag).Add("body", op.Body).Add("n", op.N).Add("v", VectorOf(op.N));

    public static string Fingerprint(string tag, string body, long n) => $"{tag}|{body}|{n}";

    /// <summary>Generates a script: mostly upserts over a small id space, some deletes, commits and merges.</summary>
    public static List<Op> Generate(int seed, int count, int idSpace = 40)
    {
        var rng = new Random(seed);
        var ops = new List<Op>(count);
        for (int i = 0; i < count; i++)
        {
            int r = rng.Next(100);
            string id = "doc" + rng.Next(idSpace);
            if (r < 60)
            {
                ops.Add(new Op(Kind.Upsert, id, "t" + rng.Next(5), "body " + rng.Next(1000), rng.NextInt64(0, 1_000_000)));
            }
            else if (r < 75)
            {
                ops.Add(new Op(Kind.DeleteId, id, "", "", 0));
            }
            else if (r < 80)
            {
                ops.Add(new Op(Kind.DeleteTag, "", "t" + rng.Next(5), "", 0));
            }
            else if (r < 94)
            {
                ops.Add(new Op(Kind.Commit, "", "", "", 0));
            }
            else
            {
                ops.Add(new Op(Kind.Merge, "", "", "", 0));
            }
        }

        ops.Add(new Op(Kind.Commit, "", "", "", 0));
        return ops;
    }

    /// <summary>The expected content of an index: identifier to fingerprint.</summary>
    public sealed class Model
    {
        public SortedDictionary<string, string> Rows { get; } = new(StringComparer.Ordinal);

        public void Apply(Op op)
        {
            switch (op.Kind)
            {
                case Kind.Upsert:
                    Rows[op.Id] = Fingerprint(op.Tag, op.Body, op.N);
                    break;
                case Kind.DeleteId:
                    Rows.Remove(op.Id);
                    break;
                case Kind.DeleteTag:
                    foreach (string id in Rows.Where(p => p.Value.StartsWith(op.Tag + "|", StringComparison.Ordinal)).Select(p => p.Key).ToList())
                    {
                        Rows.Remove(id);
                    }

                    break;
            }
        }

        public string Hash() => HashRows(Rows);
    }

    public static string HashRows(IEnumerable<KeyValuePair<string, string>> rows)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (KeyValuePair<string, string> r in rows)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(r.Key + "\0" + r.Value + "\n"));
        }

        return Convert.ToHexString(sha.GetHashAndReset())[..16];
    }

    /// <summary>Applies an operation to a writer.</summary>
    public static void Apply(IndexWriter writer, Op op)
    {
        switch (op.Kind)
        {
            case Kind.Upsert:
                writer.Upsert(ToDocument(op));
                break;
            case Kind.DeleteId:
                writer.DeleteById(op.Id);
                break;
            case Kind.DeleteTag:
                writer.DeleteByTerm("tag", op.Tag);
                break;
            case Kind.Merge:
                writer.WaitForMerges();
                break;
        }
    }

    /// <summary>Reads everything a snapshot holds and checks it is self-consistent: no duplicate identifier, the identifier map agrees with the stored documents, counts agree.</summary>
    public static SortedDictionary<string, string> ReadContent(IndexSnapshot snapshot)
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        for (int s = 0; s < snapshot.SegmentCount; s++)
        {
            SegmentReader seg = snapshot.GetSegment(s);
            IStoredFieldsReader stored = seg.GetPart<IStoredFieldsSource>()?.StoredFields ?? throw new InvalidDataException($"segment {seg.Info.Name} has no stored documents");
            for (int d = 0; d < seg.MaxDoc; d++)
            {
                if (!seg.Live.IsLive(d))
                {
                    continue;
                }

                Document doc = stored.GetDocument(d);
                string id = doc.Get("id").AsText();
                if (!rows.TryAdd(id, Fingerprint(doc.Get("tag").AsText(), doc.Get("body").AsText(), doc.Get("n").AsInt64())))
                {
                    throw new InvalidDataException($"identifier {id} is live twice");
                }

                if (!snapshot.TryFindById(id, out int segment, out int docId) || segment != s || docId != d)
                {
                    throw new InvalidDataException($"identifier map disagrees with the stored document for {id}");
                }
            }
        }

        if (rows.Count != snapshot.LiveDocCount)
        {
            throw new InvalidDataException($"{rows.Count} live documents read but the commit counts {snapshot.LiveDocCount}");
        }

        CheckVectors(snapshot, rows);
        return rows;
    }

    private static float[] Unit(float[] v)
    {
        double norm = Math.Sqrt(v.Sum(x => (double)x * x));
        return v.Select(x => (float)(x / norm)).ToArray();
    }

    private static double Score(float[] a, float[] b) => (1 + a.Zip(b, (x, y) => (double)x * y).Sum()) / 2;

    /// <summary>
    /// Every live document has its vector in the vector file, equal to the one its number determines, and exact nearest-neighbour
    /// search over the snapshot returns the best scores brute force finds over the recovered documents. The graph search must at
    /// least return live documents with true scores.
    /// </summary>
    private static void CheckVectors(IndexSnapshot snapshot, SortedDictionary<string, string> rows)
    {
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        var searcher = new IndexSearcher(context, new QueryEngine());
        var vectorOf = new Dictionary<string, float[]>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> row in rows)
        {
            vectorOf[row.Key] = Unit(VectorOf(long.Parse(row.Value[(row.Value.LastIndexOf('|') + 1)..], System.Globalization.CultureInfo.InvariantCulture)));
        }

        foreach (LeafContext leaf in context.Leaves)
        {
            IVectorFieldReader? reader = leaf.Reader.GetVectors("v");
            var buffer = new float[VectorDimensions];
            for (int d = 0; d < leaf.Reader.MaxDoc; d++)
            {
                if (leaf.Reader.LiveDocs is { } live && !live.IsLive(d))
                {
                    continue;
                }

                string id = leaf.Reader.StoredFields!.GetDocument(d).Get("id").AsText();
                if (reader is null || !reader.TryGetVector(d, buffer) || buffer.Zip(vectorOf[id], (x, y) => Math.Abs(x - y)).Max() > 1e-6)
                {
                    throw new InvalidDataException($"the vector of {id} in segment {leaf.Reader.Info.Name} is missing or wrong");
                }
            }
        }

        var rng = new Random(rows.Count);
        for (int q = 0; q < 3 && rows.Count > 0; q++)
        {
            float[] query = VectorOf(rng.Next(1_000_000));
            float[] unit = Unit(query);
            double[] expected = vectorOf.Values.Select(v => Score(v, unit)).OrderByDescending(x => x).Take(5).ToArray();
            foreach (bool exact in new[] { true, false })
            {
                TopDocs top = searcher.Search(new KnnQuery("v", query, 5) { Exact = exact }, 5);
                if (top.Hits.Count != expected.Length)
                {
                    throw new InvalidDataException($"nearest-neighbour search returned {top.Hits.Count} of {expected.Length} documents");
                }

                for (int i = 0; i < top.Hits.Count; i++)
                {
                    LeafContext leaf = context.Leaves.Last(l => l.DocBase <= top.Hits[i].DocId);
                    string id = leaf.Reader.StoredFields!.GetDocument(top.Hits[i].DocId - leaf.DocBase).Get("id").AsText();
                    if (!vectorOf.TryGetValue(id, out float[]? v) || Math.Abs(Score(v, unit) - top.Hits[i].Score) > 1e-4)
                    {
                        throw new InvalidDataException($"nearest-neighbour hit {id} is not a live document or has a wrong score");
                    }

                    if (exact && Math.Abs(expected[i] - top.Hits[i].Score) > 1e-4)
                    {
                        throw new InvalidDataException($"exact nearest-neighbour rank {i} scores {top.Hits[i].Score}, brute force finds {expected[i]}");
                    }
                }
            }
        }

        if (rows.Count > 0)
        {
            CheckFilteredVectors(context, searcher, rows, vectorOf);
        }
    }

    /// <summary>
    /// Filtered nearest-neighbour search after recovery: a keyword filter and a numeric range filter. Every hit must satisfy the filter and
    /// be a live document; the exact search must equal brute force over the matching documents, and the graph search must return a full page.
    /// </summary>
    private static void CheckFilteredVectors(SearchContext context, IndexSearcher searcher, SortedDictionary<string, string> rows, Dictionary<string, float[]> vectorOf)
    {
        var rng = new Random(rows.Count + 7);
        var filters = new (string Name, Query Filter, Func<string, bool> Matches)[]
        {
            ("tag=t1", new TermQuery("tag", "t1"), f => f.StartsWith("t1|", StringComparison.Ordinal)),
            ("n<500000", new RangeQuery("n", null, FieldValue.FromInt64(500_000)) { IncludeUpper = false }, f => long.Parse(f[(f.LastIndexOf('|') + 1)..], System.Globalization.CultureInfo.InvariantCulture) < 500_000),
        };
        foreach ((string name, Query filter, Func<string, bool> matches) in filters)
        {
            var matching = rows.Where(r => matches(r.Value)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
            float[] query = VectorOf(rng.Next(1_000_000));
            float[] unit = Unit(query);
            double[] expected = matching.Select(id => Score(vectorOf[id], unit)).OrderByDescending(x => x).Take(5).ToArray();
            foreach (bool exact in new[] { true, false })
            {
                TopDocs top = searcher.Search(new KnnQuery("v", query, 5) { Exact = exact, Filter = filter }, 5);
                if (top.Hits.Count != expected.Length)
                {
                    throw new InvalidDataException($"filtered ({name}) nearest-neighbour search returned {top.Hits.Count} of {expected.Length} documents");
                }

                for (int i = 0; i < top.Hits.Count; i++)
                {
                    LeafContext leaf = context.Leaves.Last(l => l.DocBase <= top.Hits[i].DocId);
                    string id = leaf.Reader.StoredFields!.GetDocument(top.Hits[i].DocId - leaf.DocBase).Get("id").AsText();
                    if (!matching.Contains(id))
                    {
                        throw new InvalidDataException($"filtered ({name}) nearest-neighbour hit {id} does not match the filter");
                    }

                    if (exact && Math.Abs(expected[i] - top.Hits[i].Score) > 1e-4)
                    {
                        throw new InvalidDataException($"filtered ({name}) exact rank {i} scores {top.Hits[i].Score}, brute force finds {expected[i]}");
                    }
                }
            }
        }
    }

    /// <summary>Every owned file in the storage must be listed by the newest commit (nothing leaked) once nothing else pins it.</summary>
    public static string? FindLeaks(IStorage storage)
    {
        CommitInfo? commit = CommitProtocol.OpenLatest(storage);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        if (commit is not null)
        {
            listed.Add(CommitFileNames.For(commit.Generation));
            foreach (SegmentInfo s in commit.Segments)
            {
                foreach (SegmentFile f in s.Files)
                {
                    listed.Add(f.FileName);
                }
            }
        }

        foreach (string name in storage.List())
        {
            if (!listed.Contains(name) && name != "write.lock")
            {
                return name;
            }
        }

        return null;
    }

    /// <summary>
    /// Runs fixed queries through the search engine on a snapshot and compares each result set with what the verified rows imply:
    /// a keyword term, a text term, a numeric range and a boolean combination of the two. A mismatch means the indexes disagree with the stored source.
    /// </summary>
    public static string? CheckSearch(IndexSnapshot snapshot, SortedDictionary<string, string> rows)
    {
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        var searcher = new IndexSearcher(context, new QueryEngine());
        static (string Tag, string Body, long N) Parse(string fingerprint)
        {
            string[] p = fingerprint.Split('|');
            return (p[0], p[1], long.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture));
        }

        string someBody = rows.Count > 0 ? Parse(rows.First().Value).Body : "body 0";
        string someNumber = someBody[(someBody.LastIndexOf(' ') + 1)..];
        var cases = new (string Name, Query Query, Func<(string Tag, string Body, long N), bool> Expect)[]
        {
            ("tag=t1", new TermQuery("tag", "t1"), r => r.Tag == "t1"),
            // A stemming analyzer (the evolved schema) indexes "body" as its stem, so the raw term is only checked under the plain analyzer.
            ("body:body", new TermQuery("body", "body"), _ => true),
            ($"body:{someNumber}", new TermQuery("body", someNumber), r => r.Body == someBody),
            ("n in [250000,750000)", new RangeQuery("n", FieldValue.FromInt64(250_000), FieldValue.FromInt64(750_000)) { IncludeUpper = false }, r => r.N >= 250_000 && r.N < 750_000),
            ("tag=t0 and n>=500000", new BooleanQuery([new BooleanClause(new TermQuery("tag", "t0"), Occur.Must), new BooleanClause(new RangeQuery("n", FieldValue.FromInt64(500_000), null), Occur.Must)]), r => r.Tag == "t0" && r.N >= 500_000),
        };
        bool plain = !snapshot.Schema.TryGetField("body", out FieldDefinition? bodyField) || bodyField.Analyzer is null;
        foreach ((string name, Query query, Func<(string Tag, string Body, long N), bool> expect) in cases.Where(c => plain || c.Name != "body:body"))
        {
            TopDocs top = searcher.Search(query, 10_000, countExactly: true);
            var found = new SortedSet<string>(StringComparer.Ordinal);
            foreach (ScoreDoc hit in top.Hits)
            {
                LeafContext leaf = context.Leaves.Last(l => l.DocBase <= hit.DocId);
                found.Add(leaf.Reader.StoredFields!.GetDocument(hit.DocId - leaf.DocBase).Get("id").AsText());
            }

            var wanted = new SortedSet<string>(rows.Where(r => expect(Parse(r.Value))).Select(r => r.Key), StringComparer.Ordinal);
            if (!wanted.SetEquals(found) || top.TotalHits != wanted.Count)
            {
                return $"search '{name}' found {found.Count} documents ({top.TotalHits} total), the content implies {wanted.Count}";
            }
        }

        return null;
    }
}
