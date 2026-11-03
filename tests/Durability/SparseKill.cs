using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using OneIndex.Vectors;

namespace OneIndex.Durability;

/// <summary>A document whose sparse vector is made by the index from its body.</summary>
public sealed class SparseDocument
{
    /// <summary>The identifier.</summary>
    [SearchId]
    public string Id { get; set; } = "";

    /// <summary>The text that is embedded.</summary>
    [FullText]
    public string Body { get; set; } = "";

    /// <summary>The learned sparse vector of the body.</summary>
    [Sparse(From = nameof(Body))]
    public OneIndex.Index.SparseVector? Vec { get; set; }
}

/// <summary>A deterministic sparse encoder that is slow enough for a kill to land while it works.</summary>
public sealed class SlowFakeSparseEncoder(int sleepMs = 0) : ISparseEncoder
{
    /// <inheritdoc/>
    public string ModelId => "fake-sparse";

    /// <summary>The vector of a text: a weight per word from the hash of the word.</summary>
    public static OneIndex.Index.SparseVector VectorOf(string text, EmbeddingRole role = EmbeddingRole.Document)
    {
        var entries = new List<KeyValuePair<string, float>>();
        foreach (string w in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            byte[] h = SHA256.HashData(Encoding.UTF8.GetBytes(w));
            entries.Add(new(w, role == EmbeddingRole.Query ? 1f : 0.5f + (h[0] / 64f)));
        }

        return new OneIndex.Index.SparseVector(entries);
    }

    /// <inheritdoc/>
    public void Encode(IReadOnlyList<string> texts, EmbeddingRole role, Span<OneIndex.Index.SparseVector> destination, CancellationToken cancellationToken = default)
    {
        if (sleepMs > 0)
        {
            Thread.Sleep(sleepMs);
        }

        for (int i = 0; i < texts.Count; i++)
        {
            destination[i] = VectorOf(texts[i], role);
        }
    }

    /// <inheritdoc/>
    public ValueTask EncodeAsync(IReadOnlyList<string> texts, EmbeddingRole role, Memory<OneIndex.Index.SparseVector> destination, CancellationToken cancellationToken = default)
    {
        Encode(texts, role, destination.Span, cancellationToken);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// The kill campaign for automatic embedding: a child process upserts documents whose vectors the index embeds, commits, and is killed at random
/// points (inside embedding, flushes and commits). The parent reopens the directory and requires the content to be exactly the last
/// acknowledged commit or the one being written, every document to carry the vector its text determines, and vector search to find it.
/// </summary>
public static class SparseKill
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

    private static SortedDictionary<string, string> Read(SearchIndex<SparseDocument> index)
    {
        var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (SearchHit<SparseDocument> hit in index.Search().Take(10_000))
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
        using SearchIndex<SparseDocument> index = SearchIndex.Open<SparseDocument>(directory, null, new SlowFakeSparseEncoder(slow ? 20 : 2));
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
                    var batch = new List<SparseDocument>();
                    for (int i = 0; i < 4; i++)
                    {
                        string bid = "doc" + rng.Next(200);
                        string bbody = "batch " + rng.Next(100_000);
                        batch.Add(new SparseDocument { Id = bid, Body = bbody });
                        model[bid] = bbody;
                    }

                    index.Upsert(batch);
                }
                else
                {
                    index.Upsert(new SparseDocument { Id = id, Body = body });
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
    public static Result Run(string directory, int kills, int seed, int maxDelayMs, string[] childCommand, bool clear = false)
    {
        var rng = new Random(seed);
        var messages = new List<string>();
        long commits = 0;
        int duringCommit = 0;
        long documents = 0;
        string lastHash = Hash([]);
        CampaignDirectory.Prepare(directory, clear);
        int failures = 0;
        for (int done = 0; done < kills; done++)
        {
            var psi = new ProcessStartInfo(childCommand[0]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (string a in childCommand.Skip(1))
            {
                psi.ArgumentList.Add(a);
            }

            psi.ArgumentList.Add("sparse-child");
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
            using SearchIndex<SparseDocument> index = SearchIndex.Open<SparseDocument>(directory, null, new SlowFakeSparseEncoder());
            var rows = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (SearchHit<SparseDocument> hit in index.Search().Take(10_000))
            {
                SparseDocument d = hit.Document;
                rows[d.Id] = d.Body;
                if (d.Vec is null || !d.Vec.Equals(SlowFakeSparseEncoder.VectorOf(d.Body)))
                {
                    return $"document {d.Id} has no sparse vector or the wrong one for its text";
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

            // Sparse search must reach the documents: a document's own words find it, with the exact dot product as the top score.
            int checkedDocs = 0;
            foreach (KeyValuePair<string, string> r in rows)
            {
                if (++checkedDocs > 5)
                {
                    break;
                }

                var query = new OneIndex.Search.SparseQuery("Vec", SlowFakeSparseEncoder.VectorOf(r.Value, EmbeddingRole.Query));
                var top = index.SearchBy(query).Take(3).ToList();
                if (top.Count == 0 || !top.Any(h => h.Document.Id == r.Key))
                {
                    return $"sparse search for {r.Key} did not find it";
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
