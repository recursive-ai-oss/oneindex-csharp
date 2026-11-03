using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>
/// Encodes the source text of <c>[MultiVector(From = ...)]</c> members before a document reaches the writer, with the same cache scheme as
/// <see cref="AutoEmbedding"/>: beside the multi-vector a document stores SHA-256(model identity, text), so unchanged text under an unchanged
/// model is never encoded again (upserts, restarts, merges, schema rebuilds; docs/multi-vector.md).
/// </summary>
internal sealed class AutoMultiVector
{
    private const int BatchSize = 32;
    private const int MaxCached = 8192;
    private const int MaxCachedQueries = 256;

    private readonly Dictionary<string, byte[]> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float[][]> _queries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AutoMultiVector(IMultiVectorEncoder encoder, IReadOnlyList<MappedMember> members)
    {
        Encoder = encoder;
        var slots = new List<Slot>();
        foreach (MappedMember m in members)
        {
            if (m.Type == FieldType.Bytes && m.From is not null)
            {
                if (m.Dimensions != encoder.Dimension)
                {
                    throw new InvalidOperationException($"Multi-vector encoder '{encoder.ModelId}' produces {encoder.Dimension} dimensions, but [MultiVector] member '{m.Name}' declares {m.Dimensions}.");
                }

                slots.Add(new Slot(m.Name, m.From, KeyFieldName(m.Name), m.Dimensions));
            }
        }

        Slots = [.. slots];
    }

    public IMultiVectorEncoder Encoder { get; }

    public Slot[] Slots { get; }

    public static string KeyFieldName(string member) => member + "#multivector";

    public sealed record Slot(string Member, string From, string KeyField, int Dimensions);

    private sealed class Plan
    {
        public List<string> Texts { get; } = [];

        public List<string> CacheKeys { get; } = [];

        public Dictionary<string, int> Index { get; } = new(StringComparer.Ordinal);

        public List<(Document Doc, Slot Slot, byte[] Key, string CacheKey)> Targets { get; } = [];
    }

    private byte[] KeyOf(string text)
    {
        byte[] id = Encoding.UTF8.GetBytes(Encoder.ModelId);
        byte[] body = Encoding.UTF8.GetBytes(text);
        var all = new byte[id.Length + 1 + body.Length];
        id.CopyTo(all, 0);
        body.CopyTo(all, id.Length + 1);
        return SHA256.HashData(all);
    }

