using System.Globalization;
using System.Text;

namespace OneIndex.Codecs;

/// <summary>The regular expression of a <see cref="RegexAutomaton"/> is malformed or too large.</summary>
public sealed class RegexSyntaxException : FormatException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What is wrong.</param>
    /// <param name="position">The UTF-16 index in the pattern where it was noticed.</param>
    public RegexSyntaxException(string message, int position)
        : base($"{message} (at position {position})") => Position = position;

    /// <summary>The UTF-16 index in the pattern where the problem was noticed.</summary>
    public int Position { get; }
}

/// <summary>
/// A regular expression over the code points of a term, compiled to a Thompson non-deterministic automaton (Thompson 1968) whose
/// subset construction is done lazily while a term dictionary is walked. The expression always matches a whole term.
/// Syntax: literals; <c>.</c> (any code point); classes <c>[abc]</c>, <c>[a-z]</c>, <c>[^...]</c>; groups <c>(...)</c>;
/// alternation <c>|</c>; repetition <c>*</c>, <c>+</c>, <c>?</c>, <c>{n}</c>, <c>{n,}</c>, <c>{n,m}</c>; escapes <c>\d \D \w \W \s \S</c>
/// (ASCII sets: digits, letters digits and underscore, and space, tab, line feed, carriage return, form feed, vertical tab),
/// <c>\n \r \t \f \v</c>, <c>\uXXXX</c> and <c>\U{X...}</c> (a code point in hex), and a backslash before any other punctuation
/// for that character. <c>^</c> and <c>$</c> are errors (the match is already anchored); lookaround and back-references are not supported. The pattern is limited to 10,000 automaton states and a repeat bound of 1,000.
/// An instance keeps scratch space, so use one per search and never share it between threads.
/// </summary>
public sealed class RegexAutomaton : TermAutomaton
{
    private const int MaxStates = 10_000;
    private const int MaxRepeat = 1_000;
    private const int MaxCodePoint = 0x10FFFF;

    private readonly List<NfaState> _states = [];
    private readonly int _start;
    private readonly int _match;
    private readonly int[] _mark;
    private int _generation;
    private readonly int[] _stack;

    /// <summary>Compiles a pattern.</summary>
    /// <param name="pattern">The expression, see the type remarks for the syntax.</param>
    /// <exception cref="RegexSyntaxException">The pattern is malformed or exceeds the limits.</exception>
    public RegexAutomaton(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        Node ast = new Parser(pattern).Parse();
        _states.Add(default); // state 0 is the match state
        _match = 0;
        _start = Build(ast, _match);
        _mark = new int[_states.Count];
        _stack = new int[(2 * _states.Count) + 2];
    }

    /// <inheritdoc/>
    public override int MaxStateLength => _states.Count;

    /// <inheritdoc/>
    public override int Start(Span<int> state) => Closure(_start, state, 0);

    /// <inheritdoc/>
    public override int Transition(ReadOnlySpan<int> state, int codePoint, Span<int> destination)
    {
        _generation++;
        int n = 0;
        foreach (int id in state)
        {
            NfaState s = _states[id];
            if (s.Ranges is not null && InRanges(s.Ranges, codePoint))
            {
                n = ClosureMarked(s.Out1, destination, n);
            }
        }

        return n;
    }

