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

    /// <summary>Small buffers and a low merge threshold so that a short workload flushes and merges often.</summary>
    public static IndexWriterOptions Options(IMergeScheduler? scheduler = null, int maxBuffered = 6, bool autoMerge = true) => new()
    {
        Codecs = [new StoredDocumentCodec()],
        MaxBufferedDocuments = maxBuffered,
        MergePolicy = new TieredMergePolicy { SegmentsPerTier = 3, MaxMergeAtOnce = 3, FloorSegmentDocs = 4, DeletesPctAllowed = 50 },
        MergeScheduler = scheduler,
        AutoMerge = autoMerge,
    };

    public static IReadOnlyList<ISegmentCodec> Codecs { get; } = [new StoredDocumentCodec()];

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
            IStoredFieldsReader stored = seg.GetPart<IStoredFieldsReader>() ?? throw new InvalidDataException($"segment {seg.Info.Name} has no stored documents");
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
            if (!listed.Contains(name))
            {
                return name;
            }
        }

        return null;
    }
}
