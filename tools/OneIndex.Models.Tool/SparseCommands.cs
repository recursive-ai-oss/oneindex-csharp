using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OneIndex.Harness;
using OneIndex.Index;
using OneIndex.Models;
using OneIndex.Search;
using OneIndex.Storage;
using OneIndex.Vectors;

/// <summary>The learned sparse commands of oneindex-models: parity with a reference runtime and BEIR retrieval through the engine's sparse query.</summary>
internal static class SparseCommands
{
    private static NeuralSparseEncoder Open(Func<string, string?> opt)
        => NeuralSparseEncoder.OpenDirectory(opt("dir") ?? throw new ArgumentException("missing --dir"), new NeuralSparseEncoderOptions
        {
            ModelId = "tool",
            ExtraLog1p = int.Parse(opt("extra-log1p") ?? "0", CultureInfo.InvariantCulture),
            MaxThreads = int.Parse(opt("threads") ?? "0", CultureInfo.InvariantCulture),
            MaxBatchTokens = int.Parse(opt("batch-tokens") ?? "1024", CultureInfo.InvariantCulture),
        });

    /// <summary>Compares our vectors with reference vectors (one JSON object per line) for the texts of a jsonl file.</summary>
    public static int Parity(Func<string, string?> opt)
    {
        EmbeddingRole role = opt("role") == "query" ? EmbeddingRole.Query : EmbeddingRole.Document;
        var texts = File.ReadLines(opt("texts")!).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("t").GetString()!).ToList();
        var reference = File.ReadLines(opt("reference")!).Select(l => JsonSerializer.Deserialize<Dictionary<string, float>>(l)!).ToList();
        using NeuralSparseEncoder encoder = Open(opt);
        var ours = new SparseVector[texts.Count];
        var sw = Stopwatch.StartNew();
        encoder.Encode(texts, role, ours);
        sw.Stop();
        var cosines = new List<double>();
        int sameSupport = 0;
        double maxAbs = 0;
        int worst = int.TryParse(opt("show"), out int shown) ? shown : -1;
        for (int i = 0; i < texts.Count; i++)
        {
            Dictionary<string, float> r = reference[i];
            double dot = 0, na = 0, nb = 0;
            bool same = true;
            for (int k = 0; k < ours[i].Count; k++)
            {
                double a = ours[i].Weights[k];
                double b = r.TryGetValue(ours[i].Tokens[k], out float rv) ? rv : 0;
                same &= b != 0;
                dot += a * b;
                na += a * a;
                maxAbs = Math.Max(maxAbs, Math.Abs(a - b));
            }

            foreach ((string t, float w) in r)
            {
                nb += (double)w * w;
                if (Array.BinarySearch(ours[i].Tokens, t, StringComparer.Ordinal) < 0)
                {
                    same = false;
                    maxAbs = Math.Max(maxAbs, w);
                }
            }

            sameSupport += same ? 1 : 0;
            double c = na == 0 && nb == 0 ? 1 : dot / Math.Sqrt(na * nb);
            cosines.Add(c);
            if (opt("show") is null && c <= cosines.Min())
            {
                worst = i;
            }
        }

        if (worst >= 0)
        {
            var diffs = new List<(string Token, double Ours, double Ref)>();
            foreach (string t in reference[worst].Keys.Union(ours[worst].Tokens))
            {
                int j = Array.BinarySearch(ours[worst].Tokens, t, StringComparer.Ordinal);
                diffs.Add((t, j >= 0 ? ours[worst].Weights[j] : 0, reference[worst].GetValueOrDefault(t)));
            }

            Console.WriteLine($"worst text: {texts[worst][..Math.Min(120, texts[worst].Length)]}");
            foreach (var d in diffs.OrderByDescending(x => Math.Abs(x.Ours - x.Ref)).Take(6))
            {
                Console.WriteLine($"  {d.Token}: ours {d.Ours:F4} reference {d.Ref:F4}");
            }
        }