    /// <summary>Whether a stored document's multi-vector is missing or was made by another model or from other text.</summary>
    public bool IsStale(Document stored)
    {
        foreach (Slot slot in Slots)
        {
            string? text = AutoEmbedding.SourceText(stored, slot.From);
            if (text is null)
            {
                continue;
            }

            FieldValue key = stored.Get(slot.KeyField);
            if (stored.Get(slot.Member).Kind != FieldValueKind.Bytes || key.Kind != FieldValueKind.Bytes || !key.AsBytes().Span.SequenceEqual(KeyOf(text)))
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
                if (!replace && doc.Get(slot.Member).Kind != 0)
                {
                    continue; // the application supplied this vector
                }

                string? text = AutoEmbedding.SourceText(doc, slot.From);
                if (text is null)
                {
                    continue;
                }

                byte[] key = KeyOf(text);
                string ck = Convert.ToBase64String(key);
                if (TryCached(ck, out byte[]? known) || TryCommitted(doc, slot, key, idField, committed, ck, out known))
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

    private bool TryCached(string ck, out byte[]? vector)
    {
        lock (_gate)
        {
            return _vectors.TryGetValue(ck, out vector);
        }
    }

    private bool TryCommitted(Document doc, Slot slot, byte[] key, string idField, Func<string, Document?> committed, string ck, out byte[]? vector)
    {
        vector = null;
        FieldValue id = doc.Get(idField);
        if (id.Kind != FieldValueKind.Text || committed(id.AsText()) is not { } old)
        {
            return false;
        }

        FieldValue oldKey = old.Get(slot.KeyField);
        FieldValue oldVector = old.Get(slot.Member);
        if (oldKey.Kind != FieldValueKind.Bytes || oldVector.Kind != FieldValueKind.Bytes || !oldKey.AsBytes().Span.SequenceEqual(key))
        {
            return false;
        }

        vector = oldVector.AsBytes().ToArray();
        Remember(ck, vector);
        return true;
    }

    private void Remember(string ck, byte[] vector)
    {
        lock (_gate)
        {
            if (_vectors.Count >= MaxCached)
            {
                _vectors.Clear(); // the stored source is the durable cache
            }

            _vectors[ck] = vector;
        }
    }

    private static void Apply(Document doc, Slot slot, byte[] key, byte[] vector, bool replace)
    {
        if (replace)
        {
            doc.Remove(slot.Member);
            doc.Remove(slot.KeyField);
        }

        doc.Add(slot.Member, FieldValue.FromBytes(vector));
        doc.Add(slot.KeyField, FieldValue.FromBytes(key));
    }

    private void Finish(Plan plan, byte[][] results, bool replace)
    {
        for (int i = 0; i < plan.Texts.Count; i++)
        {
            Remember(plan.CacheKeys[i], results[i]);
        }

        foreach ((Document doc, Slot slot, byte[] key, string ck) in plan.Targets)
        {
            Apply(doc, slot, key, results[plan.Index[ck]], replace);
        }
    }

    private byte[] Pack(float[][]? rows)
    {
        try
        {
            return MultiVector.Pack(rows ?? throw new ArgumentException("the encoder returned null"), Encoder.Dimension);
        }
        catch (ArgumentException e)
        {
            throw new EmbeddingException($"Multi-vector encoder '{Encoder.ModelId}' returned token vectors that do not fit its declared dimension {Encoder.Dimension}: {e.Message}", e);
        }
    }

    /// <summary>Encodes what needs it and writes the token vectors and cache keys into the documents. Nothing changes when the encoder fails.</summary>
    public void Fill(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace, CancellationToken ct)
    {
        Plan plan = MakePlan(docs, idField, committed, replace);
        if (plan.Texts.Count == 0)
        {
            return;
        }

        var results = new byte[plan.Texts.Count][];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            var raw = new float[n][][];
            try
            {
                Encoder.Encode(plan.Texts.GetRange(at, n), EmbeddingRole.Document, raw, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Multi-vector encoder '{Encoder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
            }

            for (int i = 0; i < n; i++)
            {
                results[at + i] = Pack(raw[i]);
            }
        }

        Finish(plan, results, replace);
    }

    /// <summary>The asynchronous twin of <see cref="Fill"/>.</summary>
    public async ValueTask FillAsync(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace, CancellationToken ct)
    {
        Plan plan = MakePlan(docs, idField, committed, replace);
        if (plan.Texts.Count == 0)
        {
            return;
        }

        var results = new byte[plan.Texts.Count][];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            var raw = new float[n][][];
            try
            {
                await Encoder.EncodeAsync(plan.Texts.GetRange(at, n), EmbeddingRole.Document, raw.AsMemory(), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Multi-vector encoder '{Encoder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
            }

            for (int i = 0; i < n; i++)
            {
                results[at + i] = Pack(raw[i]);
            }
        }

        Finish(plan, results, replace);
    }

    /// <summary>Encodes query text with the query role (cached for repeated searches).</summary>
    public float[][] EncodeQuery(string text) => EncodeQuery(Encoder, text, _queries, _gate);

    internal static float[][] EncodeQuery(IMultiVectorEncoder encoder, string text, Dictionary<string, float[][]>? cache, object? gate)
    {
        if (cache is not null && gate is not null)
        {
            lock (gate)
            {
                if (cache.TryGetValue(text, out float[][]? known))
                {
                    return known;
                }
            }
        }

        float[][] rows;
        try
        {
            rows = encoder.Encode(text, EmbeddingRole.Query);
            _ = MultiVector.Pack(rows, encoder.Dimension); // validates length and finiteness
        }
        catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
        {
            throw new EmbeddingException($"Multi-vector encoder '{encoder.ModelId}' failed on the query text: {e.Message}", e);
        }

        if (cache is not null && gate is not null)
        {
            lock (gate)
            {
                if (cache.Count >= MaxCachedQueries)
                {
                    cache.Clear();
                }

                cache[text] = rows;
            }
        }

        return rows;
    }
}
