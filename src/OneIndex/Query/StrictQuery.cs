using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using OneIndex.Analysis;
using OneIndex.Codecs;
using OneIndex.Index;
using OneIndex.Search;

namespace OneIndex.Queries;

/// <summary>The kinds of node in a strict query's syntax tree.</summary>
public enum StrictNodeKind : byte
{
    /// <summary>Every document (<c>*</c>).</summary>
    MatchAll,

    /// <summary>A word, optionally in a field (<c>title:invoice</c>).</summary>
    Term,

    /// <summary>A quoted phrase with optional slop (<c>"acme corp"~2</c>).</summary>
    Phrase,

    /// <summary>A word with a trailing star (<c>inv*</c>).</summary>
    Prefix,

    /// <summary>A word with a tilde and an optional edit distance (<c>acme~1</c>).</summary>
    Fuzzy,

    /// <summary>A regular expression between slashes (<c>tag:/ab+c/</c>).</summary>
    Regex,

    /// <summary>A range of values (<c>total:[10 TO 20}</c>, <c>due:&gt;2024-01-01</c>) or, with both bounds open, <c>field:*</c>.</summary>
    Range,

    /// <summary>All children must match.</summary>
    And,

    /// <summary>At least one child must match.</summary>
    Or,

    /// <summary>The child must not match.</summary>
    Not,
}

/// <summary>
/// A position in the query text. Two spans are always equal, so that comparing syntax trees ignores where each node was written.
/// </summary>
public readonly struct SourceSpan : IEquatable<SourceSpan>
{
    /// <summary>Creates a span.</summary>
    /// <param name="start">Offset of the first character.</param>
    /// <param name="end">Offset after the last character.</param>
    public SourceSpan(int start, int end)
    {
        Start = start;
        End = end;
    }

    /// <summary>Offset of the first character.</summary>
    public int Start { get; }

    /// <summary>Offset after the last character.</summary>
    public int End { get; }

    /// <inheritdoc/>
    public bool Equals(SourceSpan other) => true;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SourceSpan;

    /// <inheritdoc/>
    public override int GetHashCode() => 0;

    /// <summary>Always <see langword="true"/>: spans do not take part in comparing trees.</summary>
    /// <param name="left">A span.</param>
    /// <param name="right">A span.</param>
    /// <returns><see langword="true"/>.</returns>
    public static bool operator ==(SourceSpan left, SourceSpan right) => true;

    /// <summary>Always <see langword="false"/>.</summary>
    /// <param name="left">A span.</param>
    /// <param name="right">A span.</param>
    /// <returns><see langword="false"/>.</returns>
    public static bool operator !=(SourceSpan left, SourceSpan right) => false;
}

/// <summary>
/// One node of a strict query's syntax tree: what was written, before analysis and before the schema is consulted. Trees compare by
/// content and ignore positions, so <c>Print</c> followed by <c>ParseSyntax</c> gives back an equal tree.
/// </summary>
public sealed class StrictNode : IEquatable<StrictNode>
{
    /// <summary>The kind of node.</summary>
    public StrictNodeKind Kind { get; init; }

    /// <summary>The field the node searches, or <see langword="null"/> for "any full-text field". Always set on a range.</summary>
    public string? Field { get; init; }

    /// <summary>The word, the phrase text without quotes, the prefix without its star, or the regular expression without slashes.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>The slop of a phrase (default 0) or the largest edit distance of a fuzzy term (default 2).</summary>
    public int Number { get; init; }

    /// <summary>The lower bound of a range as written, or <see langword="null"/> when open.</summary>
    public string? Lower { get; init; }

    /// <summary>The upper bound of a range as written, or <see langword="null"/> when open.</summary>
    public string? Upper { get; init; }

    /// <summary>Whether a value equal to the lower bound matches. Always true when the bound is open.</summary>
    public bool IncludeLower { get; init; } = true;

    /// <summary>Whether a value equal to the upper bound matches. Always true when the bound is open.</summary>
    public bool IncludeUpper { get; init; } = true;

    /// <summary>Multiplies the node's score (<c>^2</c>). Default 1.</summary>
    public float Boost { get; init; } = 1f;

    /// <summary>The operands of an <see cref="StrictNodeKind.And"/> or <see cref="StrictNodeKind.Or"/>, or the single operand of a <see cref="StrictNodeKind.Not"/>.</summary>
    public IReadOnlyList<StrictNode> Children { get; init; } = [];

    /// <summary>Where the node was written. Not part of equality.</summary>
    public SourceSpan Span { get; init; }

    /// <summary>Where the field name was written (or the group's field for an inherited one). Not part of equality.</summary>
    public SourceSpan FieldSpan { get; init; }

    /// <inheritdoc/>
    public bool Equals(StrictNode? other)
        => other is not null && Kind == other.Kind && Field == other.Field && Text == other.Text && Number == other.Number && Lower == other.Lower
            && Upper == other.Upper && IncludeLower == other.IncludeLower && IncludeUpper == other.IncludeUpper && Boost.Equals(other.Boost)
            && Children.SequenceEqual(other.Children);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as StrictNode);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        // In-process hash tables only; never persisted.
        var hash = default(HashCode);
        hash.Add(Kind);
        hash.Add(Field);
        hash.Add(Text);
        hash.Add(Number);
        hash.Add(Lower);
        hash.Add(Upper);
        hash.Add(Boost);
        foreach (StrictNode c in Children)
        {
            hash.Add(c);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public override string ToString() => StrictQuery.Print(this);

    internal StrictNode With(float boost, SourceSpan span) => new()
    {
        Kind = Kind,
        Field = Field,
        Text = Text,
        Number = Number,
        Lower = Lower,
        Upper = Upper,
        IncludeLower = IncludeLower,
        IncludeUpper = IncludeUpper,
        Boost = boost,
        Children = Children,
        Span = span,
        FieldSpan = FieldSpan,
    };
}