    /// <inheritdoc/>
    public override bool IsAccepting(ReadOnlySpan<int> state)
    {
        foreach (int id in state)
        {
            if (id == _match)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InRanges(int[] ranges, int cp)
    {
        int lo = 0;
        int hi = (ranges.Length / 2) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (cp < ranges[2 * mid])
            {
                hi = mid - 1;
            }
            else if (cp > ranges[(2 * mid) + 1])
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }

        return false;
    }

    private int Closure(int from, Span<int> dest, int n)
    {
        _generation++;
        return ClosureMarked(from, dest, n);
    }

    // Epsilon closure: adds the consuming states and the match state reachable from 'from' that are not yet marked in this generation.
    private int ClosureMarked(int from, Span<int> dest, int n)
    {
        int sp = 0;
        _stack[sp++] = from;
        while (sp > 0)
        {
            int id = _stack[--sp];
            if (_mark[id] == _generation)
            {
                continue;
            }

            _mark[id] = _generation;
            NfaState s = _states[id];
            if (s.Ranges is not null || id == _match)
            {
                dest[n++] = id;
            }
            else
            {
                // A split state: both branches. Out2 is pushed first so Out1 is explored first.
                if (s.Out2 >= 0)
                {
                    _stack[sp++] = s.Out2;
                }

                _stack[sp++] = s.Out1;
            }
        }

        return n;
    }

    private int NewState(NfaState s, int position)
    {
        if (_states.Count >= MaxStates)
        {
            throw new RegexSyntaxException($"the pattern needs more than {MaxStates} automaton states", position);
        }

        _states.Add(s);
        return _states.Count - 1;
    }

    // Compiles backwards: Build returns the entry state of 'node' given the state to continue at.
    private int Build(Node node, int next)
    {
        switch (node)
        {
            case EmptyNode:
                return next;
            case SetNode set:
                return NewState(new NfaState(set.Ranges, next, -1), 0);
            case ConcatNode concat:
                {
                    int cur = next;
                    for (int i = concat.Items.Count - 1; i >= 0; i--)
                    {
                        cur = Build(concat.Items[i], cur);
                    }

                    return cur;
                }

            case AltNode alt:
                {
                    int cur = Build(alt.Items[^1], next);
                    for (int i = alt.Items.Count - 2; i >= 0; i--)
                    {
                        cur = NewState(new NfaState(null, Build(alt.Items[i], next), cur), 0);
                    }

                    return cur;
                }

            case RepeatNode rep:
                {
                    int cur = next;
                    if (rep.Max < 0)
                    {
                        // x{n,}: a loop, then n mandatory copies in front of it.
                        int loop = NewState(new NfaState(null, -1, next), 0);
                        int body = Build(rep.Inner, loop);
                        _states[loop] = new NfaState(null, body, next);
                        cur = loop;
                    }
                    else
                    {
                        for (int i = 0; i < rep.Max - rep.Min; i++)
                        {
                            cur = NewState(new NfaState(null, Build(rep.Inner, cur), next), 0);
                        }
                    }

                    for (int i = 0; i < rep.Min; i++)
                    {
                        cur = Build(rep.Inner, cur);
                    }

                    return cur;
                }

            default:
                throw new InvalidOperationException("unknown node");
        }
    }

    // A consuming state has Ranges (sorted, disjoint [lo, hi] pairs) and one successor in Out1; a split state has no Ranges and
    // two successors (Out2 is -1 for none); state 0 is the match state.
    private readonly record struct NfaState(int[]? Ranges, int Out1, int Out2);

    private abstract record Node;

    private sealed record EmptyNode : Node;

    private sealed record SetNode(int[] Ranges) : Node;

    private sealed record ConcatNode(List<Node> Items) : Node;

    private sealed record AltNode(List<Node> Items) : Node;

    private sealed record RepeatNode(Node Inner, int Min, int Max) : Node;

    private sealed class Parser(string pattern)
    {
        private int _pos;
        private int _depth;
        private int _work;

        public Node Parse()
        {
            Node n = ParseAlternation();
            if (_pos < pattern.Length)
            {
                throw new RegexSyntaxException("unbalanced ')'", _pos);
            }

            return n;
        }

        private Node ParseAlternation()
        {
            if (++_depth > 200)
            {
                throw new RegexSyntaxException("groups are nested too deeply", _pos);
            }

            var items = new List<Node> { ParseConcat() };
            while (_pos < pattern.Length && pattern[_pos] == '|')
            {
                _pos++;
                items.Add(ParseConcat());
            }

            _depth--;
            return items.Count == 1 ? items[0] : new AltNode(items);
        }

        private Node ParseConcat()
        {
            var items = new List<Node>();
            while (_pos < pattern.Length && pattern[_pos] is not ('|' or ')'))
            {
                items.Add(ParseRepeat());
            }

            return items.Count switch
            {
                0 => new EmptyNode(),
                1 => items[0],
                _ => new ConcatNode(items),
            };
        }

        private Node ParseRepeat()
        {
            Node atom = ParseAtom();
            while (_pos < pattern.Length)
            {
                char c = pattern[_pos];
                int min;
                int max;
                if (c == '*')
                {
                    min = 0;
                    max = -1;
                    _pos++;
                }
                else if (c == '+')
                {
                    min = 1;
                    max = -1;
                    _pos++;
                }
                else if (c == '?')
                {
                    min = 0;
                    max = 1;
                    _pos++;
                }
                else if (c == '{')
                {
                    (min, max) = ParseBounds();
                }
                else
                {
                    break;
                }

                _work += Math.Max(1, max < 0 ? min + 1 : max);
                if (_work > MaxStates)
                {
                    throw new RegexSyntaxException("the repetition makes the pattern too large", _pos);
                }

                atom = new RepeatNode(atom, min, max);
            }

            return atom;
        }

        private (int Min, int Max) ParseBounds()
        {
            int open = _pos;
            _pos++;
            int min = ParseNumber();
            int max = min;
            if (_pos < pattern.Length && pattern[_pos] == ',')
            {
                _pos++;
                max = _pos < pattern.Length && pattern[_pos] == '}' ? -1 : ParseNumber();
            }

            if (_pos >= pattern.Length || pattern[_pos] != '}')
            {
                throw new RegexSyntaxException("'{' needs a closing '}'", open);
            }

            _pos++;
            if (max >= 0 && max < min)
            {
                throw new RegexSyntaxException("the repeat maximum is below its minimum", open);
            }

            return (min, max);
        }

        private int ParseNumber()
        {
            int start = _pos;
            long v = 0;
            while (_pos < pattern.Length && pattern[_pos] is >= '0' and <= '9')
            {
                v = (v * 10) + (pattern[_pos] - '0');
                if (v > MaxRepeat)
                {
                    throw new RegexSyntaxException($"a repeat bound is above {MaxRepeat}", start);
                }

                _pos++;
            }

            if (_pos == start)
            {
                throw new RegexSyntaxException("a number is expected", start);
            }

            return (int)v;
        }

        private Node ParseAtom()
        {
            char c = pattern[_pos];
            switch (c)
            {
                case '(':
                    {
                        int open = _pos++;
                        Node inner = ParseAlternation();
                        if (_pos >= pattern.Length || pattern[_pos] != ')')
                        {
                            throw new RegexSyntaxException("'(' needs a closing ')'", open);
                        }

                        _pos++;
                        return inner;
                    }

                case '[':
                    return new SetNode(ParseClass());
                case '.':
                    _pos++;
                    return new SetNode([0, MaxCodePoint]);
                case '\\':
                    return ParseEscape();
                case '*' or '+' or '?' or '{':
                    throw new RegexSyntaxException($"'{c}' has nothing to repeat", _pos);
                case '^' or '$':
                    throw new RegexSyntaxException("anchors are not supported: the pattern always matches a whole term", _pos);
                default:
                    {
                        int cp = ReadCodePoint();
                        return new SetNode([cp, cp]);
                    }
            }
        }

        private int ReadCodePoint()
        {
            if (Rune.TryGetRuneAt(pattern, _pos, out Rune r))
            {
                _pos += r.Utf16SequenceLength;
                return r.Value;
            }

            _pos++;
            return 0xFFFD;
        }

        private SetNode ParseEscape()
        {
            int start = _pos;
            _pos++;
            if (_pos >= pattern.Length)
            {
                throw new RegexSyntaxException("a backslash needs a character after it", start);
            }

            char c = pattern[_pos];
            if (TryClassEscape(c, out int[]? ranges))
            {
                _pos++;
                return new SetNode(ranges);
            }

            int cp = ReadEscapedCodePoint(start);
            return new SetNode([cp, cp]);
        }

        // Reads the character after a backslash (the backslash is consumed, _pos is on the character).
        private int ReadEscapedCodePoint(int start)
        {
            char c = pattern[_pos];
            switch (c)
            {
                case 'n':
                    _pos++;
                    return '\n';
                case 'r':
                    _pos++;
                    return '\r';
                case 't':
                    _pos++;
                    return '\t';
                case 'f':
                    _pos++;
                    return '\f';
                case 'v':
                    _pos++;
                    return '\v';
                case 'u':
                    {
                        if (_pos + 5 > pattern.Length
                            || !int.TryParse(pattern.AsSpan(_pos + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v))
                        {
                            throw new RegexSyntaxException("\\u needs four hex digits", start);
                        }

                        _pos += 5;
                        return v;
                    }

                case 'U':
                    {
                        int close = _pos + 1 < pattern.Length && pattern[_pos + 1] == '{' ? pattern.IndexOf('}', _pos) : -1;
                        if (close < 0
                            || !int.TryParse(pattern.AsSpan(_pos + 2, close - _pos - 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int v)
                            || v > MaxCodePoint)
                        {
                            throw new RegexSyntaxException("\\U needs a code point in braces, like \\U{1F600}", start);
                        }

                        _pos = close + 1;
                        return v;
                    }

                default:
                    if (char.IsAsciiLetterOrDigit(c))
                    {
                        throw new RegexSyntaxException($"unknown escape '\\{c}'", start);
                    }

                    return ReadCodePoint();
            }
        }

        private static bool TryClassEscape(char c, out int[] ranges)
        {
            switch (c)
            {
                case 'd':
                    ranges = [.. Digit];
                    return true;
                case 'D':
                    ranges = Negate(Digit);
                    return true;
                case 'w':
                    ranges = [.. Word];
                    return true;
                case 'W':
                    ranges = Negate(Word);
                    return true;
                case 's':
                    ranges = [.. Space];
                    return true;
                case 'S':
                    ranges = Negate(Space);
                    return true;
                default:
                    ranges = [];
                    return false;
            }
        }

        private static readonly int[] Digit = ['0', '9'];
        private static readonly int[] Word = ['0', '9', 'A', 'Z', '_', '_', 'a', 'z'];
        private static readonly int[] Space = ['\t', '\r', ' ', ' '];

        private int[] ParseClass()
        {
            int open = _pos++;
            bool negate = false;
            if (_pos < pattern.Length && pattern[_pos] == '^')
            {
                negate = true;
                _pos++;
            }

            var pairs = new List<(int Lo, int Hi)>();
            bool first = true;
            while (true)
            {
                if (_pos >= pattern.Length)
                {
                    throw new RegexSyntaxException("'[' needs a closing ']'", open);
                }

                if (pattern[_pos] == ']' && !first)
                {
                    _pos++;
                    break;
                }

                first = false;
                if (pattern[_pos] == '\\' && _pos + 1 < pattern.Length && TryClassEscape(pattern[_pos + 1], out int[]? set))
                {
                    _pos += 2;
                    for (int i = 0; i < set.Length; i += 2)
                    {
                        pairs.Add((set[i], set[i + 1]));
                    }

                    continue;
                }

                int lo = ReadClassMember();
                int hi = lo;
                if (_pos + 1 < pattern.Length && pattern[_pos] == '-' && pattern[_pos + 1] != ']')
                {
                    int dash = _pos;
                    _pos++;
                    hi = ReadClassMember();
                    if (hi < lo)
                    {
                        throw new RegexSyntaxException("the range is backwards", dash);
                    }
                }

                pairs.Add((lo, hi));
            }

            pairs.Sort(static (a, b) => a.Lo.CompareTo(b.Lo));
            var merged = new List<int>();
            foreach ((int lo, int hi) in pairs)
            {
                if (merged.Count > 0 && lo <= merged[^1] + 1)
                {
                    merged[^1] = Math.Max(merged[^1], hi);
                }
                else
                {
                    merged.Add(lo);
                    merged.Add(hi);
                }
            }

            int[] result = [.. merged];
            return negate ? Negate(result) : result;
        }

        private int ReadClassMember()
        {
            if (pattern[_pos] == '\\')
            {
                int start = _pos++;
                if (_pos >= pattern.Length)
                {
                    throw new RegexSyntaxException("a backslash needs a character after it", start);
                }

                return ReadEscapedCodePoint(start);
            }

            return ReadCodePoint();
        }

        private static int[] Negate(int[] ranges)
        {
            var result = new List<int>();
            int next = 0;
            for (int i = 0; i < ranges.Length; i += 2)
            {
                if (ranges[i] > next)
                {
                    result.Add(next);
                    result.Add(ranges[i] - 1);
                }

                next = ranges[i + 1] + 1;
            }

            if (next <= MaxCodePoint)
            {
                result.Add(next);
                result.Add(MaxCodePoint);
            }

            return [.. result];
        }
    }
}
