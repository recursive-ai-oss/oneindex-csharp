using OneIndex.Codecs;
using OneIndex.Index;

namespace OneIndex.Search;

/// <summary>
/// Builds the weight of a <see cref="RangeQuery"/>. A numeric, date or boolean field uses the segment's point index when it has one
/// (<see cref="IPointIndexReader"/>, the fast path), and otherwise the numeric terms the text codec writes when its <see cref="TextSegmentCodec.IndexNumericTerms"/> is on (<see cref="NumericTerms"/>):
/// a contiguous run of the term dictionary, found from the digits the two bounds share. A keyword field walks its terms in byte
/// order between the bounds. Matches are collected into a bit set per segment and score as the boost.
/// </summary>
internal static class RangeWeightFactory
{
    public static Weight Create(Schema schema, RangeQuery q)
    {
        if (!schema.TryGetField(q.Field, out FieldDefinition? def))
        {
            throw new QueryException($"The schema has no field '{q.Field}'.");
        }

        if (!def.Indexed)
        {
            throw new QueryException($"Field '{q.Field}' is not indexed, so it cannot be searched by range.");
        }

        string description = $"range on {q.Field}";
        if (def.Type == FieldType.Keyword)
        {
            byte[]? lower = Text(q, q.Lower);
            byte[]? upper = Text(q, q.Upper);
            return new ConstantWeight(description, q.Boost, leaf => KeywordRange(leaf, q.Field, lower, q.IncludeLower, upper, q.IncludeUpper));
        }

        if (!NumericTerms.IsNumeric(def.Type))
        {
            throw new QueryException($"A range query needs a numeric, date, boolean or keyword field; '{q.Field}' is {def.Type}.");
        }

        long lo = Bound(def, q, q.Lower, lower: true);
        long hi = Bound(def, q, q.Upper, lower: false);
        if (q.Lower is not null && !q.IncludeLower)
        {
            if (lo == long.MaxValue)
            {
                return EmptyWeight.Instance;
            }

            lo++;
        }

        if (q.Upper is not null && !q.IncludeUpper)
        {
            if (hi == long.MinValue)
            {
                return EmptyWeight.Instance;
            }

            hi--;
        }

        if (lo > hi)
        {
            return EmptyWeight.Instance;
        }

        long from = lo;
        long to = hi;
        return new ConstantWeight(description, q.Boost, leaf => NumericRange(leaf, q.Field, from, to));
    }

    private static byte[]? Text(RangeQuery q, FieldValue? v)
    {
        if (v is null)
        {
            return null;
        }

        if (v.Value.Kind != FieldValueKind.Text)
        {
            throw new QueryException($"The bounds of a range on keyword field '{q.Field}' must be text, got {v.Value.Kind}.");
        }

        return System.Text.Encoding.UTF8.GetBytes(v.Value.AsText());
    }

    // The ordered 64-bit form of a bound; an open bound is the end of the type's range (for doubles, the infinities, which leaves NaN out).
    private static long Bound(FieldDefinition def, RangeQuery q, FieldValue? v, bool lower)
    {
        if (v is null)
        {
            return def.Type == FieldType.Fractional
                ? SortableBits.Encode(lower ? double.NegativeInfinity : double.PositiveInfinity)
                : (lower ? long.MinValue : long.MaxValue);
        }

        FieldValue value = v.Value;
        bool ok = def.Type switch
        {
            FieldType.Integral or FieldType.Date => value.Kind is FieldValueKind.Integral or FieldValueKind.Date,
            FieldType.Fractional => value.Kind is FieldValueKind.Integral or FieldValueKind.Fractional,
            FieldType.Bool => value.Kind == FieldValueKind.Bool,
            _ => false,
        };
        if (!ok)
        {
            throw new QueryException($"A bound of a range on {def.Type} field '{q.Field}' cannot be a {value.Kind} value.");
        }

        if (def.Type == FieldType.Fractional && double.IsNaN(value.AsFloat64()))
        {
            throw new QueryException($"A bound of a range on field '{q.Field}' is NaN.");
        }

        return NumericTerms.Ordered(def.Type, value);
    }

    private static DocIdSetIterator? NumericRange(LeafContext leaf, string field, long lo, long hi)
    {
        if (leaf.Reader.GetPoints(field) is { } points)
        {
            return points.GetRange(lo, hi);
        }

        if (leaf.Reader.GetTextField(field) is not { } reader)
        {
            return null;
        }

        byte[] from = new byte[NumericTerms.TermLength];
        byte[] to = new byte[NumericTerms.TermLength];
        NumericTerms.Write(lo, from);
        NumericTerms.Write(hi, to);
        int shared = from.AsSpan().CommonPrefixLength(to);
        var words = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
        bool any = false;
        using ITermEnumerator terms = reader.GetTerms(from.AsSpan(0, shared));
        while (terms.MoveNext())
        {
            ReadOnlySpan<byte> t = terms.Term;
            if (t.SequenceCompareTo(from) < 0)
            {
                continue;
            }

            if (t.SequenceCompareTo(to) > 0)
            {
                break;
            }

            any |= AddDocuments(reader, terms.Info, words);
        }

        return any ? new BitSetIterator(words) : null;
    }

    private static BitSetIterator? KeywordRange(LeafContext leaf, string field, byte[]? lower, bool includeLower, byte[]? upper, bool includeUpper)
    {
        if (leaf.Reader.GetTextField(field) is not { } reader)
        {
            return null;
        }

        ReadOnlySpan<byte> prefix = lower is not null && upper is not null ? lower.AsSpan(0, lower.AsSpan().CommonPrefixLength(upper)) : default;
        var words = new ulong[(leaf.Reader.MaxDoc + 63) >> 6];
        bool any = false;
        using ITermEnumerator terms = reader.GetTerms(prefix);
        while (terms.MoveNext())
        {
            ReadOnlySpan<byte> t = terms.Term;
            if (lower is not null)
            {
                int c = t.SequenceCompareTo(lower);
                if (c < 0 || (c == 0 && !includeLower))
                {
                    continue;
                }
            }

            if (upper is not null)
            {
                int c = t.SequenceCompareTo(upper);
                if (c > 0 || (c == 0 && !includeUpper))
                {
                    break;
                }
            }

            any |= AddDocuments(reader, terms.Info, words);
        }

        return any ? new BitSetIterator(words) : null;
    }

    private static bool AddDocuments(ITextFieldReader reader, TermInfo info, ulong[] words)
    {
        PostingsIterator it = reader.GetPostings(info, PostingsFeatures.None);
        bool any = false;
        for (int d = it.NextDoc(); d != DocIdSetIterator.NoMoreDocs; d = it.NextDoc())
        {
            words[d >> 6] |= 1UL << (d & 63);
            any = true;
        }

        return any;
    }
}
