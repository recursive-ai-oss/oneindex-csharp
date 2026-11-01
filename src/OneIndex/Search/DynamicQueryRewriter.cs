using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Maps queries on the paths of indexed JSON fields (<c>meta.customer.name</c>) to the derived fields that hold them
/// (<c>meta.customer.name#k</c>, ...), before weights are built. The kind of value compared picks the derived field: text and
/// everything text-shaped (prefix, fuzzy, regex, phrase) the keyword one, numbers the number one, booleans the bool one, dates the
/// date one. A path or kind no document has produced yet matches nothing. See docs/dynamic.md.
/// </summary>
internal static class DynamicQueryRewriter
{
    public static Query Rewrite(Query query, Schema schema)
    {
        if (!schema.Fields.Any(static f => f.Type == FieldType.Json && f.Indexed && f.DynamicParent is null))
        {
            return query;
        }

        return Visit(query, schema, 0);
    }

    private static BooleanQuery None() => new([]);

    private static Query Visit(Query query, Schema schema, int depth)
    {
        if (depth > 256)
        {
            return query; // the planner reports the depth limit itself
        }

        switch (query)
        {
            case BooleanQuery b:
                {
                    var clauses = new List<BooleanClause>(b.Clauses.Count);
                    foreach (BooleanClause c in b.Clauses)
                    {
                        clauses.Add(c with { Query = Visit(c.Query, schema, depth + 1) });
                    }

                    return b with { Clauses = clauses };
                }

            case HybridQuery h:
                return h with
                {
                    Text = Visit(h.Text, schema, depth + 1),
                    Sparse = h.Sparse is null ? null : Visit(h.Sparse, schema, depth + 1),
                    Vector = h.Vector is null ? null : h.Vector with { Filter = h.Vector.Filter is { } vf ? Visit(vf, schema, depth + 1) : null },
                };
            case TermQuery t when IsPath(schema, t.Field):
                return Keyword(schema, t.Field) is { } tk ? t with { Field = tk } : None();
            case PrefixQuery p when IsPath(schema, p.Field):
                return Keyword(schema, p.Field) is { } pk ? p with { Field = pk } : None();
            case FuzzyQuery f when IsPath(schema, f.Field):
                return Keyword(schema, f.Field) is { } fk ? f with { Field = fk } : None();
            case RegexQuery r when IsPath(schema, r.Field):
                return Keyword(schema, r.Field) is { } rk ? r with { Field = rk } : None();
            case PhraseQuery ph when IsPath(schema, ph.Field):
                return Keyword(schema, ph.Field) is { } phk ? ph with { Field = phk } : None();
            case MultiFieldTermQuery m when m.Fields.Any(n => IsPath(schema, n)):
                {
                    var fields = new List<string>(m.Fields.Count);
                    foreach (string name in m.Fields)
                    {
                        if (!IsPath(schema, name))
                        {
                            fields.Add(name);
                        }
                        else if (Keyword(schema, name) is { } k)
                        {
                            fields.Add(k);
                        }
                    }

                    return fields.Count == 0 ? None() : m with { Fields = fields };
                }

            case RangeQuery range when IsPath(schema, range.Field):
                return Range(schema, range);
            default:
                return query;
        }
    }

    private static bool IsPath(Schema schema, string name) => schema.TryGetPath(name, out _);

    private static string? Keyword(Schema schema, string logical)
    {
        string physical = DynamicFields.PhysicalName(logical, DynamicKind.Keyword);
        return schema.GetFieldId(physical) >= 0 ? physical : null;
    }

    private static Query Range(Schema schema, RangeQuery q)
    {
        if (q.Lower is null && q.Upper is null)
        {
            // Exists: any kind of value at the path.
            var any = new List<BooleanClause>();
            foreach (DynamicKind kind in DynamicFields.Kinds)
            {
                string physical = DynamicFields.PhysicalName(q.Field, kind);
                if (schema.GetFieldId(physical) >= 0)
                {
                    any.Add(new BooleanClause(q with { Field = physical }, Occur.Should));
                }
            }

            if (any.Count == 0)
            {
                return None();
            }

            return new BooleanQuery(any) { MinimumShouldMatch = 1, Boost = q.Boost };
        }

        DynamicKind? lower = q.Lower is { } lo ? DynamicFields.KindOf(lo.Kind) : null;
        DynamicKind? upper = q.Upper is { } hi ? DynamicFields.KindOf(hi.Kind) : null;
        if (lower is not null && upper is not null && lower != upper)
        {
            return None(); // a range between a number and a text matches nothing
        }

        DynamicKind? kindOfRange = lower ?? upper;
        if (kindOfRange is null)
        {
            return None();
        }

        string target = DynamicFields.PhysicalName(q.Field, kindOfRange.Value);
        RangeQuery retargeted = q with { Field = target };
        return schema.GetFieldId(target) >= 0 ? retargeted : None();
    }
}
