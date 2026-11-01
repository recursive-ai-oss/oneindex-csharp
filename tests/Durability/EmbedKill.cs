using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneIndex.Vectors;

namespace OneIndex.Durability;

/// <summary>A document whose vector is made by the index from its body.</summary>
public sealed class EmbeddedDocument
{
    /// <summary>The identifier.</summary>
    [SearchId]
    public string Id { get; set; } = "";

    /// <summary>The text that is embedded.</summary>
    [FullText]
    public string Body { get; set; } = "";

    /// <summary>The embedding of the body.</summary>
    [Vector(From = nameof(Body), Dimensions = 8, FlatThreshold = 3, M = 4, EfConstruction = 16)]
    public float[]? Vec { get; set; }
}

/// <summary>A deterministic embedder that is slow enough for a kill to land while it works.</summary>
public sealed class SlowFakeEmbedder(int sleepMs = 0) : IEmbedder
{
    /// <inheritdoc/>
    public string ModelId => "fake-8";

    /// <inheritdoc/>
    public int Dimension => 8;

    /// <inheritdoc/>
    public bool IsNormalized => false;

    /// <summary>The vector of a text: a function of its SHA-256.</summary>
    public static float[] VectorOf(string text, EmbeddingRole role = EmbeddingRole.Document)
    {
        byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var v = new float[8];
        for (int i = 0; i < 8; i++)
        {
            v[i] = (h[i] - 127.5f) / 128f + 0.01f;
        }

        return v;
    }

    /// <inheritdoc/>
    public void Embed(IReadOnlyList<string> texts, EmbeddingRole role, Span<float> destination, CancellationToken cancellationToken = default)
    {
        if (sleepMs > 0)
        {
            Thread.Sleep(sleepMs);
        }

        for (int i = 0; i < texts.Count; i++)
        {
            VectorOf(texts[i], role).CopyTo(destination.Slice(i * 8, 8));
        }
    }

