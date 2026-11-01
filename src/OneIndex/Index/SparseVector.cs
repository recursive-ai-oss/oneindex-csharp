namespace OneIndex.Index;

/// <summary>
/// A learned sparse vector: non-negative finite weights by token, as produced by SPLADE-style encoders (Formal et al., SIGIR 2021).
/// Tokens are unique and kept in ordinal order; zero weights are dropped.
/// </summary>
public sealed class SparseVector : IEquatable<SparseVector>
{
    /// <summary>Weights are stored as integers <c>round(weight * Scale)</c>; the error of a stored weight is at most <c>0.5 / Scale</c>.</summary>
    public const int Scale = 128;

    /// <summary>The largest number of entries accepted.</summary>
    public const int MaxEntries = 65_536;

    /// <summary>Creates a vector.</summary>
    /// <param name="entries">Token and weight pairs; a repeated token keeps the larger weight.</param>
    /// <exception cref="ArgumentException">A token is empty, a weight is negative, NaN or infinite, or there are more than <see cref="MaxEntries"/> entries.</exception>
    public SparseVector(IEnumerable<KeyValuePair<string, float>> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var map = new SortedDictionary<string, float>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, float> e in entries)
        {
            if (string.IsNullOrEmpty(e.Key))
            {
                throw new ArgumentException("A sparse token must not be empty.", nameof(entries));
            }

            if (!float.IsFinite(e.Value) || e.Value < 0f)
            {
                throw new ArgumentException($"Sparse weight of '{e.Key}' is {e.Value}; weights must be finite and not negative.", nameof(entries));
            }

            if (e.Value == 0f)
            {
                continue;
            }

            map[e.Key] = map.TryGetValue(e.Key, out float old) ? Math.Max(old, e.Value) : e.Value;
            if (map.Count > MaxEntries)
            {
                throw new ArgumentException($"A sparse vector holds at most {MaxEntries} entries.", nameof(entries));
            }
        }

        Tokens = [.. map.Keys];
        Weights = [.. map.Values];
    }

    /// <summary>The tokens, unique, in ordinal order.</summary>
    public string[] Tokens { get; }

    /// <summary>The weights, parallel to <see cref="Tokens"/>, all above zero.</summary>
    public float[] Weights { get; }

    /// <summary>The number of entries.</summary>
    public int Count => Tokens.Length;

    /// <summary>The integer a weight is stored as, 0 when it rounds away.</summary>
    /// <param name="weight">The weight.</param>
    /// <returns>The quantized weight, at most <c>int.MaxValue</c>.</returns>
    public static int Quantize(float weight) => (int)Math.Min(Math.Round(weight * (double)Scale), int.MaxValue);

    /// <inheritdoc/>
    public bool Equals(SparseVector? other)
        => other is not null && Tokens.AsSpan().SequenceEqual(other.Tokens) && Weights.AsSpan().SequenceEqual(other.Weights);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SparseVector v && Equals(v);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process only, never persisted.
        var h = default(HashCode);
        foreach (string t in Tokens)
        {
            h.Add(t, StringComparer.Ordinal);
        }

        return h.ToHashCode();
    }
}
