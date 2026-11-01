using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Documents scored by the dot product of the query's token weights with the document's learned sparse vector in <see cref="Field"/>
/// (SPLADE: Formal et al., SIGIR 2021). A document matches when it shares at least one token with the query. Document weights are
/// quantized when indexed (<see cref="SparseVector.Scale"/>), so a score differs from the exact dot product by at most
/// <c>0.5 / Scale</c> times the sum of the query weights. Top-k selection uses block-max WAND over the stored weights.
/// </summary>
/// <param name="Field">A sparse field that is indexed.</param>
/// <param name="Vector">The query's weights by token.</param>
public sealed record SparseQuery(string Field, SparseVector Vector) : Query
{
    /// <summary>Compares the vector by value.</summary>
    /// <param name="other">The other query.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    public bool Equals(SparseQuery? other)
        => other is not null && Field == other.Field && Boost.Equals(other.Boost) && Vector.Equals(other.Vector);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Field, Boost, Vector.GetHashCode()); // in-process only

    /// <inheritdoc/>
    public override TResult Accept<TResult>(QueryVisitor<TResult> visitor) => visitor.VisitSparse(this);
}

/// <summary>Prepares a <see cref="SparseQuery"/>: one weighted postings scorer per query token, combined by block-max WAND.</summary>
internal sealed class SparseWeight : Weight
{
    private readonly string _field;
    private readonly byte[][] _terms;
    private readonly float[] _scales;
    private readonly string[] _tokens;

    private SparseWeight(string field, string[] tokens, byte[][] terms, float[] scales)
    {
        _field = field;
        _tokens = tokens;
        _terms = terms;
        _scales = scales;
    }

    public static Weight Create(SearchContext context, SparseQuery q)
    {
        if (!float.IsFinite(q.Boost) || q.Boost < 0f)
        {
            throw new QueryException($"A query boost must be finite and not negative, got {q.Boost}.");
        }

        if (q.Vector is null)
        {
            throw new QueryException("A sparse query needs a vector.");
        }

        if (!context.Schema.TryGetField(q.Field, out FieldDefinition? def))
        {
            throw new QueryException($"The schema has no field '{q.Field}'.");
        }

        if (def.Type != FieldType.Sparse || !def.Indexed)
        {
            throw new QueryException($"A sparse query needs an indexed sparse field; '{q.Field}' is {(def.Type == FieldType.Sparse ? "not indexed" : def.Type.ToString())}.");
        }

        var terms = new byte[q.Vector.Count][];
        var scales = new float[q.Vector.Count];
        for (int i = 0; i < terms.Length; i++)
        {
            terms[i] = System.Text.Encoding.UTF8.GetBytes(q.Vector.Tokens[i]);
            // Stored frequency f stands for weight f / Scale, so the clause's score is f * (queryWeight * boost / Scale).
            scales[i] = q.Vector.Weights[i] * q.Boost / SparseVector.Scale;
        }

        return new SparseWeight(q.Field, q.Vector.Tokens, terms, scales);
    }

    public override Scorer? GetScorer(LeafContext leaf)
    {
        if (leaf.Reader.GetTextField(_field) is not { } reader)
        {
            return null;
        }

        var clauses = new List<Scorer>(_terms.Length);
        for (int i = 0; i < _terms.Length; i++)
        {
            if (reader.TryGetTerm(_terms[i], out TermInfo info))
            {
                float scale = _scales[i];
                clauses.Add(new PostingsScorer(reader.GetPostings(info, PostingsFeatures.Frequencies), reader, (freq, _) => freq * scale));
            }
        }

        return clauses.Count switch
        {
            0 => null,
            1 => clauses[0],
            _ => new BlockMaxWandScorer(clauses),
        };
    }

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        if (leaf.Reader.GetTextField(_field) is not { } reader)
        {
            return null;
        }

        float total = 0f;
        var parts = new List<Explanation>();
        for (int i = 0; i < _terms.Length; i++)
        {
            if (reader.TryGetTerm(_terms[i], out TermInfo info))
            {
                PostingsIterator it = reader.GetPostings(info, PostingsFeatures.Frequencies);
                if (it.Advance(docId) == docId)
                {
                    float v = it.Frequency * _scales[i];
                    total += v;
                    parts.Add(new Explanation(v, $"token '{_tokens[i]}': stored weight {it.Frequency}/{SparseVector.Scale} * query weight * boost"));
                }
            }
        }

        return parts.Count == 0 ? null : new Explanation(total, $"sparse dot product on {_field}", [.. parts]);
    }
}
