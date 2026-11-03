using OneIndex.Vectors;

namespace OneIndex.AotSmoke;

/// <summary>A document with a token-vector property, the generated mapping for it and MaxSim over it.</summary>
public sealed class TokenPassage
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Text { get; set; } = "";

    [MultiVector(Dimensions = 3)]
    public float[][]? Tokens { get; set; }
}

/// <summary>A document whose token vectors are encoded from its text at upsert.</summary>
public sealed class EncodedPassage
{
    [SearchId]
    public string Id { get; set; } = "";

    [FullText]
    public string Text { get; set; } = "";

    [MultiVector(From = nameof(Text), Dimensions = 3)]
    public float[][]? Tokens { get; set; }
}

/// <summary>One unit vector per word: the first letter picks the axis.</summary>
internal sealed class AxisEncoder : IMultiVectorEncoder
{
    public string ModelId => "axis-1";

    public int Dimension => 3;

    public float[][] Encode(string text, EmbeddingRole role, CancellationToken cancellationToken = default)
    {
        var rows = new List<float[]>();
        foreach (string w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var v = new float[3];
            v[char.ToLowerInvariant(w[0]) % 3] = 1;
            rows.Add(v);
        }

        return [.. rows];
    }
}

internal static class MultiVectorScenario
{
    public static void Run()
    {
        RunEncoded();
        using SearchIndex<TokenPassage> index = SearchIndex.InMemory<TokenPassage>();
        index.Upsert(new TokenPassage { Id = "a", Text = "alpha river", Tokens = [[1, 0, 0], [0, 1, 0]] });
        index.Upsert(new TokenPassage { Id = "b", Text = "beta river", Tokens = [[0, 0, 1]] });
        index.Commit();
        var best = MaxSim.Rerank(index.Search("river").Take(10).Execute(), p => p.Tokens, [[0f, 1f, 0f]], take: 2);
        if (best.Count != 2 || best[0].Hit.Id != "a" || Math.Abs(best[0].Score - 1) > 1e-6)
        {
            throw new InvalidOperationException("MaxSim over a [MultiVector] property gave the wrong order.");
        }

        if (index.Search("alpha").Take(1).Select(h => h.Document.Tokens!.Length).Single() != 2)
        {
            throw new InvalidOperationException("The token vectors did not round-trip.");
        }
    }

    private static void RunEncoded()
    {
        using SearchIndex<EncodedPassage> index = SearchIndex.InMemory<EncodedPassage>(null, null, null, new AxisEncoder());
        index.Upsert(new EncodedPassage { Id = "a", Text = "apple river" });
        index.Upsert(new EncodedPassage { Id = "b", Text = "bravo river" });
        index.Commit();
        SearchResults<EncodedPassage> hits = index.Search("river").MaxSim(query: "bravo").Take(2).Execute();
        if (hits.Count != 2 || hits[0].Id != "b" || hits[0].FirstStageScore is null)
        {
            throw new InvalidOperationException("MaxSim over encoded token vectors gave the wrong order.");
        }

        if (index.Search("apple").Take(1).Select(h => h.Document.Tokens!.Length).Single() != 2)
        {
            throw new InvalidOperationException("The encoded token vectors did not round-trip.");
        }
    }
}
