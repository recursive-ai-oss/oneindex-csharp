using System.Text;
using OneIndex.Analysis;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

/// <summary>One word or quoted phrase of user text.</summary>
/// <param name="Text">The text without its quotes, with control characters and unpaired surrogates already replaced by spaces.</param>
/// <param name="IsPhrase">Whether the text was quoted, so its words must appear next to each other.</param>
public readonly record struct UserTextOperand(string Text, bool IsPhrase);

/// <summary>
/// User text after parsing: the groups of alternatives that must each match, and the operands that must not match. Parsing needs no
/// schema and never throws; the grammar is in <c>docs/user-text.md</c>.
/// </summary>
public sealed class UserText
{
    /// <summary>The longest input read, in UTF-16 code units. Longer input is cut (never inside a surrogate pair).</summary>
    public const int MaxLength = 4096;

    /// <summary>The most operands (positive and excluded together) read. Later ones are ignored.</summary>
    public const int MaxOperands = 32;

    internal UserText(IReadOnlyList<IReadOnlyList<UserTextOperand>> required, IReadOnlyList<UserTextOperand> excluded, bool blank)
    {
        Required = required;
        Excluded = excluded;
        IsBlank = blank;
    }

    /// <summary>The groups that must each match; a group matches when any of its operands does (<c>a OR b</c>).</summary>
    public IReadOnlyList<IReadOnlyList<UserTextOperand>> Required { get; }

    /// <summary>The operands a matching document must not contain (<c>-word</c>, <c>-"a phrase"</c>).</summary>
    public IReadOnlyList<UserTextOperand> Excluded { get; }

    /// <summary>Whether the input was null, empty or only whitespace and control characters. A blank query matches every document.</summary>
    public bool IsBlank { get; }

    /// <summary>Parses user text. Any input yields a result.</summary>
    /// <param name="input">The text; <see langword="null"/> is blank.</param>
    /// <returns>The parsed query.</returns>
    public static UserText Parse(string? input)
    {
        string text = Sanitize(input);
        var required = new List<List<UserTextOperand>>();
        var excluded = new List<UserTextOperand>();
        bool pendingOr = false;
        int operands = 0;
        bool anyText = false;
        int i = 0;
        int n = text.Length;
        while (i < n && operands < MaxOperands)
        {
            if (text[i] == ' ')
            {
                i++;
                continue;
            }

            anyText = true;
            bool negate = false;
            if (text[i] == '-' && i + 1 < n && text[i + 1] != ' ')
            {
                negate = true;
                i++;
            }

            string operand;
            bool phrase = false;
            if (text[i] == '"')
            {
                // An unclosed quote runs to the end of the input.
                int close = text.IndexOf('"', i + 1);
                if (close < 0)
                {
                    close = n;
                }

                operand = text.Substring(i + 1, close - i - 1);
                phrase = true;
                i = Math.Min(n, close + 1);
            }
            else
            {
                int start = i;
                while (i < n && text[i] != ' ' && text[i] != '"')
                {
                    i++;
                }

                operand = text.Substring(start, i - start);
            }

            if (!phrase && !negate && operand == "OR")
            {
                pendingOr = true;
                continue;
            }

            if (phrase && operand.AsSpan().Trim().IsEmpty)
            {
                continue;
            }

            operands++;
            var item = new UserTextOperand(operand, phrase);
            if (negate)
            {
                // An exclusion between "a OR" and its right-hand side cancels the OR: the operator then has no operand to join.
                pendingOr = false;
                excluded.Add(item);
            }
            else if (pendingOr && required.Count > 0)
            {
                required[^1].Add(item);
                pendingOr = false;
            }
            else
            {
                required.Add([item]);
                pendingOr = false;
            }
        }

        return new UserText(required.ConvertAll(static g => (IReadOnlyList<UserTextOperand>)g), excluded, !anyText);
    }

