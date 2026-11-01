using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>
/// Embeds the source text of <c>[Vector(From = ...)]</c> members before a document reaches the writer, and keeps the embedding cache.
/// The cache is the stored source itself: beside the vector, a document stores the SHA-256 of (model identity, text), so a document whose text
/// and model are unchanged keeps its vector across upserts, restarts, merges and schema rebuilds (docs/auto-embedding.md). A small in-memory map
/// serves documents not yet committed and identical texts in different documents.
/// </summary>
internal sealed class AutoEmbedding
{
    private const int BatchSize = 32;
    private const int MaxCachedVectors = 8192;
    private const int MaxCachedQueries = 256;

    private readonly Dictionary<string, float[]> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[]> _queries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AutoEmbedding(IEmbedder embedder, IReadOnlyList<MappedMember> members)
    {
        Embedder = embedder;
        var slots = new List<Slot>();
        foreach (MappedMember m in members)
        {
            if (m.From is null || m.Type != FieldType.Vector)
            {
                continue;
            }

            if (m.Dimensions != embedder.Dimension)
            {
                throw new InvalidOperationException($"Embedder '{embedder.ModelId}' produces {embedder.Dimension} dimensions, but [Vector] member '{m.Name}' declares {m.Dimensions}.");
            }

            slots.Add(new Slot(m.Name, m.From, KeyFieldName(m.Name)));
        }

        Slots = [.. slots];
    }

    public IEmbedder Embedder { get; }

    public Slot[] Slots { get; }

    public static string KeyFieldName(string vectorMember) => vectorMember + "#embedding";

    public sealed record Slot(string Vector, string From, string KeyField);

    private sealed class Plan
    {
        public List<string> Texts { get; } = [];

        public List<string> CacheKeys { get; } = [];

        public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);

