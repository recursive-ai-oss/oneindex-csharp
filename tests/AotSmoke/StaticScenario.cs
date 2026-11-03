using OneIndex.StaticIndexes;
using OneIndex.Storage;
using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>The static index, the IVF index, multi-vector scoring and read-only opens: build, read through the chunk source, compare with the local engine.</summary>
internal static class StaticScenario
{
    /// <summary>The IVF index and multi-vector scoring, over memory; runs in the browser runtime too.</summary>
    public static async Task RunVectorsAsync()
    {
        // IVF over memory: a full probe equals the exact search.
        var rng = new Random(11);
        const int dims = 6;
        const int count = 300;
        var data = new float[count * dims];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (float)(rng.NextDouble() - 0.5);
        }

        var query = new float[dims];
        for (int i = 0; i < dims; i++)
        {
            query[i] = (float)(rng.NextDouble() - 0.5);
        }

        var mem = new MemoryStorage();
        IvfBuilder.Write(mem, "v.ivf", data, dims, new IvfOptions { Storage = IvfStorage.EightBit, Lists = 12, Seed = 2 });
        using (IvfIndex ivf = await IvfIndex.OpenAsync(await mem.OpenReadAsync("v.ivf")))
        {
            IvfHit[] hits = await ivf.SearchAsync(query, 5, ivf.Lists);
            Require(hits.Length == 5, "ivf hits");
            int best = 0;
            double bestScore = double.MinValue;
            for (int i = 0; i < count; i++)
            {
                double dot = 0, a = 0, b = 0;
                for (int d = 0; d < dims; d++)
                {
                    dot += data[(i * dims) + d] * query[d];
                    a += data[(i * dims) + d] * data[(i * dims) + d];
                    b += query[d] * query[d];
                }

                double cos = dot / Math.Sqrt(a * b);
                if (cos > bestScore)
                {
                    bestScore = cos;
                    best = i;
                }
            }

            Require(hits[0].Id == best, "ivf nearest");
        }

        // MaxSim: the document with the matching token vectors scores highest.
        float[][] q = [[1, 0, 0], [0, 1, 0]];
        float[] same = Flatten([[1, 0, 0], [0, 1, 0], [0, 0, 1]]);
        float[] other = Flatten([[0, 0, 1], [0, 0, 1]]);
        Require(MaxSim.Score(Flatten(q), same, 3) > MaxSim.Score(Flatten(q), other, 3), "maxsim order");
        Require(MultiVector.Unpack(MultiVector.Pack([[1, 2, 3], [4, 5, 6]]), out int tokens, out int dimension).Length == 6 && tokens == 2 && dimension == 3, "multivector round trip");

    }

    /// <summary>Builds a static index on disk (the index directory takes a writer lock, so this is for the desktop runtimes) and reads it through the chunk source.</summary>
    public static async Task RunAsync(string scratchDirectory)
    {
        await RunVectorsAsync();
        var rng = new Random(12);
        var query = new float[6];
        for (int i = 0; i < query.Length; i++)
        {
            query[i] = (float)(rng.NextDouble() - 0.5);
        }

        // A static index of a small index: queries through the chunk source equal the local engine's.
        string indexDir = Path.Combine(scratchDirectory, "index");
        string staticDir = Path.Combine(scratchDirectory, "static");
        using (SearchIndex<Clip> index = SearchIndex.Open<Clip>(indexDir))
        {
            for (int i = 0; i < 80; i++)
            {
                var v = new float[6];
                for (int d = 0; d < v.Length; d++)
                {
                    v[d] = (float)(rng.NextDouble() - 0.5);
                }

                index.Upsert(new Clip { Id = "c" + i, Kind = i % 3 == 0 ? "a" : "b", Embedding = v });
            }

            index.Commit();
        }

        StaticIndexBuilder.Build<Clip>(indexDir, staticDir, new StaticBuildOptions { ChunkBytes = 4096 });
        List<string> local;
        using (SearchIndex<Clip> readOnly = SearchIndex.OpenReadOnly<Clip>(indexDir))
        {
            local = [.. readOnly.Search().Where(c => c.Kind == "a").Take(50).Execute().Select(static h => h.Document.Id).Order(StringComparer.Ordinal)];
        }

        StaticIndex<Clip> remote = await StaticIndex.OpenAsync<Clip>(new DirectoryStaticSource(staticDir));
        List<string> viaChunks = await remote.QueryAsync(i => i.Search().Where(c => c.Kind == "a").Take(50).Execute().Select(static h => h.Document.Id).Order(StringComparer.Ordinal).ToList());
        Require(local.Count == 27 && local.SequenceEqual(viaChunks), "static index equals the local engine");
        var nearest = await remote.SearchVectorAsync("Embedding", query, 3, nprobe: 100);
        Require(nearest.Count == 3, "static vector search");
    }

    private static float[] Flatten(float[][] vectors) => [.. vectors.SelectMany(static v => v)];

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Static scenario failed: " + what);
        }
    }
}
