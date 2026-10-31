using OneIndex.Storage;

namespace OneIndex.Index;

/// <summary>
/// A declared schema cannot replace the committed one: a field would change to a type its values cannot be converted to, the
/// identifier field would change, or the index would have to be rebuilt from stored source that a field opted out of keeping.
/// </summary>
public sealed class SchemaEvolutionException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="field">The field the problem is about.</param>
    /// <param name="message">What cannot be done, and why.</param>
    public SchemaEvolutionException(string field, string message)
        : base(message)
    {
        Field = field;
    }

    /// <summary>The name of the field the schema change is refused for.</summary>
    public string Field { get; }
}

/// <summary>Where the background rebuild after a schema change stands.</summary>
/// <param name="SchemaEpoch">The schema epoch being served: 0 until a schema change needed existing segments rebuilt, then one higher per such change, at the swap.</param>
/// <param name="Segments">The segments in the index.</param>
/// <param name="SegmentsAwaitingRebuild">Served segments that have no rebuilt twin yet; 0 when nothing is pending.</param>
/// <param name="SwapPending">Whether a rebuild is pending, including the case where every twin is written and only the swap commit is missing.</param>
public readonly record struct SchemaEvolutionStatus(long SchemaEpoch, int Segments, int SegmentsAwaitingRebuild, bool SwapPending = false)
{
    /// <summary>Whether some segment still awaits its rebuild.</summary>
    public bool InProgress => SwapPending || SegmentsAwaitingRebuild > 0;
}

/// <summary>Compares a declared schema with the committed one (docs/schema-evolution.md).</summary>
internal static class SchemaDiff
{
    /// <summary>The outcome of a comparison.</summary>
    /// <param name="Effective">The schema to run with: the declared fields plus the derived fields of the committed schema that still apply.</param>
    /// <param name="Rebuild">Whether existing segments must be rebuilt from stored source for the change to take full effect.</param>
    internal readonly record struct Plan(Schema Effective, bool Rebuild);

    public static Plan Compare(Schema committed, Schema declared, IndexLimits limits)
    {
        if (!string.Equals(committed.IdField, declared.IdField, StringComparison.Ordinal))
        {
            throw new SchemaEvolutionException(declared.IdField ?? committed.IdField ?? string.Empty, $"The identifier field changes from '{committed.IdField}' to '{declared.IdField}'; documents are identified by the committed field, so this needs a new index.");
        }

        foreach (FieldDefinition d in declared.Fields)
        {
            if (d.Type == FieldType.Text && d.Indexed && d.Analyzer is { } analyzer && !Analysis.AnalyzerRegistry.TryCreate(analyzer, out _))
            {
                throw new SchemaEvolutionException(d.Name, $"Field '{d.Name}' names the analyzer '{analyzer}', which is not registered.");
            }
        }

        bool rebuild = false;
        string? reason = null;
        foreach (FieldDefinition d in declared.Fields)
        {
            if (!committed.TryGetField(d.Name, out FieldDefinition? c) || c.DynamicParent is not null)
            {
                continue; // a new field: existing documents have no value for it, so there is nothing to rebuild
            }

            bool typeChange = c.Type != d.Type;
            if (typeChange && !Convertible(c.Type, d.Type))
            {
                throw new SchemaEvolutionException(d.Name, $"Field '{d.Name}' changes from {c.Type} to {d.Type}, which its stored values cannot be converted to. Keep the type, or add a new field and drop this one.");
            }

            if (c.Type == FieldType.Vector && c.Dimensions != d.Dimensions)
            {
                throw new SchemaEvolutionException(d.Name, $"Vector field '{d.Name}' changes from {c.Dimensions} to {d.Dimensions} dimensions; existing vectors cannot be converted.");
            }

            if (c.Multi && !d.Multi)
            {
                throw new SchemaEvolutionException(d.Name, $"Field '{d.Name}' stops holding several values, but existing documents may hold more than one.");
            }

            bool changed = typeChange
                || d.Multi != c.Multi
                || (d.Indexed && !c.Indexed)
                || (d.Column && !c.Column)
                || (d.Facet && !c.Facet)
                || (d.Type == FieldType.Text && d.Indexed && (!string.Equals(d.Analyzer, c.Analyzer, StringComparison.Ordinal) || d.IndexOptions != c.IndexOptions));
            if (changed && !rebuild)
            {
                rebuild = true;
                reason = d.Name;
            }

            if (changed && !c.Stored)
            {
                throw new SchemaEvolutionException(d.Name, $"Field '{d.Name}' changes in a way that needs the index rebuilt from stored source, but it is not stored. Keep the field stored, or build a new index.");
            }
        }

        if (rebuild)
        {
            // A rebuild re-indexes every field from the stored source, so a field that is searched but not stored would lose its index.
            foreach (FieldDefinition d in declared.Fields)
            {
                if (committed.TryGetField(d.Name, out FieldDefinition? c) && c.DynamicParent is null && !c.Stored && (c.Indexed || c.Column || c.Facet) && d.Type != FieldType.Bytes)
                {
                    throw new SchemaEvolutionException(d.Name, $"The change to field '{reason}' needs the index rebuilt from stored source, but field '{d.Name}' is searchable and not stored, so its index would be lost. Keep it stored, or build a new index.");
                }
            }
        }

        var derived = new List<FieldDefinition>();
        foreach (FieldDefinition c in committed.Fields)
        {
            if (c.DynamicParent is { } parent && declared.TryGetField(parent, out FieldDefinition? p) && p.Type == FieldType.Json && p.Indexed)
            {
                derived.Add(c);
            }
        }

        Schema effective = derived.Count == 0 ? declared : declared.WithAppended(derived, limits);
        return new Plan(effective, rebuild);
    }

    // Conversions that keep every stored value meaningful: strings stay strings, integers widen to floating point.
    private static bool Convertible(FieldType from, FieldType to)
        => (from, to) is (FieldType.Keyword, FieldType.Text) or (FieldType.Text, FieldType.Keyword) or (FieldType.Integral, FieldType.Fractional);
}