        public List<(Document Doc, Slot Slot, byte[] Key, string CacheKey)> Targets { get; } = [];
    }

    private byte[] KeyOf(string text)
    {
        // The model identity separates models; a zero byte separates it from the text (an identity never contains one).
        byte[] id = Encoding.UTF8.GetBytes(Embedder.ModelId);
        byte[] body = Encoding.UTF8.GetBytes(text);
        var all = new byte[id.Length + 1 + body.Length];
        id.CopyTo(all, 0);
        body.CopyTo(all, id.Length + 1);
        return SHA256.HashData(all);
    }

    public static string? SourceText(Document doc, string field)
    {
        StringBuilder? sb = null;
        string? first = null;
        foreach (DocumentField f in doc.Fields)
        {
            if (f.Value.Kind == FieldValueKind.Text && string.Equals(f.Name, field, StringComparison.Ordinal))
            {
                if (first is null)
                {
                    first = f.Value.AsText();
                }
                else
                {
                    (sb ??= new StringBuilder(first)).Append('\n').Append(f.Value.AsText());
                }
            }
        }

        return sb?.ToString() ?? first;
    }

    /// <summary>Whether a stored document's vector is missing or was made by another model or from other text.</summary>
    public bool IsStale(Document stored)
    {
        foreach (Slot slot in Slots)
        {
            string? text = SourceText(stored, slot.From);
            if (text is null)
            {
                continue;
            }

            FieldValue key = stored.Get(slot.KeyField);
            if (stored.Get(slot.Vector).Kind != FieldValueKind.Vector || key.Kind != FieldValueKind.Bytes || !key.AsBytes().Span.SequenceEqual(KeyOf(text)))
            {
                return true;
            }
        }

        return false;
    }

    private Plan MakePlan(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace)
    {
        var plan = new Plan();
        foreach (Document doc in docs)
        {
            foreach (Slot slot in Slots)
            {
                if (!replace && doc.Get(slot.Vector).Kind != 0)
                {
                    continue; // the application supplied this vector
                }

                string? text = SourceText(doc, slot.From);
                if (text is null)
                {
                    continue;
                }

                byte[] key = KeyOf(text);
                string ck = Convert.ToBase64String(key);
                if (TryCached(ck, out float[]? known) || TryCommitted(doc, slot, key, idField, committed, ck, out known))
                {
                    Apply(doc, slot, key, known!, replace);
                    continue;
                }

                if (!plan.Index.ContainsKey(ck))
                {
                    plan.Index[ck] = plan.Texts.Count;
                    plan.Texts.Add(text);
                    plan.CacheKeys.Add(ck);
                }

                plan.Targets.Add((doc, slot, key, ck));
            }
        }

        return plan;
    }

    private bool TryCached(string ck, out float[]? vector)
    {
        lock (_gate)
        {
            return _vectors.TryGetValue(ck, out vector);
        }
    }

    private bool TryCommitted(Document doc, Slot slot, byte[] key, string idField, Func<string, Document?> committed, string ck, out float[]? vector)
    {
        vector = null;
        FieldValue id = doc.Get(idField);
        if (id.Kind != FieldValueKind.Text || committed(id.AsText()) is not { } old)
        {
            return false;
        }

        FieldValue oldKey = old.Get(slot.KeyField);
        FieldValue oldVector = old.Get(slot.Vector);
        if (oldKey.Kind != FieldValueKind.Bytes || oldVector.Kind != FieldValueKind.Vector || !oldKey.AsBytes().Span.SequenceEqual(key))
        {
            return false;
        }

        vector = oldVector.AsVector().ToArray();
        Remember(ck, vector);
        return true;
    }

    private void Remember(string ck, float[] vector)
    {
        lock (_gate)
        {
            if (_vectors.Count >= MaxCachedVectors)
            {
                _vectors.Clear(); // simple and deterministic: the stored source is the durable cache
            }

            _vectors[ck] = vector;
        }
    }

    private static void Apply(Document doc, Slot slot, byte[] key, float[] vector, bool replace)
    {
        if (replace)
        {
            doc.Remove(slot.Vector);
            doc.Remove(slot.KeyField);
        }

        doc.Add(slot.Vector, (float[])vector.Clone());
        doc.Add(slot.KeyField, FieldValue.FromBytes(key));
    }

    private void Finish(Plan plan, float[] results)
    {
        int dim = Embedder.Dimension;
        for (int i = 0; i < plan.Texts.Count; i++)
        {
            Remember(plan.CacheKeys[i], results.AsSpan(i * dim, dim).ToArray());
        }

        foreach ((Document doc, Slot slot, byte[] key, string ck) in plan.Targets)
        {
            TryCached(ck, out float[]? v);
            Apply(doc, slot, key, v ?? results.AsSpan(plan.Index[ck] * dim, dim).ToArray(), replace: doc.Get(slot.Vector).Kind != 0);
        }
    }

    private void Check(ReadOnlySpan<float> batch, int count)
    {
        int dim = Embedder.Dimension;
        for (int i = 0; i < count * dim; i++)
        {
            if (!float.IsFinite(batch[i]))
            {
                throw new EmbeddingException($"Embedder '{Embedder.ModelId}' returned a NaN or an infinite value.");
            }
        }
    }

    /// <summary>Adds the vector and cache key of every automatically embedded member the documents lack. Throws before changing the index.</summary>
    public void Fill(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace, CancellationToken ct)
    {
        Plan plan = MakePlan(docs, idField, committed, replace);
        if (plan.Texts.Count == 0)
        {
            return;
        }

        int dim = Embedder.Dimension;
        var results = new float[plan.Texts.Count * dim];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            try
            {
                Embedder.Embed(plan.Texts.GetRange(at, n), EmbeddingRole.Document, results.AsSpan(at * dim, n * dim), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Embedder '{Embedder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
            }

            Check(results.AsSpan(at * dim, n * dim), n);
        }

        Finish(plan, results);
    }

    /// <summary>The asynchronous twin of <see cref="Fill"/>.</summary>
    public async ValueTask FillAsync(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace, CancellationToken ct)
    {
        Plan plan = MakePlan(docs, idField, committed, replace);
        if (plan.Texts.Count == 0)
        {
            return;
        }

        int dim = Embedder.Dimension;
        var results = new float[plan.Texts.Count * dim];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            try
            {
                await Embedder.EmbedAsync(plan.Texts.GetRange(at, n), EmbeddingRole.Document, results.AsMemory(at * dim, n * dim), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Embedder '{Embedder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
            }

            Check(results.AsSpan(at * dim, n * dim), n);
        }

        Finish(plan, results);
    }

    /// <summary>Embeds query text with the query role (cached for repeated searches).</summary>
    public float[] EmbedQuery(string text)
    {
        lock (_gate)
        {
            if (_queries.TryGetValue(text, out float[]? known))
            {
                return known;
            }
        }

        float[] vector;
        try
        {
            vector = Embedder.Embed(text, EmbeddingRole.Query);
        }
        catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
        {
            throw new EmbeddingException($"Embedder '{Embedder.ModelId}' failed on the query text: {e.Message}", e);
        }

        Check(vector, 1);
        lock (_gate)
        {
            if (_queries.Count >= MaxCachedQueries)
            {
                _queries.Clear();
            }

            _queries[text] = vector;
        }

        return vector;
    }
}