    // Controls and lone surrogates cannot be part of a word. Each becomes a space, so it separates words and nothing downstream sees it.
    private static string Sanitize(string? input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        int length = Math.Min(input.Length, MaxLength);
        if (length < input.Length && char.IsHighSurrogate(input[length - 1]))
        {
            length--;
        }

        var sb = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            char c = input[i];
            if (char.IsHighSurrogate(c) && i + 1 < length && char.IsLowSurrogate(input[i + 1]))
            {
                sb.Append(c).Append(input[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.IsControl(c) || char.IsWhiteSpace(c))
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}

/// <summary>
/// Turns <see cref="UserText"/> into query objects over a schema's full-text fields. Each operand is analyzed once per distinct analyzer
/// of those fields, so that a stemmed field and an unstemmed field are each searched with their own terms; fields that share an analyzer
/// are scored together as one BM25F term.
/// </summary>
public sealed class UserTextCompiler
{
    private readonly List<(string Analyzer, List<string> Fields)> _groups = [];
    private readonly Dictionary<string, Analyzer> _analyzers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SynonymMap> _synonyms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _stopWords = new(StringComparer.Ordinal);
    private readonly TextSettings _settings;

    /// <summary>Creates a compiler for a schema.</summary>
    /// <param name="schema">The schema; its indexed text fields with positions are searched.</param>
    public UserTextCompiler(Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        _settings = schema.Settings;
        foreach (FieldDefinition f in schema.Fields)
        {
            if (f.Type != FieldType.Text || !f.Indexed || f.IndexOptions == IndexOptions.None)
            {
                continue;
            }

            string analyzer = f.Analyzer ?? FieldDefinition.DefaultAnalyzer;
            int g = _groups.FindIndex(x => x.Analyzer == analyzer);
            if (g < 0)
            {
                _groups.Add((analyzer, [f.Name]));
                _analyzers[analyzer] = AnalyzerRegistry.Create(analyzer);
            }
            else
            {
                _groups[g].Fields.Add(f.Name);
            }
        }

        PrepareSettings();
    }

    private void PrepareSettings()
    {
        // Synonyms and stop words are analyzed with each field group's own analyzer, so that they meet the terms the index holds.
        if (_settings.Synonyms.Count == 0 && _settings.StopWords.Count == 0)
        {
            return;
        }

        foreach ((string name, Analyzer analyzer) in _analyzers)
        {
            if (_settings.Synonyms.Count > 0)
            {
                var rules = new SynonymRules();
                foreach (SynonymRule r in _settings.Synonyms)
                {
                    _ = r.IsGroup ? rules.AddGroup([.. r.From]) : rules.AddOneWay(r.From[0], [.. r.To]);
                }

                _synonyms[name] = rules.Build(analyzer);
            }

            var stop = new HashSet<string>(StringComparer.Ordinal);
            foreach (string w in _settings.StopWords)
            {
                foreach (AnalyzedToken t in analyzer.Analyze(w))
                {
                    stop.Add(t.Term);
                }
            }

            _stopWords[name] = stop;
        }
    }

    /// <summary>Whether the schema has any field that user text can search.</summary>
    public bool HasSearchableFields => _groups.Count > 0;

    // The last word of the text is the one being typed, unless the text ends in a space or a quote.
    private static bool EndsInWord(string input, UserTextOperand last)
        => !last.IsPhrase && last.Text.Length > 0 && input.TrimEnd().EndsWith(last.Text, StringComparison.Ordinal) && input.Length > 0 && !char.IsWhiteSpace(input[^1]);

    /// <summary>Compiles user text.</summary>
    /// <param name="input">The text; <see langword="null"/> or blank matches every document.</param>
    /// <returns>
    /// A query that scores the text with BM25F. It matches nothing when the text has words but none survives analysis (only stop words
    /// or punctuation) and nothing is excluded, and when the schema has no searchable field. It never throws.
    /// </returns>
    public Search.Query Compile(string? input)
    {
        UserText parsed = UserText.Parse(input);
        if (parsed.IsBlank)
        {
            return new MatchAllQuery();
        }

        // As-you-type applies to the last operand of the text, when it is a plain word still being typed.
        UserTextOperand? typed = null;
        if (_settings.PrefixSearch && parsed.Required.Count > 0 && parsed.Required[^1].Count > 0)
        {
            UserTextOperand last = parsed.Required[^1][^1];
            if (EndsInWord(input ?? string.Empty, last))
            {
                typed = last;
            }
        }

        var clauses = new List<BooleanClause>();
        for (int g = 0; g < parsed.Required.Count; g++)
        {
            IReadOnlyList<UserTextOperand> group = parsed.Required[g];
            var alternatives = new List<Search.Query>();
            for (int o = 0; o < group.Count; o++)
            {
                bool isTyped = typed is not null && g == parsed.Required.Count - 1 && o == group.Count - 1;
                if (CompileOperand(group[o], isTyped, exact: false) is { } q)
                {
                    alternatives.Add(q);
                }
            }

            if (alternatives.Count > 0)
            {
                clauses.Add(new BooleanClause(AnyOf(alternatives), Occur.Must));
            }
        }

        int required = clauses.Count;
        foreach (UserTextOperand operand in parsed.Excluded)
        {
            if (CompileOperand(operand, false, exact: true) is { } q)
            {
                clauses.Add(new BooleanClause(q, Occur.MustNot));
            }
        }

        if (clauses.Count == 0)
        {
            return new BooleanQuery([]);
        }

        if (required == 0)
        {
            // Only exclusions: every document except those, as a filter so that scores stay constant.
            clauses.Insert(0, new BooleanClause(new MatchAllQuery(), Occur.Must));
        }

        return clauses.Count == 1 ? clauses[0].Query : new BooleanQuery(clauses);
    }

    private static Search.Query AnyOf(List<Search.Query> alternatives)
        => alternatives.Count == 1 ? alternatives[0] : new BooleanQuery(alternatives.ConvertAll(static q => new BooleanClause(q, Occur.Should))) { MinimumShouldMatch = 1 };

    // exact: the operand is excluded (-word), so it is matched as written, without typos, synonyms or as-you-type.
    private Search.Query? CompileOperand(UserTextOperand operand, bool typing, bool exact)
    {
        var perGroup = new List<Search.Query>();
        foreach ((string analyzerName, List<string> fields) in _groups)
        {
            IReadOnlyList<AnalyzedToken> analyzed = _analyzers[analyzerName].Analyze(operand.Text);
            if (analyzed.Count == 0)
            {
                continue;
            }

            // Stop words are dropped where they stand; the positions of the rest stay, so a phrase keeps the gap.
            IReadOnlyList<AnalyzedToken> tokens = analyzed;
            if (!exact && _stopWords.TryGetValue(analyzerName, out HashSet<string>? stop) && stop.Count > 0)
            {
                var kept = new List<AnalyzedToken>(analyzed.Count);
                foreach (AnalyzedToken t in analyzed)
                {
                    if (!stop.Contains(t.Term))
                    {
                        kept.Add(t);
                    }
                }

                if (kept.Count == 0)
                {
                    continue;
                }

                tokens = kept;
            }

            var readings = new List<Search.Query>();
            if (tokens.Count == 1)
            {
                readings.Add(WordQuery(fields, tokens[0].Term, operand, typing && !exact, exact));
            }
            else
            {
                // Several terms from one word ("acme.corp", "state-of-the-art") or a quoted phrase: adjacent, in order. Positions the
                // analyzer skipped (stop words) widen the slop by as much so that the phrase still matches the text it came from.
                var terms = new string[tokens.Count];
                int slop = 0;
                for (int t = 0; t < tokens.Count; t++)
                {
                    terms[t] = tokens[t].Term;
                    if (t > 0)
                    {
                        slop += Math.Max(0, tokens[t].Position - tokens[t - 1].Position - 1);
                    }
                }

                foreach (string field in fields)
                {
                    readings.Add(new PhraseQuery(field, terms) { Slop = slop });
                }
            }

            if (!exact && _synonyms.TryGetValue(analyzerName, out SynonymMap? map) && map.KeyCount > 0)
            {
                var key = new string[tokens.Count];
                for (int t = 0; t < key.Length; t++)
                {
                    key[t] = tokens[t].Term;
                }

                foreach (IReadOnlyList<string> alternative in map.GetSynonyms(key))
                {
                    if (alternative.Count == 1)
                    {
                        readings.Add(WordQuery(fields, alternative[0], operand, typing: false, exact: true, TokenGraphQueries.DefaultSynonymBoost));
                    }
                    else
                    {
                        foreach (string field in fields)
                        {
                            readings.Add(new PhraseQuery(field, [.. alternative]) { Boost = TokenGraphQueries.DefaultSynonymBoost });
                        }
                    }
                }
            }

            perGroup.AddRange(readings);
        }

        return perGroup.Count == 0 ? null : AnyOf(perGroup);
    }

    // One analyzed word in the fields of one analyzer group: a plain term, or a typo-tolerant one when the settings allow typos or the
    // word is the one being typed.
    private Search.Query WordQuery(List<string> fields, string term, UserTextOperand operand, bool typing, bool exact, float boost = 1f)
    {
        TypoSettings typo = _settings.Typo;
        int budget = exact || operand.IsPhrase ? 0 : typo.Budget(CountCodePoints(term));
        if (budget > 0 && ((typo.DisableOnNumbers && IsNumber(term)) || IsListed(typo.DisableOnWords, term, operand.Text)))
        {
            budget = 0;
        }

        bool prefix = typing && !operand.IsPhrase;
        if (budget == 0 && !prefix)
        {
            return fields.Count == 1 ? new TermQuery(fields[0], term) { Boost = boost } : new MultiFieldTermQuery(fields.ToArray(), term) { Boost = boost };
        }

        return new TypoTermQuery(fields.ToArray(), term) { MaxTypos = budget, Prefix = prefix, ExactFields = typo.DisableOnFields.Count == 0 ? [] : fields.Where(typo.DisableOnFields.Contains).ToArray(), Boost = boost };
    }

    private static int CountCodePoints(string s)
    {
        int n = 0;
        foreach (Rune _ in s.EnumerateRunes())
        {
            n++;
        }

        return n;
    }

    private static bool IsNumber(string term)
    {
        foreach (char c in term)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return term.Length > 0;
    }

    private static bool IsListed(IReadOnlyList<string> words, string term, string raw)
    {
        foreach (string w in words)
        {
            if (string.Equals(w, term, StringComparison.OrdinalIgnoreCase) || string.Equals(w, raw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
