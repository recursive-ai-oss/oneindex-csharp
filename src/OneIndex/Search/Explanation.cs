namespace OneIndex.Search;

/// <summary>
/// One node of a score breakdown: a value, what it is, and the parts it is made of. Leaves of a BM25F term carry the numbers the
/// formula used (see docs/scoring.md): per field the term frequency, field length and weight, and for the term its idf.
/// </summary>
public sealed class Explanation
{
    /// <summary>Creates a node.</summary>
    /// <param name="value">The node's value.</param>
    /// <param name="description">What the value is, in words and symbols (for example <c>idf = ln(1 + (N - n + 0.5) / (n + 0.5)), n = 3, N = 100</c>).</param>
    /// <param name="details">The parts; empty for a leaf.</param>
    public Explanation(float value, string description, params Explanation[] details)
    {
        Value = value;
        Description = description;
        Details = details;
    }

    /// <summary>The node's value. For the root it equals the document's score.</summary>
    public float Value { get; }

    /// <summary>What the value is.</summary>
    public string Description { get; }

    /// <summary>The parts the value is made of.</summary>
    public IReadOnlyList<Explanation> Details { get; }

    /// <summary>Renders the tree, one node per line, children indented two spaces.</summary>
    /// <returns>The text.</returns>
    public override string ToString()
    {
        var sb = new System.Text.StringBuilder();
        Append(sb, 0);
        return sb.ToString();
    }

    private void Append(System.Text.StringBuilder sb, int depth)
    {
        sb.Append(' ', depth * 2).Append(Value.ToString("G9", System.Globalization.CultureInfo.InvariantCulture)).Append(" = ").Append(Description).Append('\n');
        foreach (Explanation d in Details)
        {
            d.Append(sb, depth + 1);
        }
    }
}