/// <summary>
/// The strict query syntax: an opt-in grammar for people and programs that want exactly what they wrote, with errors instead of
/// guesses. It has fields (<c>title:invoice</c>), boolean operators and grouping, phrases with slop, prefixes, fuzzy terms, regular
/// expressions, ranges and boosts. Unlike forgiving user text it throws a <see cref="QueryParseException"/> that says where the
/// problem is and what was expected, and it names fields that the schema does not have. The grammar is in
/// <c>docs/query-syntax.md</c>.
/// </summary>
public static class StrictQuery
{
    /// <summary>The longest query read, in UTF-16 code units. Longer input is rejected.</summary>
    public const int MaxLength = 8192;

    /// <summary>The deepest nesting of parentheses and <c>NOT</c>s.</summary>
    public const int MaxDepth = 64;

    /// <summary>The most nodes in one query.</summary>
    public const int MaxNodes = 1024;

    /// <summary>The largest slop of a phrase.</summary>
    public const int MaxSlop = 1000;

    /// <summary>Reads the syntax only, without a schema: fields are not checked and nothing is analyzed.</summary>
    /// <param name="text">The query.</param>
    /// <returns>The syntax tree.</returns>
    /// <exception cref="QueryParseException">The text is not valid; the exception carries the position and what was expected.</exception>
    public static StrictNode ParseSyntax(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new Parser(text).ParseAll();
    }

    /// <summary>Reads a query and compiles it against a schema.</summary>
    /// <param name="text">The query.</param>
    /// <param name="schema">The schema: field names are checked and words are analyzed with each field's analyzer.</param>
    /// <returns>
    /// The query objects. A word that analysis removes entirely (a stop word) is dropped from its parent, as in user text; when nothing
    /// is left the query matches no document.
    /// </returns>
    /// <exception cref="QueryParseException">The text is not valid, or names a field the schema lacks or uses a field in a way it cannot be used.</exception>
    public static Query Parse(string text, Schema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        StrictNode tree = ParseSyntax(text);
        return Bind(tree, schema, text);
    }

