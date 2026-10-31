using System.Text;
using OneIndex.Analysis;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Highlighting;

/// <summary>Where highlighted snippets come from and how big they are.</summary>
public sealed record HighlightOptions
{
    /// <summary>The target length of a fragment in UTF-16 code units. Default 160. A fragment is widened to whole words, so it can be somewhat longer; it is never cut inside a word or a surrogate pair.</summary>
    public int FragmentLength { get; init; } = 160;

    /// <summary>The most fragments returned per field. Default 3.</summary>
    public int MaxFragments { get; init; } = 3;
}

/// <summary>A run of characters inside a fragment that matched the query.</summary>
/// <param name="Start">The offset of the first character within <see cref="Fragment.Text"/>.</param>
/// <param name="Length">The number of characters.</param>
public readonly record struct MatchSpan(int Start, int Length);

/// <summary>A snippet of a field with the matched words located.</summary>
public sealed class Fragment
{
    internal Fragment(string text, int offset, IReadOnlyList<MatchSpan> matches)
    {
        Text = text;
        Offset = offset;
        Matches = matches;
    }

    /// <summary>The snippet, unmodified text of the field value.</summary>
    public string Text { get; }

    /// <summary>The offset of the snippet within the field value.</summary>
    public int Offset { get; }

    /// <summary>The matched runs, in order and not overlapping. Words that the analyzer split or stemmed are located in the original text.</summary>
    public IReadOnlyList<MatchSpan> Matches { get; }

    /// <summary>Returns the snippet with each match wrapped in tags. The text is not escaped, so escape it first if the result becomes HTML.</summary>
    /// <param name="open">Placed before each match. Default <c>&lt;em&gt;</c>.</param>
    /// <param name="close">Placed after each match. Default <c>&lt;/em&gt;</c>.</param>
    /// <returns>The marked snippet.</returns>
    public string Marked(string open = "<em>", string close = "</em>")
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(close);
        var sb = new StringBuilder(Text.Length + (Matches.Count * (open.Length + close.Length)));
        int at = 0;
        foreach (MatchSpan m in Matches)
        {
            sb.Append(Text, at, m.Start - at).Append(open).Append(Text, m.Start, m.Length).Append(close);
            at = m.Start + m.Length;
        }

        return sb.Append(Text, at, Text.Length - at).ToString();
    }

    /// <inheritdoc/>
    public override string ToString() => Marked();
}

/// <summary>The analyzed terms of a query, per field: what a highlighter looks for.</summary>
public sealed class HighlightTerms
{
    private readonly Dictionary<string, FieldTerms> _fields = new(StringComparer.Ordinal);

    /// <summary>Whether the query names any term to highlight.</summary>
    public bool IsEmpty => _fields.Count == 0;

    internal IEnumerable<KeyValuePair<string, FieldTerms>> Fields => _fields;

    /// <summary>Collects the terms of a query. Excluded clauses are skipped, and range, prefix, fuzzy and regular-expression queries add nothing.</summary>
    /// <param name="query">The query.</param>
    /// <returns>The terms.</returns>
    public static HighlightTerms From(Search.Query query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var terms = new HighlightTerms();
        query.Accept(new Collector(terms));
        return terms;
    }

    private FieldTerms For(string field)
    {
        if (!_fields.TryGetValue(field, out FieldTerms? f))
        {
            _fields[field] = f = new FieldTerms();
        }

        return f;
    }

    internal sealed class FieldTerms
    {
        public HashSet<string> Words { get; } = new(StringComparer.Ordinal);

        public List<(string[] Terms, int Slop)> Phrases { get; } = [];
    }

    private sealed class Collector(HighlightTerms owner) : QueryVisitor<int>
    {
        public override int VisitTerm(TermQuery query)
        {
            owner.For(query.Field).Words.Add(query.Term);
            return 0;
        }

        public override int VisitMultiFieldTerm(MultiFieldTermQuery query)
        {
            foreach (string field in query.Fields)
            {
                owner.For(field).Words.Add(query.Term);
            }

            return 0;
        }

