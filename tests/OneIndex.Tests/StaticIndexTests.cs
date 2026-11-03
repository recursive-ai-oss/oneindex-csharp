using OneIndex.Highlighting;
using OneIndex.Storage;
using OneIndex.StaticIndexes;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class StaticDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 2)]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;

    [Filterable, Facet]
    public string Topic { get; set; } = string.Empty;

    [Filterable, Sortable]
    public int Year { get; set; }

    [Vector(Dimensions = 8)]
    public float[]? Embedding { get; set; }
}

public class StaticIndexTests
{
    private static readonly string[] Words = ["river", "garden", "engine", "market", "winter", "signal", "harbor", "lantern", "meadow", "copper", "violin", "glacier", "orchard", "compass", "thunder", "saffron"];
    private static readonly string[] Topics = ["travel", "music", "tech", "food"];

    internal static List<StaticDoc> Corpus(int n)
    {
        var rng = new Random(5);
        var docs = new List<StaticDoc>();
        for (int i = 0; i < n; i++)
        {
            int topic = rng.Next(Topics.Length);
            var v = new float[8];
            for (int d = 0; d < 8; d++)
            {
                v[d] = (d == topic * 2 ? 2f : 0f) + (float)rng.NextDouble();
            }

            docs.Add(new StaticDoc
            {
                Id = "d" + i,
                Title = Words[rng.Next(Words.Length)] + " " + Words[rng.Next(Words.Length)],
                Body = string.Join(' ', Enumerable.Range(0, 30).Select(_ => Words[rng.Next(Words.Length)])),
                Topic = Topics[topic],
                Year = 1990 + rng.Next(35),
                Embedding = v,
            });
        }

        return docs;
    }

    private sealed class NamingSource(IStaticSource inner) : IStaticSource
    {
        public List<string> Names { get; } = [];

        public long BytesFetched => inner.BytesFetched;

        public int Requests => inner.Requests;

        public ValueTask<byte[]> FetchAsync(string objectName, CancellationToken cancellationToken = default)
        {
            Names.Add(objectName);
            return inner.FetchAsync(objectName, cancellationToken);
        }
    }