    /// <summary>Reads and compiles a query without throwing for invalid input.</summary>
    /// <param name="text">The query.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="query">The query when valid.</param>
    /// <param name="error">The error when not.</param>
    /// <returns><see langword="true"/> when the query is valid.</returns>
    public static bool TryParse(string text, Schema schema, [NotNullWhen(true)] out Query? query, [NotNullWhen(false)] out QueryParseException? error)
    {
        try
        {
            query = Parse(text, schema);
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

    /// <summary>Compiles a syntax tree against a schema.</summary>
    /// <param name="node">The tree, from <see cref="ParseSyntax"/> or built by hand.</param>
    /// <param name="schema">The schema.</param>
    /// <param name="source">The text the tree was read from, used to turn positions into lines and columns; <see langword="null"/> for a tree built by hand.</param>
    /// <returns>The query objects.</returns>
    /// <exception cref="QueryParseException">A field is unknown or cannot be used that way, or a value does not fit the field's type.</exception>
    public static Query Bind(StrictNode node, Schema schema, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(schema);
        return new Binder(schema, source ?? string.Empty).BindRoot(node);
    }

    /// <summary>Writes a syntax tree as query text that <see cref="ParseSyntax"/> reads back to an equal tree.</summary>
    /// <param name="node">The tree.</param>
    /// <returns>The text. Composite nodes are parenthesized, so the nesting survives.</returns>
    /// <exception cref="ArgumentException">The tree has no text form: an empty word, a range or an inherited-field node without a field, a node with the wrong shape.</exception>
    public static string Print(StrictNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var sb = new StringBuilder();
        PrintNode(node, sb);
        return sb.ToString();
    }

    private static void PrintNode(StrictNode n, StringBuilder sb)
    {
        bool composite = n.Kind is StrictNodeKind.And or StrictNodeKind.Or or StrictNodeKind.Not;
        bool wrap = composite && n.Boost != 1f;
        if (wrap)
        {
            sb.Append('(');
        }

        switch (n.Kind)
        {
            case StrictNodeKind.MatchAll:
                sb.Append('*');
                break;
            case StrictNodeKind.Term:
                AppendField(n, sb);
                sb.Append(EscapeWord(n.Text));
                break;
            case StrictNodeKind.Prefix:
                AppendField(n, sb);
                sb.Append(EscapeWord(n.Text)).Append('*');
                break;
            case StrictNodeKind.Fuzzy:
                AppendField(n, sb);
                sb.Append(EscapeWord(n.Text)).Append('~');
                if (n.Number != 2)
                {
                    sb.Append(n.Number.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case StrictNodeKind.Phrase:
                AppendField(n, sb);
                sb.Append('"');
                foreach (char c in n.Text)
                {
                    if (c is '"' or '\\')
                    {
                        sb.Append('\\');
                    }

                    sb.Append(c);
                }

                sb.Append('"');
                if (n.Number != 0)
                {
                    sb.Append('~').Append(n.Number.ToString(CultureInfo.InvariantCulture));
                }

                break;
            case StrictNodeKind.Regex:
                AppendField(n, sb);
                PrintRegex(n.Text, sb);
                break;
            case StrictNodeKind.Range:
                PrintRange(n, sb);
                break;
            case StrictNodeKind.And:
            case StrictNodeKind.Or:
                if (n.Children.Count < 2)
                {
                    throw new ArgumentException("An AND or OR node needs at least two operands.", nameof(n));
                }

                for (int i = 0; i < n.Children.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(n.Kind == StrictNodeKind.And ? " AND " : " OR ");
                    }

                    PrintChild(n.Children[i], sb);
                }

                break;
            default:
                if (n.Children.Count != 1)
                {
                    throw new ArgumentException("A NOT node needs exactly one operand.", nameof(n));
                }

                sb.Append("NOT ");
                PrintChild(n.Children[0], sb);
                break;
        }

        if (wrap)
        {
            sb.Append(')');
        }

        if (n.Boost != 1f)
        {
            sb.Append('^').Append(n.Boost.ToString("R", CultureInfo.InvariantCulture));
        }
    }

    private static void PrintChild(StrictNode child, StringBuilder sb)
    {
        if (child.Kind is StrictNodeKind.And or StrictNodeKind.Or && child.Boost == 1f)
        {
            sb.Append('(');
            PrintNode(child, sb);
            sb.Append(')');
        }
        else
        {
            PrintNode(child, sb);
        }
    }

    private static void AppendField(StrictNode n, StringBuilder sb)
    {
        if (n.Field is not null)
        {
            sb.Append(EscapeWord(n.Field)).Append(':');
        }
    }

    private static void PrintRegex(string pattern, StringBuilder sb)
    {
        sb.Append('/');
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '/')
            {
                sb.Append("\\/");
            }
            else if (c == '\\')
            {
                if (i + 1 >= pattern.Length || pattern[i + 1] == '/')
                {
                    throw new ArgumentException("A regular expression cannot end in a lone backslash or hold a backslash before a slash.");
                }

                sb.Append(c).Append(pattern[++i]);
            }
            else
            {
                sb.Append(c);
            }
        }

        sb.Append('/');
    }

    private static void PrintRange(StrictNode n, StringBuilder sb)
    {
        if (n.Field is null)
        {
            throw new ArgumentException("A range needs a field.", nameof(n));
        }

        sb.Append(EscapeWord(n.Field)).Append(':');
        sb.Append(n.IncludeLower ? '[' : '{');
        sb.Append(Bound(n.Lower)).Append(" TO ").Append(Bound(n.Upper));
        sb.Append(n.IncludeUpper ? ']' : '}');
    }

    private static string Bound(string? b)
    {
        if (b is null)
        {
            return "*";
        }

        bool plain = b.Length > 0 && b != "*";
        foreach (char c in b)
        {
            if (char.IsWhiteSpace(c) || "()[]{}\"^\\".Contains(c, StringComparison.Ordinal))
            {
                plain = false;
            }
        }

        if (plain)
        {
            return b;
        }

        var sb = new StringBuilder("\"");
        foreach (char c in b)
        {
            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    private static string EscapeWord(string w)
    {
        if (w.Length == 0)
        {
            throw new ArgumentException("A word or field name cannot be empty.");
        }

        var sb = new StringBuilder(w.Length + 2);
        if (w is "AND" or "OR" or "NOT")
        {
            sb.Append('\\');
        }

        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            bool special = char.IsWhiteSpace(c) || "\\()\"^~:[]{}*/".Contains(c, StringComparison.Ordinal) || (i == 0 && c is '-' or '<' or '>');
            if (special)
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private sealed class Parser
    {
        private readonly string _s;
        private int _pos;
        private int _depth;
        private int _nodes;
        private string? _groupField;
        private SourceSpan _groupFieldSpan;

        internal Parser(string s)
        {
            _s = s;
        }

        internal StrictNode ParseAll()
        {
            if (_s.Length > MaxLength)
            {
                throw Err(MaxLength, 0, $"The query is longer than the limit of {MaxLength} characters.");
            }

            SkipWs();
            if (_pos >= _s.Length)
            {
                throw Expected("a word, a quoted phrase, a field:value, NOT or '('");
            }

            StrictNode n = ParseOr();
            SkipWs();
            if (_pos < _s.Length)
            {
                throw _s[_pos] == ')' ? Err(_pos, 1, "There is a ')' with no '(' before it.", "AND, OR or the end of the query") : Expected("AND, OR or the end of the query");
            }

            return n;
        }

        private QueryParseException Err(int at, int length, string detail, string? expected = null)
            => QueryParseException.At(_s, at, length, detail, expected);

        private QueryParseException Expected(string what)
        {
            int at = Math.Min(_pos, _s.Length);
            string found = at >= _s.Length ? "the end of the query" : "'" + _s[at] + "'";
            if (at < _s.Length && (IsOp("AND") || IsOp("OR")))
            {
                string op = IsOp("AND") ? "AND" : "OR";
                return Err(at, op.Length, $"Expected {what}, but found the operator {op}. To search for the word, write \\{op}.", what);
            }

            return Err(at, at < _s.Length ? 1 : 0, $"Expected {what}, but found {found}.", what);
        }

        private void SkipWs()
        {
            while (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
            {
                _pos++;
            }
        }

        // An operator word is bare (a backslash in front makes it a word), uppercase, and followed by space, an opening parenthesis or the end.
        private bool IsOp(string op)
            => string.CompareOrdinal(_s, _pos, op, 0, op.Length) == 0
                && (_pos + op.Length >= _s.Length || char.IsWhiteSpace(_s[_pos + op.Length]) || _s[_pos + op.Length] == '(');

        private StrictNode Make(StrictNodeKind kind, int start, int end, string? field = null, SourceSpan fieldSpan = default, string text = "", int number = 0, float boost = 1f, IReadOnlyList<StrictNode>? children = null)
        {
            if (++_nodes > MaxNodes)
            {
                throw Err(start, end - start, $"The query has more than {MaxNodes} parts.");
            }

            return new StrictNode { Kind = kind, Field = field, FieldSpan = fieldSpan, Text = text, Number = number, Boost = boost, Children = children ?? [], Span = new SourceSpan(start, end) };
        }

        private StrictNode ParseOr()
        {
            int start = _pos;
            StrictNode first = ParseAnd();
            SkipWs();
            if (!(_pos < _s.Length && IsOp("OR")))
            {
                return first;
            }

            var items = new List<StrictNode> { first };
            while (_pos < _s.Length && IsOp("OR"))
            {
                _pos += 2;
                SkipWs();
                items.Add(ParseAnd());
                SkipWs();
            }

            return Make(StrictNodeKind.Or, start, _pos, children: items);
        }

        private StrictNode ParseAnd()
        {
            int start = _pos;
            StrictNode first = ParseUnary();
            var items = new List<StrictNode> { first };
            while (true)
            {
                SkipWs();
                if (_pos >= _s.Length || _s[_pos] == ')' || IsOp("OR"))
                {
                    break;
                }

                if (IsOp("AND"))
                {
                    _pos += 3;
                    SkipWs();
                }

                items.Add(ParseUnary());
            }

            return items.Count == 1 ? first : Make(StrictNodeKind.And, start, _pos, children: items);
        }

        private StrictNode ParseUnary()
        {
            SkipWs();
            int start = _pos;
            bool not = false;
            if (_pos < _s.Length && IsOp("NOT"))
            {
                _pos += 3;
                not = true;
            }
            else if (_pos < _s.Length && _s[_pos] == '-')
            {
                _pos++;
                not = true;
                if (_pos >= _s.Length || char.IsWhiteSpace(_s[_pos]))
                {
                    throw Expected("a term after '-'");
                }
            }

            if (!not)
            {
                return ParsePrimary();
            }

            if (++_depth > MaxDepth)
            {
                throw Err(start, 1, $"The query is nested deeper than the limit of {MaxDepth}.");
            }

            SkipWs();
            StrictNode inner = ParseUnary();
            _depth--;
            return Make(StrictNodeKind.Not, start, _pos, children: [inner]);
        }

        private StrictNode ParsePrimary()
        {
            SkipWs();
            if (_pos >= _s.Length)
            {
                throw Expected("a word, a quoted phrase, a field:value, NOT or '('");
            }

            if (_s[_pos] == '(')
            {
                return Suffix(ParseGroup(_groupField, _groupFieldSpan));
            }

            if (IsOp("AND") || IsOp("OR"))
            {
                throw Expected("a word, a quoted phrase, a field:value, NOT or '('");
            }

            return ParseAtom();
        }

        private StrictNode ParseGroup(string? field, SourceSpan fieldSpan)
        {
            int open = _pos;
            if (++_depth > MaxDepth)
            {
                throw Err(open, 1, $"The query is nested deeper than the limit of {MaxDepth}.");
            }

            _pos++;
            string? savedField = _groupField;
            SourceSpan savedSpan = _groupFieldSpan;
            _groupField = field;
            _groupFieldSpan = fieldSpan;
            StrictNode inner = ParseOr();
            SkipWs();
            if (_pos >= _s.Length || _s[_pos] != ')')
            {
                throw Err(Math.Min(_pos, _s.Length), _pos < _s.Length ? 1 : 0, $"Expected ')' to close the '(' at offset {open.ToString(CultureInfo.InvariantCulture)}, but found {(_pos < _s.Length ? "'" + _s[_pos] + "'" : "the end of the query")}.", "')'");
            }

            _pos++;
            _groupField = savedField;
            _groupFieldSpan = savedSpan;
            _depth--;
            return inner.With(inner.Boost, new SourceSpan(open, _pos));
        }

        private static bool IsStop(char c) => char.IsWhiteSpace(c) || "()\"^~:[]{}".Contains(c, StringComparison.Ordinal);

        // A word: any run of characters up to a space or a structural character; a backslash makes the next character literal; a star is
        // allowed only as the last character (it makes a prefix).
        private string ReadWord(out bool star)
        {
            star = false;
            var sb = new StringBuilder();
            while (_pos < _s.Length)
            {
                char c = _s[_pos];
                if (c == '\\')
                {
                    if (_pos + 1 >= _s.Length)
                    {
                        throw Err(_pos, 1, "A backslash must be followed by the character it makes literal.", "a character");
                    }

                    sb.Append(_s[_pos + 1]);
                    _pos += 2;
                    continue;
                }

                if (IsStop(c))
                {
                    break;
                }

                if (c == '*')
                {
                    if (_pos + 1 >= _s.Length || IsStop(_s[_pos + 1]))
                    {
                        star = true;
                        _pos++;
                        break;
                    }

                    throw Err(_pos, 1, "A * is only allowed at the end of a word, where it makes a prefix search. Write \\* for a literal asterisk.");
                }

                sb.Append(c);
                _pos++;
            }

            return sb.ToString();
        }

        private StrictNode ParseAtom()
        {
            int start = _pos;
            char c = _s[_pos];
            string? field = _groupField;
            SourceSpan fieldSpan = _groupFieldSpan;
            switch (c)
            {
                case '"':
                    return Suffix(ParsePhrase(start, field, fieldSpan));
                case '/':
                    return Suffix(ParseRegex(start, field, fieldSpan));
                case '[':
                case '{':
                    throw Err(start, 1, "A range needs a field in front of it, as in total:[10 TO 20].", "a field name");
            }

            string word = ReadWord(out bool star);
            if (word.Length == 0 && !star)
            {
                throw Expected("a word, a quoted phrase, a field:value, NOT or '('");
            }

            if (_pos < _s.Length && _s[_pos] == ':' && !star)
            {
                field = word;
                fieldSpan = new SourceSpan(start, _pos);
                _pos++;
                return Suffix(ParseValue(start, field, fieldSpan));
            }

            return Suffix(FinishWord(start, word, star, field, fieldSpan));
        }

        private StrictNode ParseValue(int atomStart, string field, SourceSpan fieldSpan)
        {
            if (_pos >= _s.Length || char.IsWhiteSpace(_s[_pos]))
            {
                throw Expected($"a value after '{field}:'");
            }

            int start = _pos;
            switch (_s[_pos])
            {
                case '(':
                    return ParseGroup(field, fieldSpan);
                case '"':
                    return ParsePhrase(atomStart, field, fieldSpan);
                case '/':
                    return ParseRegex(atomStart, field, fieldSpan);
                case '[':
                case '{':
                    return ParseBracketRange(atomStart, field, fieldSpan);
                case '>':
                case '<':
                    return ParseComparison(atomStart, field, fieldSpan);
            }

            string word = ReadWord(out bool star);
            if (word.Length == 0)
            {
                if (star)
                {
                    return Make(StrictNodeKind.Range, atomStart, _pos, field, fieldSpan);
                }

                _pos = start;
                throw Expected($"a value after '{field}:'");
            }

            return FinishWord(atomStart, word, star, field, fieldSpan);
        }

        private StrictNode FinishWord(int start, string word, bool star, string? field, SourceSpan fieldSpan)
        {
            if (word.Length == 0)
            {
                // A lone star: every document.
                return Make(StrictNodeKind.MatchAll, start, _pos);
            }

            if (star)
            {
                return Make(StrictNodeKind.Prefix, start, _pos, field, fieldSpan, word);
            }

            if (_pos < _s.Length && _s[_pos] == '~')
            {
                _pos++;
                int edits = 2;
                int digitsAt = _pos;
                if (ReadInt(out int value))
                {
                    edits = value;
                    if (edits > 2)
                    {
                        throw Err(digitsAt, _pos - digitsAt, "A fuzzy term allows at most 2 edits.", "0, 1 or 2");
                    }
                }

                return Make(StrictNodeKind.Fuzzy, start, _pos, field, fieldSpan, word, edits);
            }

            return Make(StrictNodeKind.Term, start, _pos, field, fieldSpan, word);
        }

        private bool ReadInt(out int value)
        {
            int begin = _pos;
            long v = 0;
            while (_pos < _s.Length && _s[_pos] is >= '0' and <= '9')
            {
                v = Math.Min((v * 10) + (_s[_pos] - '0'), int.MaxValue);
                _pos++;
            }

            value = (int)v;
            return _pos > begin;
        }

        private StrictNode ParsePhrase(int start, string? field, SourceSpan fieldSpan)
        {
            int open = _pos;
            _pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _s.Length)
                {
                    throw Err(open, _s.Length - open, "The phrase that starts here is not closed; expected a closing \".", "\"");
                }

                char c = _s[_pos];
                if (c == '"')
                {
                    _pos++;
                    break;
                }

                if (c == '\\')
                {
                    if (_pos + 1 >= _s.Length || _s[_pos + 1] is not ('"' or '\\'))
                    {
                        throw Err(_pos, Math.Min(2, _s.Length - _pos), "Unknown escape. Inside a phrase, \\\" and \\\\ are the only escapes.", "\\\" or \\\\");
                    }

                    sb.Append(_s[_pos + 1]);
                    _pos += 2;
                    continue;
                }

                sb.Append(c);
                _pos++;
            }

            int slop = 0;
            if (_pos < _s.Length && _s[_pos] == '~')
            {
                _pos++;
                int at = _pos;
                if (!ReadInt(out slop))
                {
                    throw Err(at, 0, "Expected the slop (a whole number) after '~'.", "a whole number");
                }

                if (slop > MaxSlop)
                {
                    throw Err(at, _pos - at, $"The slop is larger than the limit of {MaxSlop}.", "a whole number up to " + MaxSlop.ToString(CultureInfo.InvariantCulture));
                }
            }

            return Make(StrictNodeKind.Phrase, start, _pos, field, fieldSpan, sb.ToString(), slop);
        }

        private StrictNode ParseRegex(int start, string? field, SourceSpan fieldSpan)
        {
            int open = _pos;
            _pos++;
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _s.Length)
                {
                    throw Err(open, _s.Length - open, "The regular expression that starts here is not closed; expected a closing /.", "/");
                }

                char c = _s[_pos];
                if (c == '/')
                {
                    _pos++;
                    break;
                }

                if (c == '\\')
                {
                    if (_pos + 1 >= _s.Length)
                    {
                        throw Err(_pos, 1, "A backslash must be followed by a character.", "a character");
                    }

                    char next = _s[_pos + 1];
                    if (next == '/')
                    {
                        sb.Append('/');
                    }
                    else
                    {
                        sb.Append(c).Append(next);
                    }

                    _pos += 2;
                    continue;
                }

                sb.Append(c);
                _pos++;
            }

            return Make(StrictNodeKind.Regex, start, _pos, field, fieldSpan, sb.ToString());
        }

        private static bool IsBoundStop(char c) => char.IsWhiteSpace(c) || "()[]{}\"^".Contains(c, StringComparison.Ordinal);

        // A range bound: text in quotes, or a bare run up to a space or bracket. A bare * is an open end.
        private string? ReadBound(string what)
        {
            SkipWs();
            if (_pos >= _s.Length)
            {
                throw Expected(what);
            }

            if (_s[_pos] == '"')
            {
                _pos++;
                var q = new StringBuilder();
                int open = _pos - 1;
                while (true)
                {
                    if (_pos >= _s.Length)
                    {
                        throw Err(open, _s.Length - open, "The bound that starts here is not closed; expected a closing \".", "\"");
                    }

                    char c = _s[_pos];
                    if (c == '"')
                    {
                        _pos++;
                        return q.ToString();
                    }

                    if (c == '\\')
                    {
                        if (_pos + 1 >= _s.Length || _s[_pos + 1] is not ('"' or '\\'))
                        {
                            throw Err(_pos, Math.Min(2, _s.Length - _pos), "Unknown escape. Inside quotes, \\\" and \\\\ are the only escapes.", "\\\" or \\\\");
                        }

                        q.Append(_s[_pos + 1]);
                        _pos += 2;
                        continue;
                    }

                    q.Append(c);
                    _pos++;
                }
            }

            int begin = _pos;
            while (_pos < _s.Length && !IsBoundStop(_s[_pos]))
            {
                _pos++;
            }

            if (_pos == begin)
            {
                throw Expected(what);
            }

            string bare = _s.Substring(begin, _pos - begin);
            return bare == "*" ? null : bare;
        }

        private StrictNode ParseBracketRange(int start, string field, SourceSpan fieldSpan)
        {
            bool includeLower = _s[_pos] == '[';
            _pos++;
            string? lower = ReadBound("the lower bound of the range (or * for no bound)");
            SkipWs();
            if (!(_pos < _s.Length && string.CompareOrdinal(_s, _pos, "TO", 0, 2) == 0 && (_pos + 2 >= _s.Length || char.IsWhiteSpace(_s[_pos + 2]))))
            {
                throw Expected("TO");
            }

            _pos += 2;
            string? upper = ReadBound("the upper bound of the range (or * for no bound)");
            SkipWs();
            if (_pos >= _s.Length || _s[_pos] is not (']' or '}'))
            {
                throw Expected("']' or '}' to close the range");
            }

            bool includeUpper = _s[_pos] == ']';
            _pos++;
            return RangeNode(start, field, fieldSpan, lower, upper, includeLower, includeUpper);
        }

        private StrictNode ParseComparison(int start, string field, SourceSpan fieldSpan)
        {
            bool greater = _s[_pos] == '>';
            _pos++;
            bool inclusive = false;
            if (_pos < _s.Length && _s[_pos] == '=')
            {
                inclusive = true;
                _pos++;
            }

            if (_pos < _s.Length && char.IsWhiteSpace(_s[_pos]))
            {
                throw Expected($"a value right after '{(greater ? ">" : "<")}{(inclusive ? "=" : string.Empty)}'");
            }

            string? bound = ReadBound("a value");
            return greater
                ? RangeNode(start, field, fieldSpan, bound, null, inclusive, true)
                : RangeNode(start, field, fieldSpan, null, bound, true, inclusive);
        }

        private StrictNode RangeNode(int start, string field, SourceSpan fieldSpan, string? lower, string? upper, bool includeLower, bool includeUpper)
        {
            if (++_nodes > MaxNodes)
            {
                throw Err(start, _pos - start, $"The query has more than {MaxNodes} parts.");
            }

            return new StrictNode
            {
                Kind = StrictNodeKind.Range,
                Field = field,
                FieldSpan = fieldSpan,
                Lower = lower,
                Upper = upper,
                IncludeLower = lower is null || includeLower,
                IncludeUpper = upper is null || includeUpper,
                Span = new SourceSpan(start, _pos),
            };
        }

        // Suffix: ^boost, applied to the node just read (an atom or a group).
        private StrictNode Suffix(StrictNode node)
        {
            if (_pos >= _s.Length || _s[_pos] != '^')
            {
                return node;
            }

            _pos++;
            int at = _pos;
            while (_pos < _s.Length && (_s[_pos] is >= '0' and <= '9' or '.' or 'e' or 'E' or '-' or '+'))
            {
                _pos++;
            }

            if (_pos == at || !float.TryParse(_s.AsSpan(at, _pos - at), NumberStyles.Float, CultureInfo.InvariantCulture, out float boost) || !float.IsFinite(boost) || boost < 0f)
            {
                throw Err(at, Math.Max(0, _pos - at), "A boost is a number that is not negative, as in ^2 or ^0.5.", "a number such as 2 or 0.5");
            }

            return node.With(node.Boost * boost, new SourceSpan(node.Span.Start, _pos));
        }
    }

    private sealed class Binder
    {
        private readonly Schema _schema;
        private readonly string _source;
        private readonly UserTextCompiler _text;

        internal Binder(Schema schema, string source)
        {
            _schema = schema;
            _source = source;
            _text = new UserTextCompiler(schema);
        }

        internal Query BindRoot(StrictNode node) => Bind(node) ?? new BooleanQuery([]);

        private QueryParseException Err(SourceSpan span, string detail, string? suggestion = null)
            => QueryParseException.At(_source, span.Start, Math.Max(0, span.End - span.Start), detail, null, suggestion);

        private static Query Boosted(Query q, float boost) => boost == 1f ? q : q with { Boost = q.Boost * boost };

        private Query? Bind(StrictNode n)
        {
            switch (n.Kind)
            {
                case StrictNodeKind.MatchAll:
                    return Boosted(new MatchAllQuery(), n.Boost);
                case StrictNodeKind.And:
                    return BindAnd(n);
                case StrictNodeKind.Or:
                    return BindOr(n);
                case StrictNodeKind.Not:
                    {
                        Query? inner = Bind(n.Children[0]);
                        return inner is null ? null : Boosted(Everything(inner), n.Boost);
                    }

                case StrictNodeKind.Range:
                    return Boosted(BindRange(n), n.Boost);
            }

            if (n.Field is null)
            {
                return BindUnqualified(n);
            }

            FieldDefinition def = Field(n);
            return BindOnField(n, def);
        }

        private static BooleanQuery Everything(Query inner)
            => new([new BooleanClause(new MatchAllQuery(), Occur.Must), new BooleanClause(inner, Occur.MustNot)]);

        private Query? BindAnd(StrictNode n)
        {
            var must = new List<BooleanClause>();
            var mustNot = new List<BooleanClause>();
            foreach (StrictNode child in n.Children)
            {
                if (child.Kind == StrictNodeKind.Not && child.Boost == 1f)
                {
                    if (Bind(child.Children[0]) is { } excluded)
                    {
                        mustNot.Add(new BooleanClause(excluded, Occur.MustNot));
                    }
                }
                else if (Bind(child) is { } q)
                {
                    must.Add(new BooleanClause(q, Occur.Must));
                }
            }

            if (must.Count == 0 && mustNot.Count == 0)
            {
                return null;
            }

            if (must.Count == 0)
            {
                must.Add(new BooleanClause(new MatchAllQuery(), Occur.Must));
            }

            if (must.Count == 1 && mustNot.Count == 0)
            {
                return Boosted(must[0].Query, n.Boost);
            }

            must.AddRange(mustNot);
            return new BooleanQuery(must) { Boost = n.Boost };
        }

        private Query? BindOr(StrictNode n)
        {
            var should = new List<BooleanClause>();
            foreach (StrictNode child in n.Children)
            {
                if (Bind(child) is { } q)
                {
                    should.Add(new BooleanClause(q, Occur.Should));
                }
            }

            return should.Count switch
            {
                0 => null,
                1 => Boosted(should[0].Query, n.Boost),
                _ => new BooleanQuery(should) { MinimumShouldMatch = 1, Boost = n.Boost },
            };
        }

        private FieldDefinition Field(StrictNode n)
        {
            string name = n.Field!;
            if (!_schema.TryGetField(name, out FieldDefinition? def))
            {
                string? near = QuerySyntax.Nearest(name, _schema.Fields.Select(static f => f.Name));
                string hint = near is null ? string.Empty : $" Did you mean '{near}'?";
                string known = string.Join(", ", _schema.Fields.Where(static f => f.Indexed).Select(static f => f.Name));
                throw Err(n.FieldSpan, $"The schema has no field {QuerySyntax.Q(name)}.{hint} Searchable fields: {known}.", near);
            }

            if (!def.Indexed)
            {
                throw Err(n.FieldSpan, $"Field '{name}' is not searchable; mark it [FullText], [Keyword] or [Filterable].");
            }

            return def;
        }

        private Query? BindUnqualified(StrictNode n)
        {
            switch (n.Kind)
            {
                case StrictNodeKind.Term:
                case StrictNodeKind.Phrase:
                    {
                        Query? q = _text.CompileOperand(new UserTextOperand(n.Text, n.Kind == StrictNodeKind.Phrase));
                        if (q is not null && n.Kind == StrictNodeKind.Phrase && n.Number > 0)
                        {
                            q = AddSlop(q, n.Number);
                        }

                        return q is null ? null : Boosted(q, n.Boost);
                    }
            }

            // Prefix, fuzzy and regular expression: one query per searchable full-text field, any of which may match.
            var alternatives = new List<BooleanClause>();
            foreach (FieldDefinition f in _schema.Fields)
            {
                if (f.Type == FieldType.Text && f.Indexed && f.IndexOptions != IndexOptions.None && BindOnField(n, f) is { } q)
                {
                    alternatives.Add(new BooleanClause(q, Occur.Should));
                }
            }

            return alternatives.Count switch
            {
                0 => null,
                1 => alternatives[0].Query,
                _ => new BooleanQuery(alternatives) { MinimumShouldMatch = 1 },
            };
        }

        private static Query AddSlop(Query q, int slop) => q switch
        {
            PhraseQuery p => p with { Slop = p.Slop + slop },
            BooleanQuery b => new BooleanQuery(b.Clauses.Select(c => new BooleanClause(AddSlop(c.Query, slop), c.Occur)).ToList()) { Boost = b.Boost, MinimumShouldMatch = b.MinimumShouldMatch },
            _ => q,
        };

        private Query? BindOnField(StrictNode n, FieldDefinition def)
        {
            Query? q = n.Kind switch
            {
                StrictNodeKind.Term => BindTerm(n, def, phrase: false),
                StrictNodeKind.Phrase => BindTerm(n, def, phrase: true),
                StrictNodeKind.Prefix => BindPrefixOrFuzzy(n, def),
                StrictNodeKind.Fuzzy => BindPrefixOrFuzzy(n, def),
                _ => BindRegex(n, def),
            };
            return q is null ? null : Boosted(q, n.Boost);
        }

        private Query? BindTerm(StrictNode n, FieldDefinition def, bool phrase)
        {
            switch (def.Type)
            {
                case FieldType.Text:
                    {
                        IReadOnlyList<AnalyzedToken> tokens = Analyze(def).Analyze(n.Text);
                        if (tokens.Count == 0)
                        {
                            return null;
                        }

                        if (tokens.Count == 1)
                        {
                            return new TermQuery(def.Name, tokens[0].Term);
                        }

                        var terms = new string[tokens.Count];
                        int slop = n.Kind == StrictNodeKind.Phrase ? n.Number : 0;
                        for (int i = 0; i < tokens.Count; i++)
                        {
                            terms[i] = tokens[i].Term;
                            if (i > 0)
                            {
                                slop += Math.Max(0, tokens[i].Position - tokens[i - 1].Position - 1);
                            }
                        }

                        return new PhraseQuery(def.Name, terms) { Slop = slop };
                    }

                case FieldType.Keyword:
                    if (phrase && n.Number != 0)
                    {
                        throw Err(n.Span, $"Field '{def.Name}' is a keyword: its value is matched whole, so a phrase on it has no slop.");
                    }

                    if (n.Text.Length == 0)
                    {
                        throw Err(n.Span, $"Field '{def.Name}' is a keyword; an empty value matches nothing (empty strings are not indexed).");
                    }

                    return new TermQuery(def.Name, n.Text);

                case FieldType.Integral:
                case FieldType.Fractional:
                case FieldType.Date:
                case FieldType.Bool:
                    {
                        string? error = ValueBinder.Bind(def, LiteralKind.Bare, n.Text, BoundRole.Equal, lenient: true, out FieldValue v, out _, out bool nothing);
                        if (error is not null)
                        {
                            throw Err(n.Span, error);
                        }

                        return nothing ? new BooleanQuery([]) : new RangeQuery(def.Name, v, v);
                    }

                default:
                    throw Err(n.FieldSpan, $"Field '{def.Name}' is {def.Type} and cannot be searched by a word.");
            }
        }

        private Query? BindPrefixOrFuzzy(StrictNode n, FieldDefinition def)
        {
            string kind = n.Kind == StrictNodeKind.Prefix ? "prefix" : "fuzzy";
            string term;
            if (def.Type == FieldType.Text)
            {
                IReadOnlyList<AnalyzedToken> tokens = Analyze(def).Analyze(n.Text);
                if (tokens.Count == 0)
                {
                    return null;
                }

                if (tokens.Count > 1)
                {
                    throw Err(n.Span, $"The {kind} term {QuerySyntax.Q(n.Text)} becomes {tokens.Count} terms in field '{def.Name}'; use a single word, or a quoted phrase.");
                }

                term = tokens[0].Term;
            }
            else if (def.Type == FieldType.Keyword)
            {
                term = n.Text;
            }
            else
            {
                throw Err(n.FieldSpan, $"A {kind} search needs a full-text or keyword field; '{def.Name}' is {def.Type}.");
            }

            return n.Kind == StrictNodeKind.Prefix
                ? new PrefixQuery(def.Name, term)
                : new FuzzyQuery(def.Name, term) { MaxEdits = n.Number };
        }

        private RegexQuery? BindRegex(StrictNode n, FieldDefinition def)
        {
            if (def.Type is not (FieldType.Text or FieldType.Keyword))
            {
                throw Err(n.FieldSpan, $"A regular expression needs a full-text or keyword field; '{def.Name}' is {def.Type}.");
            }

            try
            {
                _ = new RegexAutomaton(n.Text);
            }
            catch (RegexSyntaxException e)
            {
                // The pattern was read between slashes, so position 0 is one character after the node's start (plus the field, if written).
                int offset = n.Span.End - 1 - n.Text.Length + Math.Min(e.Position, n.Text.Length);
                throw QueryParseException.At(_source, offset, 1, "Invalid regular expression: " + e.Message);
            }

            return new RegexQuery(def.Name, n.Text);
        }

        private RangeQuery BindRange(StrictNode n)
        {
            if (n.Field is null)
            {
                throw Err(n.Span, "A range needs a field.");
            }

            FieldDefinition def = Field(n);
            if (n.Lower is null && n.Upper is null)
            {
                if (def.Type == FieldType.Text)
                {
                    throw Err(n.FieldSpan, $"'{def.Name}:*' (has a value) is not available on full-text field '{def.Name}'.");
                }

                return new RangeQuery(def.Name, null, null);
            }

            if (def.Type is FieldType.Text or FieldType.Bool or FieldType.GeoPoint or FieldType.Bytes or FieldType.Vector or FieldType.Json)
            {
                throw Err(n.FieldSpan, $"A range needs a numeric, date or keyword field; '{def.Name}' is {def.Type}.");
            }

            FieldValue? lower = null;
            FieldValue? upper = null;
            bool includeLower = n.IncludeLower;
            bool includeUpper = n.IncludeUpper;
            if (n.Lower is not null)
            {
                lower = Bound(n, def, n.Lower, BoundRole.Lower, ref includeLower);
            }

            if (n.Upper is not null)
            {
                upper = Bound(n, def, n.Upper, BoundRole.Upper, ref includeUpper);
            }

            return new RangeQuery(def.Name, lower, upper) { IncludeLower = includeLower, IncludeUpper = includeUpper };
        }

        private FieldValue Bound(StrictNode n, FieldDefinition def, string text, BoundRole role, ref bool include)
        {
            string? error = ValueBinder.Bind(def, LiteralKind.Bare, text, role, lenient: true, out FieldValue v, out bool rounded, out _);
            if (error is not null)
            {
                throw Err(n.Span, error);
            }

            include |= rounded;
            return v;
        }

        private static Analyzer Analyze(FieldDefinition def)
            => AnalyzerRegistry.Create(def.Analyzer ?? FieldDefinition.DefaultAnalyzer);
    }
}
