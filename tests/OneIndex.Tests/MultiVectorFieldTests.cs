using OneIndex.Search;
using OneIndex.Vectors;

namespace OneIndex.Tests;

public sealed class TokenDoc
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Text { get; set; } = string.Empty;

    [Keyword]
    public string Lang { get; set; } = string.Empty;

    [MultiVector(Dimensions = 5)]
    public float[][]? Tokens { get; set; }
}

/// <summary>A [MultiVector] property through the generated mapping: round trip, validation, and MaxSim as a scan and as a rerank, against brute force.</summary>
public sealed class MultiVectorFieldTests
{
    private const int Dim = 5;

    private static float[][] Rows(Random rng, int n) => [.. Enumerable.Range(0, n).Select(_ => Enumerable.Range(0, Dim).Select(_ => (float)((rng.NextDouble() * 2) - 1)).ToArray())];

    private static double Brute(float[][] query, float[][]? doc)
    {
        if (doc is null || doc.Length == 0)
        {
            return 0;
        }

        double total = 0;
        foreach (float[] q in query)
        {
            total += doc.Max(d => Cos(q, d));
        }

        return total;
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

        return na == 0 || nb == 0 ? 0 : dot / Math.Sqrt(na * nb);
    }

    [Fact]
    public void ScanAndRerankEqualBruteForce()
    {
        var rng = new Random(5);
        var docs = new List<TokenDoc>();
        for (int i = 0; i < 150; i++)
        {
            docs.Add(new TokenDoc { Id = "d" + i, Text = i % 3 == 0 ? "late interaction ranking" : "other words", Lang = i % 2 == 0 ? "en" : "de", Tokens = i % 25 == 7 ? null : Rows(rng, i % 11) });
        }

        using SearchIndex<TokenDoc> index = SearchIndex.InMemory<TokenDoc>();
        index.Upsert(docs);
        index.Commit();

        // The value comes back as it went in.
        TokenDoc back = index.Search("\"late interaction\"").Take(1000).Select(h => h.Document).Single(d => d.Id == "d3");
        Assert.Equal(docs[3].Tokens!.Length, back.Tokens!.Length);
        Assert.All(back.Tokens.Zip(docs[3].Tokens!), pair => Assert.Equal(pair.Second, pair.First));

        for (int trial = 0; trial < 20; trial++)
        {
            float[][] query = Rows(rng, 1 + trial % 4);

            // Scan of the documents a filter admits.
            var en = docs.Where(d => d.Lang == "en").ToList();
            double[] expected = en.Select(d => Brute(query, d.Tokens)).OrderByDescending(x => x).Take(10).ToArray();
            List<MaxSimHit<TokenDoc>> scan = MaxSim.Search(index.Search().Where(d => d.Lang == "en"), d => d.Tokens, query, 10);
            Assert.Equal(expected.Length, scan.Count);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.Equal(expected[i], scan[i].Score, 4);
            }

            // Rerank of a first stage.
            var first = index.Search("late interaction ranking").Take(60).Execute().ToList();
            List<MaxSimHit<TokenDoc>> reranked = MaxSim.Rerank(first, d => d.Tokens, query, take: 10);
            double[] want = first.Select(h => Brute(query, h.Document.Tokens)).OrderByDescending(x => x).Take(10).ToArray();
            Assert.Equal(want.Length, reranked.Count);
            for (int i = 0; i < want.Length; i++)
            {
                Assert.Equal(want[i], reranked[i].Score, 4);
            }
        }
    }

    [Fact]
    public void ARowOfTheWrongLengthIsRefusedAtUpsert()
    {
        using SearchIndex<TokenDoc> index = SearchIndex.InMemory<TokenDoc>();
        var e = Assert.Throws<ArgumentException>(() => index.Upsert(new TokenDoc { Id = "x", Tokens = [[1f, 2f]] }));
        Assert.Contains("5 values", e.Message);
        index.Upsert(new TokenDoc { Id = "ok", Tokens = [] });
        index.Commit();
        Assert.Empty(index.Search().Take(5).Select(h => h.Document).Single().Tokens!);
    }
}