    [Fact]
    public async Task ATextQueryDoesNotFetchColumnsPointsOrVectors()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneindex-static-lazy-" + Guid.NewGuid().ToString("N"));
        try
        {
            string idx = Path.Combine(root, "idx");
            string stat = Path.Combine(root, "static");
            using (SearchIndex<StaticDoc> index = SearchIndex.Open<StaticDoc>(idx, null))
            {
                index.Upsert(Corpus(1200));
                index.Commit();
            }

            StaticIndexBuilder.Build<StaticDoc>(idx, stat, new StaticBuildOptions { ChunkBytes = 4096 });
            var source = new NamingSource(new DirectoryStaticSource(stat));
            StaticIndex<StaticDoc> remote = await StaticIndex.OpenAsync<StaticDoc>(source);
            using SearchIndex<StaticDoc> local = SearchIndex.OpenReadOnly<StaticDoc>(idx);

            Func<SearchIndex<StaticDoc>, List<(string, float)>> text = i => [.. i.Search("river garden").Take(10).Execute().Select(static h => (h.Document.Id, h.Score))];
            Assert.Equal(text(local), await remote.QueryAsync(text));
            // Columns and points are not read at all. The vector files are asked only for their directory (the last chunk of each file).
            Assert.DoesNotContain(source.Names, static n => n.Contains(".columns", StringComparison.Ordinal) || n.Contains(".points", StringComparison.Ordinal));
            Assert.True(source.Names.Count(static n => n.Contains(".vectors", StringComparison.Ordinal) || n.Contains(".hnsw", StringComparison.Ordinal) || n.Contains(".vecq", StringComparison.Ordinal)) <= 3);

            // The first query that does use them opens them, and the answer still equals the ordinary index.
            Func<SearchIndex<StaticDoc>, List<(string, float)>> filtered = i => [.. i.Search("river").Where("Year >= 2010").Take(10).Execute().Select(static h => (h.Document.Id, h.Score))];
            long before = source.BytesFetched;
            Assert.Equal(filtered(local), await remote.QueryAsync(filtered));
            Assert.True(source.BytesFetched > before, "the filter needs the columns, which are fetched when it first asks");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task StaticResultsEqualServerResultsAndReadFewChunks()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneindex-static-" + Guid.NewGuid().ToString("N"));
        try
        {
            string idx = Path.Combine(root, "idx");
            string stat = Path.Combine(root, "static");
            using (SearchIndex<StaticDoc> index = SearchIndex.Open<StaticDoc>(idx, null))
            {
                index.Upsert(Corpus(1500));
                index.Commit();
            }

            StaticManifest manifest = StaticIndexBuilder.Build<StaticDoc>(idx, stat, new StaticBuildOptions { ChunkBytes = 16 * 1024 });
            Assert.True(manifest.Files.Count > 3);
            Assert.Throws<IOException>(() => StaticIndexBuilder.Build<StaticDoc>(idx, stat));

            var source = new DirectoryStaticSource(stat);
            StaticIndex<StaticDoc> remote = await StaticIndex.OpenAsync<StaticDoc>(source);
            using SearchIndex<StaticDoc> local = SearchIndex.OpenReadOnly<StaticDoc>(idx);

            int compared = 0;
            string[] texts = ["river garden", "winter signal harbor", "copper", "\"lantern meadow\"", "violin -glacier", "orchard compass thunder", "saffron"];
            foreach (string text in texts)
            {
                foreach (string? filter in new string?[] { null, "Topic = music", "Year >= 2010 AND Topic != tech" })
                {
                    Func<SearchIndex<StaticDoc>, List<(string, float)>> run = i =>
                    {
                        SearchQuery<StaticDoc> q = i.Search(text);
                        if (filter is not null)
                        {
                            q = q.Where(filter);
                        }

                        return [.. q.Take(10).Execute().Select(static h => (h.Document.Id, h.Score))];
                    };

                    List<(string, float)> expected = run(local);
                    List<(string, float)> actual = await remote.QueryAsync(run);
                    Assert.Equal(expected, actual);
                    compared++;
                }
            }

            // Highlights and facets come from the same engine, so they match too.
            var hl = await remote.QueryAsync(i => i.Search("river").Highlight(new HighlightOptions()).Take(3).Execute().Select(static h => h.Highlights.Count).ToList());
            Assert.Equal([.. local.Search("river").Highlight(new HighlightOptions()).Take(3).Execute().Select(static h => h.Highlights.Count)], hl);

            Assert.Equal(21, compared);
            Assert.True(remote.Retries > 0);
            Assert.True(source.BytesFetched < new DirectoryInfo(idx).EnumerateFiles().Sum(static f => f.Length), "a query must not need the whole index");

            // Vector search through IVF finds the same neighbours as the exact scan for most queries.
            List<StaticDoc> docs = Corpus(1500);
            int agree = 0, total = 0;
            for (int q = 0; q < 20; q++)
            {
                float[] qv = docs[q * 7].Embedding!;
                var viaIvf = (await remote.SearchVectorAsync(nameof(StaticDoc.Embedding), qv, 10, nprobe: 8)).Select(static h => h.Document.Id).ToHashSet();
                var exact = docs.OrderByDescending(d => Cos(qv, d.Embedding!)).Take(10).Select(static d => d.Id).ToHashSet();
                agree += viaIvf.Intersect(exact).Count();
                total += 10;
            }

            Assert.True(agree / (double)total > 0.9, $"IVF recall {agree / (double)total}");

            // A filter on identifiers works.
            var filtered = await remote.SearchVectorAsync(nameof(StaticDoc.Embedding), docs[0].Embedding!, 5, 8, id => id.EndsWith('1'));
            Assert.All(filtered, static h => Assert.EndsWith("1", h.Document.Id));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    private static double Cos(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return dot / Math.Sqrt(na * nb);
    }

    [Fact]
    public async Task DamagedChunksAndManifestsAreRefused()
    {
        string root = Path.Combine(Path.GetTempPath(), "oneindex-static-" + Guid.NewGuid().ToString("N"));
        try
        {
            string idx = Path.Combine(root, "idx");
            string stat = Path.Combine(root, "static");
            using (SearchIndex<StaticDoc> index = SearchIndex.Open<StaticDoc>(idx, null))
            {
                index.Upsert(Corpus(200));
                index.Commit();
            }

            StaticIndexBuilder.Build<StaticDoc>(idx, stat, new StaticBuildOptions { ChunkBytes = 4096 });
            string victim = Directory.EnumerateFiles(stat).First(static f => !f.EndsWith(".json", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("commit-", StringComparison.Ordinal));
            byte[] bytes = File.ReadAllBytes(victim);
            bytes[bytes.Length / 2] ^= 0x40;
            File.WriteAllBytes(victim, bytes);
            StaticIndex<StaticDoc> remote = await StaticIndex.OpenAsync<StaticDoc>(new DirectoryStaticSource(stat));
            await Assert.ThrowsAnyAsync<CorruptIndexException>(async () =>
            {
                for (int i = 0; i < 40; i++)
                {
                    await remote.QueryAsync(ix => ix.Search("river garden engine").Take(50).Execute().Count);
                    await remote.QueryAsync(ix => ix.Search().Where("Year > 0").Take(300).Execute().Count);
                }

                await remote.SearchVectorAsync(nameof(StaticDoc.Embedding), new float[8], 5, 100);
            });

            string manifest = Path.Combine(stat, "static.json");
            byte[] json = File.ReadAllBytes(manifest);
            var rng = new Random(3);
            for (int i = 0; i < 400; i++)
            {
                byte[] bad = (byte[])json.Clone();
                bad[rng.Next(bad.Length)] = (byte)rng.Next(256);
                try
                {
                    StaticManifest.Parse(bad);
                }
                catch (CorruptIndexException)
                {
                }
            }
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