        cosines.Sort();
        Console.WriteLine($"texts {texts.Count} role {role} identical-support {sameSupport} cosine min {cosines[0]:F6} median {cosines[cosines.Count / 2]:F6} mean {cosines.Average():F6} maxAbsDiff {maxAbs:F4} worst text #{worst} seconds {sw.Elapsed.TotalSeconds:F1}");
        return 0;
    }

    /// <summary>Encodes a BEIR corpus in resumable chunks, indexes the vectors in an in-memory index and ranks the queries with SparseQuery.</summary>
    public static int Beir(Func<string, string?> opt)
    {
        var data = BeirDataset.Open(Path.Combine(opt("data")!, opt("set")!));
        string work = Path.Combine(opt("work")!, opt("set")!);
        Directory.CreateDirectory(work);
        double budget = double.Parse(opt("budget-seconds") ?? "1500", CultureInfo.InvariantCulture);
        var clock = Stopwatch.StartNew();
        using NeuralSparseEncoder encoder = Open(opt);
        const int ChunkDocs = 200;
        var ids = new List<string>();
        var chunks = new List<List<string>>();
        foreach (Doc doc in data.ReadCorpus())
        {
            ids.Add(doc.Id);
            if (chunks.Count == 0 || chunks[^1].Count == ChunkDocs)
            {
                chunks.Add([]);
            }

            chunks[^1].Add(doc.Title.Length == 0 ? doc.Text : doc.Title + " " + doc.Text);
        }

        double encodeSeconds = 0;
        int encoded = 0;
        for (int c = 0; c < chunks.Count; c++)
        {
            string file = Path.Combine(work, $"corpus-{c:D5}.sparse");
            if (File.Exists(file))
            {
                continue;
            }

            if (clock.Elapsed.TotalSeconds > budget)
            {
                Console.WriteLine($"budget used after {c} of {chunks.Count} chunks; run again to continue");
                return 3;
            }

            var sw = Stopwatch.StartNew();
            var vectors = new SparseVector[chunks[c].Count];
            encoder.Encode(chunks[c], EmbeddingRole.Document, vectors);
            encodeSeconds += sw.Elapsed.TotalSeconds;
            encoded += vectors.Length;
            using (var w = new BinaryWriter(File.Create(file + ".part")))
            {
                foreach (SparseVector v in vectors)
                {
                    w.Write(v.Count);
                    for (int k = 0; k < v.Count; k++)
                    {
                        w.Write(v.Tokens[k]);
                        w.Write(v.Weights[k]);
                    }
                }
            }

            File.Move(file + ".part", file);
            Console.WriteLine($"chunk {c + 1}/{chunks.Count}: {vectors.Length} docs in {sw.Elapsed.TotalSeconds:F1} s ({vectors.Length / sw.Elapsed.TotalSeconds:F1} passages/s)");
        }

        var schema = new SchemaBuilder().Id("id").Sparse("s").Build();
        var storage = new MemoryStorage();
        using IndexWriter writer = IndexWriter.Open(storage, schema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 });
        long entries = 0;
        int at = 0;
        for (int c = 0; c < chunks.Count; c++)
        {
            using var r = new BinaryReader(File.OpenRead(Path.Combine(work, $"corpus-{c:D5}.sparse")));
            for (int i = 0; i < chunks[c].Count; i++)
            {
                int n = r.ReadInt32();
                var list = new List<KeyValuePair<string, float>>(n);
                for (int k = 0; k < n; k++)
                {
                    list.Add(new(r.ReadString(), r.ReadSingle()));
                }

                entries += n;
                writer.Upsert(new Document().Add("id", ids[at++]).Add("s", new SparseVector(list)));
            }
        }

        writer.Commit();
        writer.ForceMerge(1);
        writer.Commit();
        using IndexSnapshot snap = writer.OpenSnapshot();
        IndexSearcher searcher = SnapshotSearch.CreateSearcher(snap);
        var run = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
        var qsw = Stopwatch.StartNew();
        var qvec = new SparseVector[data.Queries.Count];
        encoder.Encode(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query, qvec);
        double encodeQuerySeconds = qsw.Elapsed.TotalSeconds;
        qsw.Restart();
        for (int q = 0; q < data.Queries.Count; q++)
        {
            TopDocs top = searcher.Search(new SparseQuery("s", qvec[q]), 11);
            var hits = new List<(string, double)>(10);
            foreach (ScoreDoc h in top.Hits)
            {
                LeafContext leaf = searcher.Context.Leaves.Last(l => l.DocBase <= h.DocId);
                string id = leaf.Reader.StoredFields!.GetDocument(h.DocId - leaf.DocBase).Get("id").AsText();
                if (id != data.Queries[q].Id && hits.Count < 10)
                {
                    hits.Add((id, h.Score));
                }
            }

            run[data.Queries[q].Id] = hits;
        }

        double searchSeconds = qsw.Elapsed.TotalSeconds;
        (double ndcg, int nq) = TrecEval.NdcgAt(10, data.Qrels, run);
        if (opt("run") is { } runFile)
        {
            using var w = new StreamWriter(runFile);
            foreach ((string qid, var hits) in run)
            {
                for (int r = 0; r < hits.Count; r++)
                {
                    w.Write($"{qid} Q0 {hits[r].Doc} {r + 1} {hits[r].Score.ToString("R", CultureInfo.InvariantCulture)} sparse\n");
                }
            }
        }

        Console.WriteLine($"set {data.Name} docs {ids.Count} queries {nq} nDCG@10 {ndcg:F4} entries/doc {(double)entries / ids.Count:F1} query encode {encodeQuerySeconds:F2} s, search {searchSeconds * 1000 / nq:F2} ms/query (encoding this run: {encoded} passages in {encodeSeconds:F1} s)");
        return 0;
    }
}