        // Only the query word itself is located; words matched through typos or as longer words are not highlighted.
        public override int VisitTypoTerm(TypoTermQuery query)
        {
            foreach (string field in query.Fields)
            {
                owner.For(field).Words.Add(query.Term);
            }

            return 0;
        }

        public override int VisitPhrase(PhraseQuery query)
        {
            if (query.Terms.Count == 1)
            {
                owner.For(query.Field).Words.Add(query.Terms[0]);
            }
            else if (query.Terms.Count > 1)
            {
                owner.For(query.Field).Phrases.Add((query.Terms.ToArray(), query.Slop));
            }

            return 0;
        }

        public override int VisitBoolean(BooleanQuery query)
        {
            foreach (BooleanClause c in query.Clauses)
            {
                if (c.Occur != Occur.MustNot)
                {
                    c.Query.Accept(this);
                }
            }

            return 0;
        }

        public override int VisitPrefix(PrefixQuery query) => 0;

        public override int VisitFuzzy(FuzzyQuery query) => 0;

        public override int VisitRange(RangeQuery query) => 0;

        public override int VisitMatchAll(MatchAllQuery query) => 0;

        public override int VisitRegex(RegexQuery query) => 0;
    }
}

/// <summary>Finds the matched words of a query in stored text by analyzing the text again with the field's analyzer.</summary>
public static class Highlighter
{
    /// <summary>Highlights the full-text fields of a document.</summary>
    /// <param name="schema">The schema, for each field's analyzer.</param>
    /// <param name="document">The stored document.</param>
    /// <param name="terms">The query's terms.</param>
    /// <param name="options">Fragment size and count; the defaults apply when <see langword="null"/>.</param>
    /// <returns>Fragments per field name, for the fields where something matched, in field order.</returns>
    public static IReadOnlyDictionary<string, IReadOnlyList<Fragment>> Highlight(Schema schema, Document document, HighlightTerms terms, HighlightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(terms);
        options ??= new HighlightOptions();
        if (options.FragmentLength < 1 || options.MaxFragments < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FragmentLength and MaxFragments must be at least 1.");
        }

        var result = new Dictionary<string, IReadOnlyList<Fragment>>(StringComparer.Ordinal);
        if (terms.IsEmpty)
        {
            return result;
        }

        var analyzers = new Dictionary<string, Analyzer>(StringComparer.Ordinal);
        foreach (FieldDefinition def in schema.Fields)
        {
            if (def.Type != FieldType.Text || !HasField(terms, def.Name, out HighlightTerms.FieldTerms? wanted))
            {
                continue;
            }

            string analyzerName = def.Analyzer ?? FieldDefinition.DefaultAnalyzer;
            if (!analyzers.TryGetValue(analyzerName, out Analyzer? analyzer))
            {
                analyzers[analyzerName] = analyzer = AnalyzerRegistry.Create(analyzerName);
            }

            var fragments = new List<Fragment>();
            foreach (DocumentField f in document.Fields)
            {
                if (fragments.Count >= options.MaxFragments)
                {
                    break;
                }

                if (f.Name == def.Name && f.Value.Kind == FieldValueKind.Text)
                {
                    string text = f.Value.AsText();
                    Fragments(text, analyzer.Analyze(text), wanted!, options, fragments);
                }
            }

            if (fragments.Count > 0)
            {
                result[def.Name] = fragments;
            }
        }

        return result;
    }

    private static bool HasField(HighlightTerms terms, string name, out HighlightTerms.FieldTerms? found)
    {
        foreach (KeyValuePair<string, HighlightTerms.FieldTerms> kv in terms.Fields)
        {
            if (kv.Key == name)
            {
                found = kv.Value;
                return true;
            }
        }

        found = null;
        return false;
    }