    /// <inheritdoc/>
    public ValueTask EmbedAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<float> destination, CancellationToken cancellationToken = default)
    {
        Embed(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The kill campaign for automatic embedding: a child process upserts documents whose vectors the index embeds, commits, and is killed at random
/// points (inside embedding, flushes and commits). The parent reopens the directory and requires the content to be exactly the last
/// acknowledged commit or the one being written, every document to carry the vector its text determines, and vector search to find it.
/// </summary>
public static class EmbedKill
{
    /// <summary>The result of a campaign.</summary>
    public sealed record Result(int Kills, int Failures, List<string> Messages, long Commits, int KilledDuringCommit, long Documents);

    private static string Hash(SortedDictionary<string, string> rows)
    {
        var sb = new StringBuilder();
        foreach (KeyValuePair<string, string> r in rows)
        {
            sb.Append(r.Key).Append('|').Append(r.Value).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..16];
    }

    private static SortedDictionary<string, string> Read(SearchIndex<EmbeddedDocument> index)
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (SearchHit<EmbeddedDocument> hit in index.Search().Take(10_000))
        {
            rows[hit.Document.Id] = hit.Document.Body;
        }

        return rows;
    }

    /// <summary>The writer child: random upserts and deletes with a commit every few operations, reporting PRE/ACK lines.</summary>
    public static int RunChild(string directory, int seed)
    {
        // Odd seeds embed slowly and commit rarely, so kills land inside embedding; even seeds commit often, so they land inside commits.
        bool slow = seed % 2 != 0;
        using SearchIndex<EmbeddedDocument> index = SearchIndex.Open<EmbeddedDocument>(directory, new SlowFakeEmbedder(slow ? 20 : 2));
        SortedDictionary<string, string> model = Read(index);
        Console.Out.WriteLine("READY");
        var rng = new Random(seed);
        int since = 0;
        while (true)
        {
            string id = "doc" + rng.Next(200);
            if (rng.Next(100) < 80)
            {
                string body = "text " + rng.Next(100_000);
                if (rng.Next(4) == 0)
                {
                    // A batch call: several documents embedded together.
                    var batch = new List<EmbeddedDocument>();
                    for (int i = 0; i < 4; i++)
                    {
                        string bid = "doc" + rng.Next(200);
                        string bbody = "batch " + rng.Next(100_000);
                        batch.Add(new EmbeddedDocument { Id = bid, Body = bbody });
                        model[bid] = bbody;
                    }

                    index.Upsert(batch);
                }
                else
                {
                    index.Upsert(new EmbeddedDocument { Id = id, Body = body });
                    model[id] = body;
                }
            }
            else
            {
                index.Delete(id);
                model.Remove(id);
            }

            if (++since >= (slow ? 8 + rng.Next(30) : 2 + rng.Next(15)))
            {
                since = 0;
                Console.Out.WriteLine("PRE " + Hash(model));
                long gen = index.Commit();
                Console.Out.WriteLine($"ACK {gen} {Hash(model)}");
            }
        }
    }

    /// <summary>Runs the campaign in one directory.</summary>
    public static Result Run(string directory, int kills, int seed, int maxDelayMs, string[] childCommand)
    {
        var rng = new Random(seed);
        var messages = new List<string>();
        long commits = 0;
        int duringCommit = 0;
        long documents = 0;
        string lastHash = Hash([]);
        Directory.CreateDirectory(directory);
        int failures = 0;
        for (int done = 0; done < kills; done++)
        {
            var psi = new ProcessStartInfo(childCommand[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in childCommand.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("embed-child");
            psi.ArgumentList.Add("--dir");
            psi.ArgumentList.Add(directory);
            psi.ArgumentList.Add("--seed");
            psi.ArgumentList.Add(rng.Next().ToString(CultureInfo.InvariantCulture));
            using Process child = Process.Start(psi)!;
            var lines = new List<string>();
            child.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (lines)
                    {
                        lines.Add(e.Data);
                    }
                }
            };
            child.BeginOutputReadLine();
            child.ErrorDataReceived += (_, _) => { };
            child.BeginErrorReadLine();
            int delay = 150 + (int)(maxDelayMs * Math.Pow(rng.NextDouble(), 1.3)); // the runtime starts in about 100 ms
            if (child.WaitForExit(delay))
            {
                messages.Add($"kill {done}: the child ended by itself with exit code {child.ExitCode}");
                failures++;
                break;
            }

            child.Kill(entireProcessTree: true);
            child.WaitForExit();
            string[] output;
            lock (lines)
            {
                output = [.. lines];
            }

            string? pre = null;
            foreach (string line in output)
            {
                if (line.StartsWith("PRE ", StringComparison.Ordinal))
                {
                    pre = line[4..];
                }
                else if (line.StartsWith("ACK ", StringComparison.Ordinal))
                {
                    lastHash = line.Split(' ')[2];
                    pre = null;
                    commits++;
                }
            }

            string? failure = Verify(directory, lastHash, pre, out string hash, out int count);
            documents += count;
            if (pre is not null)
            {
                duringCommit++;
            }

            if (failure is not null)
            {
                failures++;
                messages.Add($"kill {done}: {failure}");
                break;
            }

            lastHash = hash; // the content is now fixed, whether or not the last commit was acknowledged
        }

        return new Result(kills, failures, messages, commits, duringCommit, documents);
    }

    private static string? Verify(string directory, string ackHash, string? pre, out string hash, out int count)
    {
        hash = "";
        count = 0;
        try
        {
            using SearchIndex<EmbeddedDocument> index = SearchIndex.Open<EmbeddedDocument>(directory, new SlowFakeEmbedder());
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (SearchHit<EmbeddedDocument> hit in index.Search().Take(10_000))
            {
                EmbeddedDocument d = hit.Document;
                rows[d.Id] = d.Body;
                float[] expected = SlowFakeEmbedder.VectorOf(d.Body);
                if (d.Vec is null || d.Vec.Length != 8 || d.Vec.Zip(expected, (x, y) => Math.Abs(x - y)).Max() > 1e-6f)
                {
                    return $"document {d.Id} has no vector or the wrong one for its text";
                }
            }

            count = rows.Count;
            hash = Hash(rows);
            if (hash != ackHash && hash != pre)
            {
                return $"content hash {hash} is neither the last acknowledged commit {ackHash} nor the one in flight {pre ?? "(none)"}";
            }

            if (index.Count != rows.Count)
            {
                return $"Count {index.Count} but {rows.Count} documents are readable";
            }

            // Vector search must reach the documents: the nearest to a document's own embedding is that document.
            int checkedDocs = 0;
            foreach (KeyValuePair<string, string> r in rows)
            {
                if (++checkedDocs > 5)
                {
                    break;
                }

                float[] q = SlowFakeEmbedder.VectorOf(r.Value);
                var top = index.SearchVector(x => x.Vec, q, 3).Take(3).ToList();
                if (top.Count == 0 || top[0].Score < 0.999f)
                {
                    return $"vector search for {r.Key} did not find a perfect match";
                }
            }

            return null;
        }
        catch (Exception e)
        {
            return "reopen failed: " + e.GetType().Name + ": " + e.Message;
        }
    }
}
