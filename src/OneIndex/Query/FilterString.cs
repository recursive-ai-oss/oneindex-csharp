using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

/// <summary>
/// The filter string: a small expression language for selecting documents by field values, in the style of Meilisearch's filter
/// expressions (<c>genres = horror AND (rating &gt; 85 OR director = 'Jordan Peele')</c>). A filter selects and never scores. It is
/// checked against the schema while it is read, so unknown, unfilterable and mistyped fields are reported with their position. The
/// grammar is in <c>docs/filter-strings.md</c>.
/// </summary>
public static class FilterString
{
    /// <summary>The longest filter read, in UTF-16 code units. Longer input is rejected.</summary>
    public const int MaxLength = 8192;

    /// <summary>The deepest nesting of parentheses and <c>NOT</c>s. Deeper input is rejected.</summary>
    public const int MaxDepth = 64;

    /// <summary>The most conditions in one filter.</summary>
    public const int MaxConditions = 512;

    /// <summary>The most values in one <c>IN [...]</c> list.</summary>
    public const int MaxListValues = 1024;

    /// <summary>Reads a filter string and compiles it to query objects made of <see cref="Occur.Filter"/> clauses.</summary>
    /// <param name="filter">The filter text.</param>
    /// <param name="schema">The schema whose fields it may mention.</param>
    /// <returns>The query. <c>a AND b</c> is a boolean query of two filter clauses, <c>a OR b</c> of two should clauses, <c>NOT a</c> every document except <c>a</c>.</returns>
    /// <exception cref="QueryParseException">The filter is not valid; the exception carries the offset, line, column and what was expected.</exception>
    public static Query Parse(string filter, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(schema);
        return new Parser(filter, schema).ParseAll();
    }

    /// <summary>Reads a filter string without throwing for invalid input.</summary>
    /// <param name="filter">The filter text.</param>
    /// <param name="schema">The schema whose fields it may mention.</param>
    /// <param name="query">The query when the filter is valid.</param>
    /// <param name="error">The error when it is not.</param>
    /// <returns><see langword="true"/> when the filter is valid.</returns>
    public static bool TryParse(string filter, Schema schema, [NotNullWhen(true)] out Query? query, [NotNullWhen(false)] out QueryParseException? error)
    {
        try
        {
            query = Parse(filter, schema);
            error = null;
            return true;
        }
        catch (QueryParseException e)
        {
            query = null;
            error = e;
            return false;
        }
    }

    /// <summary>Writes query objects of the shapes <see cref="Parse"/> produces back as a filter string that parses to an equal query.</summary>
    /// <param name="query">A term, range or boolean query (all <see cref="Occur.Filter"/> clauses, all <see cref="Occur.Should"/> clauses with <c>MinimumShouldMatch</c> 1, or the "everything except" pair) over filterable fields, with boost 1.</param>
    /// <param name="schema">The schema, which gives each field's type.</param>
    /// <returns>The filter text.</returns>
    /// <exception cref="ArgumentException">The query has a shape that has no filter string.</exception>
    public static string Print(Query query, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(schema);
        var sb = new StringBuilder();
        PrintNode(query, schema, sb, root: true);
        return sb.ToString();
    }

    private static void PrintNode(Query q, Schema schema, StringBuilder sb, bool root)
    {
        if (q.Boost != 1f)
        {
            throw new ArgumentException("A filter has no boost.", nameof(q));
        }

        switch (q)
        {
            case TermQuery t:
                sb.Append(Name(t.Field)).Append(" = ").Append(Quote(t.Term));
                return;
            case RangeQuery r:
                PrintRange(r, schema, sb);
                return;
            case BooleanQuery b:
                PrintBoolean(b, schema, sb, root);
                return;
            default:
                throw new ArgumentException($"A {q.GetType().Name} has no filter string.", nameof(q));
        }
    }

