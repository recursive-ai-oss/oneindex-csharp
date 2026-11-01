using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using OneIndex.Analysis;
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

    private static List<SparseVector> ReadSparseChunks(string work, int chunkCount, int total)
    {
        var all = new List<SparseVector>(total);
        for (int c = 0; c < chunkCount; c++)
        {
            using var r = new BinaryReader(File.OpenRead(Path.Combine(work, $"corpus-{c:D5}.sparse")));
            while (r.BaseStream.Position < r.BaseStream.Length)
            {
                int n = r.ReadInt32();
                var list = new List<KeyValuePair<string, float>>(n);
                for (int k = 0; k < n; k++)
                {
                    list.Add(new(r.ReadString(), r.ReadSingle()));
                }

                all.Add(new SparseVector(list));
            }
        }

        return all;
    }

    private static long DirectorySize(string dir) => new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);

    /// <summary>
    /// Hybrid with the sparse arm over cached vectors: BM25 (title and text), the dense bge vectors of a finished beir run and the sparse
    /// vectors of a finished sparse-beir run go into one on-disk index; each configuration runs through the engine's query objects.
    /// Also builds a sparse-only on-disk index and reports its size and query latency.
    /// </summary>
    public static int Hybrid(Func<string, string?> opt)
    {
        var data = BeirDataset.Open(Path.Combine(opt("data")!, opt("set")!));
        string sparseWork = Path.Combine(opt("work")!, opt("set")!);
        string denseWork = Path.Combine(opt("dense-work")!, opt("set")!);
        string outDir = opt("out")!;
        Directory.CreateDirectory(outDir);
        string baseDir = Path.Combine(opt("index-dir")!, opt("set")!);
        if (Directory.Exists(baseDir))
        {
            Directory.Delete(baseDir, recursive: true);
        }

        var docs = data.ReadCorpus().ToList();
        const int SparseChunk = 200;
        const int DenseChunk = 500;
        List<SparseVector> sparse = ReadSparseChunks(sparseWork, (docs.Count + SparseChunk - 1) / SparseChunk, docs.Count);
        if (sparse.Count != docs.Count)
        {
            throw new InvalidOperationException($"sparse cache has {sparse.Count} vectors for {docs.Count} documents");
        }

        using var embedder = BertEmbedder.Open(opt("model")!, opt("vocab")!, new BertEmbedderOptions
        {
            ModelId = "tool",
            QueryPrefix = "Represent this sentence for searching relevant passages: ",
            MaxThreads = int.Parse(opt("threads") ?? "0", CultureInfo.InvariantCulture),
        });
        int d = embedder.Dimension;
        var dense = new float[docs.Count * d];
        for (int c = 0; c * DenseChunk < docs.Count; c++)
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(denseWork, $"corpus-{c:D5}.f32"));
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw).CopyTo(dense.AsSpan(c * DenseChunk * d));
        }

        FieldDefinition Text(string name, float boost = 1f) => new(name, FieldType.Text) { Analyzer = "en", IndexOptions = IndexOptions.Freqs, Stored = false, Boost = boost };
        Schema schema = new SchemaBuilder().Id("id").Add(Text("title", 3f)).Add(Text("text"))
            .Add(new FieldDefinition("vec", FieldType.Vector) { Dimensions = d, Metric = VectorMetric.Cosine, Stored = false }).Sparse("s").Build();
        string fullDir = Path.Combine(baseDir, "full");
        Directory.CreateDirectory(fullDir);
        using (IndexWriter writer = IndexWriter.Open(new FileSystemStorage(fullDir), schema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 }))
        {
            for (int i = 0; i < docs.Count; i++)
            {
                writer.AddUnique(new Document().Add("id", docs[i].Id).Add("title", docs[i].Title).Add("text", docs[i].Text)
                    .Add("vec", dense.AsSpan(i * d, d).ToArray()).Add("s", sparse[i]));
            }

            writer.Commit();
            writer.ForceMerge(1);
            writer.Commit();
        }

        string sparseDir = Path.Combine(baseDir, "sparse-only");
        Directory.CreateDirectory(sparseDir);
        var sparseSchema = new SchemaBuilder().Id("id").Sparse("s").Build();
        using (IndexWriter writer = IndexWriter.Open(new FileSystemStorage(sparseDir), sparseSchema, new IndexWriterOptions { MaxBufferedDocuments = 20_000 }))
        {
            for (int i = 0; i < docs.Count; i++)
            {
                writer.AddUnique(new Document().Add("id", docs[i].Id).Add("s", sparse[i]));
            }

            writer.Commit();
            writer.ForceMerge(1);
            writer.Commit();
        }

        Console.WriteLine($"set {data.Name} index bytes: full {DirectorySize(fullDir)}, sparse-only {DirectorySize(sparseDir)}");

        float[] queryVectors = embedder.EmbedBatch(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query);
        using var sparseEncoder = Open(opt);
        var qsparse = new SparseVector[data.Queries.Count];
        sparseEncoder.Encode(data.Queries.Select(q => q.Text).ToList(), EmbeddingRole.Query, qsparse);

        // Both searchers stay open for the queries.
        using IndexWriter fullWriter = IndexWriter.Open(new FileSystemStorage(fullDir), schema, new IndexWriterOptions());
        using IndexSnapshot snapshot = fullWriter.OpenSnapshot();
        SearchContext context = SnapshotSearch.CreateContext(snapshot);
        var idOf = new string[context.MaxDoc];
        foreach (LeafContext leaf in context.Leaves)
        {
            for (int i = 0; i < leaf.Reader.MaxDoc; i++)
            {
                idOf[leaf.DocBase + i] = leaf.Reader.StoredFields!.GetDocument(i).Get("id").AsText();
            }
        }

        var searcher = new IndexSearcher(context, new QueryEngine());
        Query TextQuery(int q)
        {
            var terms = new List<string>();
            TokenSource tokens = AnalyzerRegistry.Create("en").GetTokens(data.Queries[q].Text.AsMemory());
            while (tokens.MoveNext())
            {
                terms.Add(tokens.Token.Term.ToString());
            }

            return new BooleanQuery([.. terms.Select(t => new BooleanClause(new MultiFieldTermQuery(["title", "text"], t), Occur.Should))]);
        }

        const int Window = 100;
        var configs = new List<(string Name, Func<Query, KnnQuery, SparseQuery, Query> Make)>
        {
            ("bm25", (t, _, _) => t),
            ("dense", (_, k, _) => k),
            ("sparse", (_, _, s) => s),
            ("rrf-text-dense", (t, k, _) => new HybridQuery(t, k)),
            ("rrf-text-sparse", (t, _, s) => new HybridQuery(t, null) { Sparse = s }),
            ("rrf-text-dense-sparse", (t, k, s) => new HybridQuery(t, k) { Sparse = s }),
            ("linear-text-dense-sparse", (t, k, s) => new HybridQuery(t, k) { Sparse = s, Fusion = FusionMethod.Linear }),
        };
        foreach ((string name, var make) in configs)
        {
            var run = new Dictionary<string, List<(string Doc, double Score)>>(StringComparer.Ordinal);
            var sw = Stopwatch.StartNew();
            for (int q = 0; q < data.Queries.Count; q++)
            {
                var knn = new KnnQuery("vec", queryVectors.AsSpan(q * d, d).ToArray(), Window) { Exact = true };
                TopDocs top = searcher.Search(make(TextQuery(q), knn, new SparseQuery("s", qsparse[q])), 11);
                var list = new List<(string, double)>(10);
                foreach (ScoreDoc sd in top.Hits)
                {
                    if (idOf[sd.DocId] != data.Queries[q].Id && list.Count < 10)
                    {
                        list.Add((idOf[sd.DocId], sd.Score));
                    }
                }

                run[data.Queries[q].Id] = list;
            }

            double ms = sw.Elapsed.TotalMilliseconds / data.Queries.Count;
            (double ndcg, int n) = TrecEval.NdcgAt(10, data.Qrels, run);
            Console.WriteLine($"set {data.Name} config {name} queries {n} nDCG@10 {ndcg:F4} ms/query {ms:F2}");
        }

        // Sparse-only latency on its own on-disk index (warm, single thread).
        using IndexWriter sw2 = IndexWriter.Open(new FileSystemStorage(sparseDir), sparseSchema, new IndexWriterOptions());
        using IndexSnapshot snap2 = sw2.OpenSnapshot();
        IndexSearcher s2 = SnapshotSearch.CreateSearcher(snap2);
        for (int pass = 0; pass < 2; pass++)
        {
            var t = Stopwatch.StartNew();
            for (int q = 0; q < data.Queries.Count; q++)
            {
                s2.Search(new SparseQuery("s", qsparse[q]), 10);
            }

            Console.WriteLine($"set {data.Name} sparse-only on disk pass {pass}: {t.Elapsed.TotalMilliseconds / data.Queries.Count:F2} ms/query (top 10)");
        }

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
