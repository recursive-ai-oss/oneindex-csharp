using System.Security.Cryptography;
using System.Text;
using OneIndex.Index;
using OneIndex.Vectors;

namespace OneIndex;

/// <summary>
/// Encodes the source text of <c>[Sparse(From = ...)]</c> members before a document reaches the writer, with the same cache scheme as
/// <see cref="AutoEmbedding"/>: beside the sparse vector a document stores SHA-256(model identity, text), so unchanged text under an unchanged
/// model is never encoded again (upserts, restarts, merges, schema rebuilds; docs/sparse.md).
/// </summary>
internal sealed class AutoSparse
{
    private const int BatchSize = 32;
    private const int MaxCached = 8192;
    private const int MaxCachedQueries = 256;

    private readonly Dictionary<string, SparseVector> _vectors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SparseVector> _queries = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public AutoSparse(ISparseEncoder encoder, IReadOnlyList<MappedMember> members)
    {
        Encoder = encoder;
        var slots = new List<Slot>();
        foreach (MappedMember m in members)
        {
            if (m.Type == FieldType.Sparse && m.From is not null)
            {
                slots.Add(new Slot(m.Name, m.From, KeyFieldName(m.Name)));
            }
        }

        Slots = [.. slots];
    }

    public ISparseEncoder Encoder { get; }

    public Slot[] Slots { get; }

    public static string KeyFieldName(string member) => member + "#sparse";

    public sealed record Slot(string Sparse, string From, string KeyField);

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

    /// <summary>Whether a stored document's sparse vector is missing or was made by another model or from other text.</summary>
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
            if (stored.Get(slot.Sparse).Kind != FieldValueKind.Sparse || key.Kind != FieldValueKind.Bytes || !key.AsBytes().Span.SequenceEqual(KeyOf(text)))
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
                if (!replace && doc.Get(slot.Sparse).Kind != 0)
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
                if (TryCached(ck, out SparseVector? known) || TryCommitted(doc, slot, key, idField, committed, ck, out known))
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

    private bool TryCached(string ck, out SparseVector? vector)
    {
        lock (_gate)
        {
            return _vectors.TryGetValue(ck, out vector);
        }
    }

    private bool TryCommitted(Document doc, Slot slot, byte[] key, string idField, Func<string, Document?> committed, string ck, out SparseVector? vector)
    {
        vector = null;
        FieldValue id = doc.Get(idField);
        if (id.Kind != FieldValueKind.Text || committed(id.AsText()) is not { } old)
        {
            return false;
        }

        FieldValue oldKey = old.Get(slot.KeyField);
        FieldValue oldVector = old.Get(slot.Sparse);
        if (oldKey.Kind != FieldValueKind.Bytes || oldVector.Kind != FieldValueKind.Sparse || !oldKey.AsBytes().Span.SequenceEqual(key))
        {
            return false;
        }

        vector = oldVector.AsSparse();
        Remember(ck, vector);
        return true;
    }

    private void Remember(string ck, SparseVector vector)
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

    private static void Apply(Document doc, Slot slot, byte[] key, SparseVector vector, bool replace)
    {
        if (replace)
        {
            doc.Remove(slot.Sparse);
            doc.Remove(slot.KeyField);
        }

        doc.Add(slot.Sparse, vector);
        doc.Add(slot.KeyField, FieldValue.FromBytes(key));
    }

    private void Finish(Plan plan, SparseVector[] results, bool replace)
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

    /// <summary>Encodes what needs it and writes the vectors and cache keys into the documents. Nothing changes when the encoder fails.</summary>
    public void Fill(IReadOnlyList<Document> docs, string idField, Func<string, Document?> committed, bool replace, CancellationToken ct)
    {
        Plan plan = MakePlan(docs, idField, committed, replace);
        if (plan.Texts.Count == 0)
        {
            return;
        }

        var results = new SparseVector[plan.Texts.Count];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            try
            {
                Encoder.Encode(plan.Texts.GetRange(at, n), EmbeddingRole.Document, results.AsSpan(at, n), ct);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Sparse encoder '{Encoder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
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

        var results = new SparseVector[plan.Texts.Count];
        for (int at = 0; at < plan.Texts.Count; at += BatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int n = Math.Min(BatchSize, plan.Texts.Count - at);
            try
            {
                await Encoder.EncodeAsync(plan.Texts.GetRange(at, n), EmbeddingRole.Document, results.AsMemory(at, n), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
            {
                throw new EmbeddingException($"Sparse encoder '{Encoder.ModelId}' failed on a batch of {n} texts: {e.Message}", e);
            }
        }

        Finish(plan, results, replace);
    }

    /// <summary>Encodes query text with the query role (cached for repeated searches).</summary>
    public SparseVector EncodeQuery(string text)
    {
        lock (_gate)
        {
            if (_queries.TryGetValue(text, out SparseVector? known))
            {
                return known;
            }
        }

        SparseVector vector;
        try
        {
            vector = Encoder.Encode(text, EmbeddingRole.Query);
        }
        catch (Exception e) when (e is not OperationCanceledException and not EmbeddingException)
        {
            throw new EmbeddingException($"Sparse encoder '{Encoder.ModelId}' failed on the query text: {e.Message}", e);
        }

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
