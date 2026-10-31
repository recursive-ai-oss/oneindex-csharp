using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;
using OneIndex.Storage;

namespace OneIndex.Durability;

/// <summary>The schema, codecs and a seeded operation script shared by the fault sweeps and the kill harness.</summary>
public static class Workload
{
    public static readonly Schema Schema = new SchemaBuilder()
        .Id("id")
        .Keyword("tag")
        .Text("body")
        .Integral("n")
        .Build();

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

    public static Document ToDocument(Op op) => new Document().Add("id", op.Id).Add("tag", op.Tag).Add("body", op.Body).Add("n", op.N);

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

        return rows;
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
            ("body:body", new TermQuery("body", "body"), _ => true),
            ($"body:{someNumber}", new TermQuery("body", someNumber), r => r.Body == someBody),
            ("n in [250000,750000)", new RangeQuery("n", FieldValue.FromInt64(250_000), FieldValue.FromInt64(750_000)) { IncludeUpper = false }, r => r.N >= 250_000 && r.N < 750_000),
            ("tag=t0 and n>=500000", new BooleanQuery([new BooleanClause(new TermQuery("tag", "t0"), Occur.Must), new BooleanClause(new RangeQuery("n", FieldValue.FromInt64(500_000), null), Occur.Must)]), r => r.Tag == "t0" && r.N >= 500_000),
        };
        foreach ((string name, Query query, Func<(string Tag, string Body, long N), bool> expect) in cases)
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