    private static void PrintBoolean(BooleanQuery b, Schema schema, StringBuilder sb, bool root)
    {
        IReadOnlyList<BooleanClause> c = b.Clauses;
        if (c.Count == 2 && c[0].Occur == Occur.Filter && c[0].Query is MatchAllQuery { Boost: 1f } && c[1].Occur == Occur.MustNot && b.MinimumShouldMatch == 0)
        {
            sb.Append("NOT ");
            PrintChild(c[1].Query, schema, sb);
            return;
        }

        bool and = c.Count >= 2 && b.MinimumShouldMatch == 0 && c.All(static x => x.Occur == Occur.Filter);
        bool or = c.Count >= 2 && b.MinimumShouldMatch == 1 && c.All(static x => x.Occur == Occur.Should);
        if (!and && !or)
        {
            throw new ArgumentException("This boolean query has no filter string: use filter clauses for AND, or should clauses with MinimumShouldMatch 1 for OR.", nameof(b));
        }

        for (int i = 0; i < c.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(and ? " AND " : " OR ");
            }

            PrintChild(c[i].Query, schema, sb);
        }
    }

    // Every boolean below the root is parenthesized, so that the printed text reads back with the same nesting.
    private static void PrintChild(Query q, Schema schema, StringBuilder sb)
    {
        if (q is BooleanQuery b && !IsNot(b))
        {
            sb.Append('(');
            PrintNode(q, schema, sb, root: false);
            sb.Append(')');
        }
        else
        {
            PrintNode(q, schema, sb, root: false);
        }
    }

    private static bool IsNot(BooleanQuery b)
        => b.Clauses.Count == 2 && b.Clauses[0].Occur == Occur.Filter && b.Clauses[0].Query is MatchAllQuery && b.Clauses[1].Occur == Occur.MustNot && b.MinimumShouldMatch == 0 && b.Boost == 1f;

    private static void PrintRange(RangeQuery r, Schema schema, StringBuilder sb)
    {
        if (!schema.TryGetField(r.Field, out FieldDefinition? def))
        {
            throw new ArgumentException($"The schema has no field '{r.Field}'.", nameof(r));
        }

        string f = Name(r.Field);
        if (r.Lower is null && r.Upper is null)
        {
            sb.Append(f).Append(" EXISTS");
            return;
        }

        if (r.Lower is { } lo && r.Upper is { } hi)
        {
            if (!r.IncludeLower || !r.IncludeUpper)
            {
                throw new ArgumentException("A range with two bounds has a filter string only when both bounds are inclusive.", nameof(r));
            }

            if (def.Type != FieldType.Keyword && lo.Equals(hi))
            {
                sb.Append(f).Append(" = ").Append(Literal(lo));
            }
            else
            {
                sb.Append(f).Append(' ').Append(Literal(lo)).Append(" TO ").Append(Literal(hi));
            }

            return;
        }

        if (r.Lower is { } only)
        {
            sb.Append(f).Append(r.IncludeLower ? " >= " : " > ").Append(Literal(only));
        }
        else
        {
            sb.Append(f).Append(r.IncludeUpper ? " <= " : " < ").Append(Literal(r.Upper!.Value));
        }
    }

    private static string Literal(FieldValue v) => v.Kind switch
    {
        FieldValueKind.Text => Quote(v.AsText()),
        FieldValueKind.Integral or FieldValueKind.Fractional => ValueBinder.FormatNumber(v),
        FieldValueKind.Bool => v.AsBool() ? "true" : "false",
        FieldValueKind.Date => Quote(ValueBinder.FormatDate(v)),
        _ => throw new ArgumentException($"A {v.Kind} value has no filter string."),
    };

    private static string Name(string field) => IsPlainWord(field) && !IsKeyword(field) ? field : Quote(field);

    private static bool IsKeyword(string s)
        => s.Equals("AND", StringComparison.OrdinalIgnoreCase) || s.Equals("OR", StringComparison.OrdinalIgnoreCase) || s.Equals("NOT", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainWord(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        foreach (char c in s)
        {
            if (char.IsWhiteSpace(c) || "()[],=!<>'\"\\".Contains(c, StringComparison.Ordinal) || char.IsControl(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (char c in s)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    private enum Tk : byte
    {
        End,
        Word,
        Quoted,
        LParen,
        RParen,
        LBracket,
        RBracket,
        Comma,
        Eq,
        Ne,
        Gt,
        Ge,
        Lt,
        Le,
    }

    private readonly record struct Tok(Tk Kind, int Start, int End, string Text);

    private sealed class Parser
    {
        private readonly string _s;
        private readonly Schema _schema;
        private int _pos;
        private Tok _tok;
        private int _depth;
        private int _conditions;

        internal Parser(string s, Schema schema)
        {
            _s = s;
            _schema = schema;
        }

        internal Query ParseAll()
        {
            if (_s.Length > MaxLength)
            {
                throw QueryParseException.At(_s, MaxLength, 0, $"The filter is longer than the limit of {MaxLength} characters.");
            }

            Advance();
            if (_tok.Kind == Tk.End)
            {
                throw Expected("a condition such as price > 10, or NOT, or '('");
            }

            Query q = ParseOr();
            if (_tok.Kind != Tk.End)
            {
                throw Expected("AND, OR or the end of the filter");
            }

            return q;
        }

        private Query ParseOr()
        {
            Query first = ParseAnd();
            if (!IsWord("OR"))
            {
                return first;
            }

            var clauses = new List<BooleanClause> { new(first, Occur.Should) };
            while (IsWord("OR"))
            {
                Advance();
                clauses.Add(new BooleanClause(ParseAnd(), Occur.Should));
            }

            return new BooleanQuery(clauses) { MinimumShouldMatch = 1 };
        }

        private Query ParseAnd()
        {
            Query first = ParseUnary();
            if (!IsWord("AND"))
            {
                return first;
            }

            var clauses = new List<BooleanClause> { new(first, Occur.Filter) };
            while (IsWord("AND"))
            {
                Advance();
                clauses.Add(new BooleanClause(ParseUnary(), Occur.Filter));
            }

            return new BooleanQuery(clauses);
        }

        private Query ParseUnary()
        {
            if (IsWord("NOT"))
            {
                Enter(_tok);
                Advance();
                Query inner = ParseUnary();
                _depth--;
                return Negate(inner);
            }

            if (_tok.Kind == Tk.LParen)
            {
                Tok open = _tok;
                Enter(open);
                Advance();
                Query inner = ParseOr();
                if (_tok.Kind != Tk.RParen)
                {
                    throw Expected("')' to close the '(' at offset " + open.Start.ToString(CultureInfo.InvariantCulture));
                }

                Advance();
                _depth--;
                return inner;
            }

            return ParseCondition();
        }

        private void Enter(Tok at)
        {
            if (++_depth > MaxDepth)
            {
                throw QueryParseException.At(_s, at.Start, at.End - at.Start, $"The filter is nested deeper than the limit of {MaxDepth}.");
            }
        }

        private Query ParseCondition()
        {
            Tok fieldTok = _tok;
            if (fieldTok.Kind is not (Tk.Word or Tk.Quoted) || (fieldTok.Kind == Tk.Word && (IsWord("AND") || IsWord("OR"))))
            {
                throw Expected("a field name, NOT or '('");
            }

            if (++_conditions > MaxConditions)
            {
                throw QueryParseException.At(_s, fieldTok.Start, fieldTok.End - fieldTok.Start, $"The filter has more than {MaxConditions} conditions.");
            }

            FieldDefinition def = Resolve(fieldTok);
            Advance();
            Tok op = _tok;
            switch (op.Kind)
            {
                case Tk.Eq:
                    Advance();
                    return EqualTo(def, TakeValue());
                case Tk.Ne:
                    Advance();
                    return Negate(EqualTo(def, TakeValue()));
                case Tk.Gt:
                case Tk.Ge:
                case Tk.Lt:
                case Tk.Le:
                    Advance();
                    return Compare(def, op, TakeValue());
            }

            if (op.Kind == Tk.Word)
            {
                if (IsWord("NOT"))
                {
                    Advance();
                    if (!IsWord("IN") && !IsWord("EXISTS"))
                    {
                        throw Expected("IN or EXISTS after NOT");
                    }

                    return Negate(KeywordCondition(def));
                }

                if (IsWord("IN") || IsWord("EXISTS") || IsWord("IS"))
                {
                    return KeywordCondition(def);
                }
            }

            if (op.Kind is Tk.Word or Tk.Quoted)
            {
                Tok lo = TakeValue();
                if (!IsWord("TO"))
                {
                    throw Expected("TO");
                }

                Advance();
                return Between(def, lo, TakeValue());
            }

            throw Expected("an operator (=, !=, >, >=, <, <=, TO, IN, EXISTS, IS NULL, IS EMPTY) or a value before TO");
        }

        // IN [...], EXISTS, IS [NOT] NULL, IS [NOT] EMPTY. The current token is the keyword.
        private Query KeywordCondition(FieldDefinition def)
        {
            if (IsWord("EXISTS"))
            {
                Tok at = _tok;
                Advance();
                return Exists(def, at);
            }

            if (IsWord("IS"))
            {
                Tok at = _tok;
                Advance();
                bool not = false;
                if (IsWord("NOT"))
                {
                    not = true;
                    Advance();
                }

                if (!IsWord("NULL") && !IsWord("EMPTY"))
                {
                    throw Expected("NULL or EMPTY" + (not ? string.Empty : " (or NOT)"));
                }

                Advance();

                // A document never holds an explicit null or an empty value (empty strings and null are not indexed), so both
                // tests are the absence of a value.
                Query exists = Exists(def, at);
                return not ? exists : Negate(exists);
            }

            // IN
            Advance();
            if (_tok.Kind != Tk.LBracket)
            {
                throw Expected("'[' to start the list of values");
            }

            Advance();
            var alternatives = new List<Query>();
            if (_tok.Kind != Tk.RBracket)
            {
                while (true)
                {
                    if (alternatives.Count >= MaxListValues)
                    {
                        throw QueryParseException.At(_s, _tok.Start, _tok.End - _tok.Start, $"The list has more than {MaxListValues} values.");
                    }

                    alternatives.Add(EqualTo(def, TakeValue()));
                    if (_tok.Kind == Tk.Comma)
                    {
                        Advance();
                        continue;
                    }

                    break;
                }
            }

            if (_tok.Kind != Tk.RBracket)
            {
                throw Expected("',' or ']'");
            }

            Advance();
            return alternatives.Count switch
            {
                0 => None(),
                1 => alternatives[0],
                _ => new BooleanQuery(alternatives.ConvertAll(static a => new BooleanClause(a, Occur.Should))) { MinimumShouldMatch = 1 },
            };
        }

        private RangeQuery Exists(FieldDefinition def, Tok at)
        {
            if (def.Type == FieldType.Text)
            {
                throw QueryParseException.At(_s, at.Start, at.End - at.Start, $"EXISTS is not available on full-text field '{def.Name}'.");
            }

            return new RangeQuery(def.Name, null, null);
        }

        private Tok TakeValue()
        {
            if (_tok.Kind is not (Tk.Word or Tk.Quoted))
            {
                throw Expected("a value (a word, or text in quotes)");
            }

            Tok v = _tok;
            Advance();
            return v;
        }

        private FieldDefinition Resolve(Tok fieldTok)
        {
            string name = fieldTok.Text;
            if (!_schema.TryGetField(name, out FieldDefinition? def))
            {
                string? near = QuerySyntax.Nearest(name, _schema.Fields.Where(IsFilterable).Select(static f => f.Name));
                string hint = near is null ? string.Empty : $" Did you mean '{near}'?";
                throw QueryParseException.At(_s, fieldTok.Start, fieldTok.End - fieldTok.Start, $"The schema has no field {QuerySyntax.Q(name)}.{hint}", "a filterable field name", near);
            }

            if (def.Type == FieldType.Text)
            {
                throw QueryParseException.At(_s, fieldTok.Start, fieldTok.End - fieldTok.Start, $"Field '{name}' is full-text; a filter compares exact values. Filter on a [Keyword] field, or search the text.");
            }

            if (!IsFilterable(def))
            {
                string why = def.Indexed ? $"a {def.Type} field cannot be filtered" : "it is not filterable (mark it [Filterable], or [Keyword] for text)";
                throw QueryParseException.At(_s, fieldTok.Start, fieldTok.End - fieldTok.Start, $"Field '{name}' cannot be used in a filter: {why}.");
            }

            return def;
        }

        private static bool IsFilterable(FieldDefinition f)
            => f.Indexed && f.Type is FieldType.Keyword or FieldType.Integral or FieldType.Fractional or FieldType.Date or FieldType.Bool;

        private Query EqualTo(FieldDefinition def, Tok value)
        {
            if (def.Type == FieldType.Keyword)
            {
                return new TermQuery(def.Name, value.Text);
            }

            FieldValue v = Bind(def, value, BoundRole.Equal, out _, out bool nothing);
            return nothing ? None() : new RangeQuery(def.Name, v, v);
        }

        private RangeQuery Compare(FieldDefinition def, Tok op, Tok value)
        {
            if (def.Type == FieldType.Bool)
            {
                throw QueryParseException.At(_s, op.Start, op.End - op.Start, $"Ordering comparisons are not available on boolean field '{def.Name}'; use = or !=.");
            }

            bool lower = op.Kind is Tk.Gt or Tk.Ge;
            bool inclusive = op.Kind is Tk.Ge or Tk.Le;
            FieldValue v = Bind(def, value, lower ? BoundRole.Lower : BoundRole.Upper, out bool rounded, out _);
            inclusive |= rounded;
            return lower
                ? new RangeQuery(def.Name, v, null) { IncludeLower = inclusive }
                : new RangeQuery(def.Name, null, v) { IncludeUpper = inclusive };
        }

        private RangeQuery Between(FieldDefinition def, Tok lo, Tok hi)
        {
            if (def.Type == FieldType.Bool)
            {
                throw QueryParseException.At(_s, lo.Start, hi.End - lo.Start, $"TO is not available on boolean field '{def.Name}'.");
            }

            FieldValue a = Bind(def, lo, BoundRole.Lower, out _, out _);
            FieldValue b = Bind(def, hi, BoundRole.Upper, out _, out _);
            return new RangeQuery(def.Name, a, b);
        }

        private FieldValue Bind(FieldDefinition def, Tok value, BoundRole role, out bool rounded, out bool nothing)
        {
            LiteralKind kind = value.Kind == Tk.Quoted ? LiteralKind.Text : LiteralKind.Bare;
            string? error = ValueBinder.Bind(def, kind, value.Text, role, lenient: true, out FieldValue v, out rounded, out nothing);
            if (error is not null)
            {
                throw QueryParseException.At(_s, value.Start, value.End - value.Start, error, ValueBinder.Describe(def.Type));
            }

            return v;
        }

        private static BooleanQuery Negate(Query inner)
            => new BooleanQuery([new BooleanClause(new MatchAllQuery(), Occur.Filter), new BooleanClause(inner, Occur.MustNot)]);

        private static BooleanQuery None() => new([]);

        private bool IsWord(string keyword)
            => _tok.Kind == Tk.Word && _tok.Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

        private QueryParseException Expected(string what)
            => QueryParseException.At(_s, _tok.Start, _tok.End - _tok.Start, $"Expected {what}, but found {Describe(_tok)}.", what);

        private static string Describe(Tok t) => t.Kind switch
        {
            Tk.End => "the end of the filter",
            Tk.Word => "'" + t.Text + "'",
            Tk.Quoted => "text in quotes",
            Tk.LParen => "'('",
            Tk.RParen => "')'",
            Tk.LBracket => "'['",
            Tk.RBracket => "']'",
            Tk.Comma => "','",
            Tk.Eq => "'='",
            Tk.Ne => "'!='",
            Tk.Gt => "'>'",
            Tk.Ge => "'>='",
            Tk.Lt => "'<'",
            _ => "'<='",
        };

        private void Advance()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
            {
                _pos++;
            }

            int start = _pos;
            if (_pos >= _s.Length)
            {
                _tok = new Tok(Tk.End, _s.Length, _s.Length, string.Empty);
                return;
            }

            char c = _s[_pos];
            switch (c)
            {
                case '(':
                    One(Tk.LParen);
                    return;
                case ')':
                    One(Tk.RParen);
                    return;
                case '[':
                    One(Tk.LBracket);
                    return;
                case ']':
                    One(Tk.RBracket);
                    return;
                case ',':
                    One(Tk.Comma);
                    return;
                case '=':
                    One(Tk.Eq);
                    return;
                case '!':
                    if (_pos + 1 < _s.Length && _s[_pos + 1] == '=')
                    {
                        _pos += 2;
                        _tok = new Tok(Tk.Ne, start, _pos, "!=");
                        return;
                    }

                    throw QueryParseException.At(_s, start, 1, "Expected '=' after '!' (the inequality operator is !=).", "'='");
                case '>':
                case '<':
                    _pos++;
                    if (_pos < _s.Length && _s[_pos] == '=')
                    {
                        _pos++;
                        _tok = new Tok(c == '>' ? Tk.Ge : Tk.Le, start, _pos, string.Empty);
                        return;
                    }

                    _tok = new Tok(c == '>' ? Tk.Gt : Tk.Lt, start, _pos, string.Empty);
                    return;
                case '\'':
                case '"':
                    ReadQuoted(c);
                    return;
            }

            while (_pos < _s.Length)
            {
                char w = _s[_pos];
                if (char.IsWhiteSpace(w) || "()[],=!<>'\"".Contains(w, StringComparison.Ordinal))
                {
                    break;
                }

                _pos++;
            }

            _tok = new Tok(Tk.Word, start, _pos, _s.Substring(start, _pos - start));
        }

        private void One(Tk kind)
        {
            _tok = new Tok(kind, _pos, _pos + 1, string.Empty);
            _pos++;
        }

        private void ReadQuoted(char quote)
        {
            int start = _pos;
            _pos++;
            var sb = new StringBuilder();
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c == quote)
                {
                    _pos++;
                    _tok = new Tok(Tk.Quoted, start, _pos, sb.ToString());
                    return;
                }

                if (c == '\\')
                {
                    if (_pos + 1 >= _s.Length)
                    {
                        break;
                    }

                    char e = _s[_pos + 1];
                    if (e is not ('\\' or '"' or '\''))
                    {
                        throw QueryParseException.At(_s, _pos, 2, "Unknown escape. Inside quotes, \\\\, \\\" and \\' are the only escapes.", "\\\\, \\\" or \\'");
                    }

                    sb.Append(e);
                    _pos += 2;
                    continue;
                }

                sb.Append(c);
                _pos++;
            }

            throw QueryParseException.At(_s, start, _s.Length - start, $"The text in quotes that starts here is not closed; expected a closing {quote}.", quote.ToString());
        }
    }
}