    private static void Fragments(string text, IReadOnlyList<AnalyzedToken> tokens, HighlightTerms.FieldTerms wanted, HighlightOptions options, List<Fragment> into)
    {
        var spans = new List<(int Start, int End)>();
        foreach (AnalyzedToken t in tokens)
        {
            if (wanted.Words.Contains(t.Term) && Valid(t, text))
            {
                spans.Add((t.StartOffset, t.EndOffset));
            }
        }

        foreach ((string[] terms, int slop) in wanted.Phrases)
        {
            MatchPhrases(text, tokens, terms, slop, spans);
        }

        if (spans.Count == 0)
        {
            return;
        }

        // Overlapping or touching runs are one highlighted run: CJK bigrams overlap, and a phrase covers its words.
        spans.Sort(static (a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.End.CompareTo(b.End));
        var merged = new List<(int Start, int End)>();
        foreach ((int s, int e) in spans)
        {
            if (merged.Count > 0 && s <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, e));
            }
            else
            {
                merged.Add((s, e));
            }
        }

        int next = 0;
        while (next < merged.Count && into.Count < options.MaxFragments)
        {
            int first = merged[next].Start;
            int start = Math.Max(0, first - (options.FragmentLength / 4));
            int end = Math.Max(first + 1, Math.Min(text.Length, start + options.FragmentLength));
            end = Math.Max(end, merged[next].End);
            start = SnapToWordStart(tokens, start);
            end = Math.Min(text.Length, SnapToWordEnd(tokens, end));
            foreach ((int s, int e) in merged)
            {
                // A run that begins inside the fragment is kept whole.
                if (s < end && e > end)
                {
                    end = e;
                }
            }

            if (start > 0 && start < text.Length && char.IsLowSurrogate(text[start]))
            {
                start--;
            }

            if (end < text.Length && char.IsLowSurrogate(text[end]))
            {
                end++;
            }

            var matches = new List<MatchSpan>();
            int after = next;
            while (after < merged.Count && merged[after].Start < end)
            {
                if (merged[after].Start >= start)
                {
                    matches.Add(new MatchSpan(merged[after].Start - start, merged[after].End - merged[after].Start));
                }

                after++;
            }

            into.Add(new Fragment(text[start..end], start, matches));
            next = after;
        }
    }

    private static bool Valid(AnalyzedToken t, string text) => t.StartOffset >= 0 && t.EndOffset > t.StartOffset && t.EndOffset <= text.Length;

    // A start inside a word moves back to the word's start, so that no word is cut.
    private static int SnapToWordStart(IReadOnlyList<AnalyzedToken> tokens, int start)
    {
        foreach (AnalyzedToken t in tokens)
        {
            if (t.StartOffset < start && t.EndOffset > start)
            {
                return Math.Max(0, t.StartOffset);
            }
        }

        return start;
    }

    private static int SnapToWordEnd(IReadOnlyList<AnalyzedToken> tokens, int end)
    {
        int result = end;
        foreach (AnalyzedToken t in tokens)
        {
            if (t.StartOffset < end && t.EndOffset > result)
            {
                result = t.EndOffset;
            }
        }

        return result;
    }

    // A phrase matches where its terms occur at increasing positions with at most `slop` positions skipped in total.
    private static void MatchPhrases(string text, IReadOnlyList<AnalyzedToken> tokens, string[] terms, int slop, List<(int Start, int End)> spans)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Term != terms[0])
            {
                continue;
            }

            int last = i;
            int skipped = 0;
            bool ok = true;
            for (int k = 1; k < terms.Length && ok; k++)
            {
                ok = false;
                int expected = tokens[last].Position + 1;
                for (int j = last + 1; j < tokens.Count; j++)
                {
                    int gap = tokens[j].Position - expected;
                    if (gap > slop - skipped)
                    {
                        break;
                    }

                    if (gap >= 0 && tokens[j].Term == terms[k])
                    {
                        skipped += gap;
                        last = j;
                        ok = true;
                        break;
                    }
                }
            }

            if (ok && Valid(tokens[i], text) && Valid(tokens[last], text) && tokens[last].EndOffset > tokens[i].StartOffset)
            {
                spans.Add((tokens[i].StartOffset, tokens[last].EndOffset));
            }
        }
    }
}
