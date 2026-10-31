using System.Globalization;

namespace OneIndex.Tools.SnowballGen;

// ----- Arithmetic expressions -----
internal abstract record Expr;
internal sealed record Num(int Value) : Expr;
internal sealed record IntVar(string Name) : Expr;
internal sealed record Special(string Which) : Expr; // cursor, limit, size, len, maxint, minint
internal sealed record SizeOf(string? Literal, string? Variable, bool Chars) : Expr;
internal sealed record Binary(char Op, Expr Left, Expr Right) : Expr;
internal sealed record Negate(Expr Operand) : Expr;

// ----- Commands -----
internal abstract record Cmd;
internal sealed record Seq(List<Cmd> Items) : Cmd;
internal sealed record OrCmd(Cmd Left, Cmd Right) : Cmd;
internal sealed record AndCmd(Cmd Left, Cmd Right) : Cmd;
internal sealed record Monadic(string Kind, Cmd Operand) : Cmd; // not try fail test do goto gopast repeat reverse backwards
internal sealed record CountedCmd(string Kind, Expr Count, Cmd Operand) : Cmd; // loop atleast
internal sealed record SetLimit(Cmd Limit, Cmd Body) : Cmd;
internal sealed record Literal(string Text) : Cmd;
internal sealed record StringTest(string Variable) : Cmd;
internal sealed record Call(string Routine) : Cmd;
internal sealed record GroupTest(string Grouping, bool Negated) : Cmd;
internal sealed record IntAssign(string Variable, string Op, Expr Value) : Cmd;
internal sealed record IntTest(Expr Left, string Op, Expr Right) : Cmd;
internal sealed record SetBool(string Variable, bool Value) : Cmd;
internal sealed record BoolTest(string Variable) : Cmd;
internal sealed record Simple(string Kind) : Cmd; // true false next [ ] delete tolimit atlimit
internal sealed record HopCmd(Expr Count) : Cmd;
internal sealed record MarkCmd(string Kind, Expr? Value, string? Variable) : Cmd; // setmark tomark atmark
internal sealed record SliceTo(string Variable) : Cmd;
internal sealed record SliceFrom(string? Literal, string? Variable) : Cmd;
internal sealed record InsertCmd(string? Literal, string? Variable, bool Attach) : Cmd;

internal sealed record Substring : Cmd
{
    internal Among? Among { get; set; }
    internal int Id { get; set; }
}

internal sealed record AmongEntryDef(string Text, string? Routine);

internal sealed record AmongGroup(List<AmongEntryDef> Entries, Cmd? Body);

internal sealed record Among(Cmd? Starter, List<AmongGroup> Groups) : Cmd
{
    internal Substring? Sub { get; set; }
}

internal sealed record RoutineDef(string Name, Cmd Body, bool Backward);

internal enum NameKind { String, Integer, Boolean, Routine, External, Grouping }

/// <summary>Parses a Snowball script into routines and groupings, following the grammar in the Snowball manual.</summary>
internal sealed class Parser
{
    private readonly List<Tok> _tokens;
    private readonly string _file;
    private int _index;
    private bool _backwardMode;
    private readonly Stack<Substring> _pendingSubstrings = new();
    private int _substringCounter;

    internal Dictionary<string, NameKind> Names { get; } = new(StringComparer.Ordinal);

    internal List<RoutineDef> Routines { get; } = [];

    internal Dictionary<string, string> Groupings { get; } = new(StringComparer.Ordinal);

    internal List<string> Externals { get; } = [];

    internal Parser(List<Tok> tokens, string file)
    {
        _tokens = tokens;
        _file = file;
    }

    private Tok Peek => _tokens[_index];

    private Tok Take() => _tokens[_index++];

    private InvalidDataException Error(string message) => new InvalidDataException($"{_file}:{Peek.Line}: {message} (at '{Peek.Text}')");

    private bool IsSymbol(string s) => Peek.Kind == TokenKind.Symbol && Peek.Text == s;

    private bool IsName(string s) => Peek.Kind == TokenKind.Name && Peek.Text == s;

    private void Expect(string symbolOrName)
    {
        if (Peek.Text != symbolOrName || Peek.Kind is TokenKind.String)
        {
            throw Error($"expected '{symbolOrName}'");
        }

        _index++;
    }

    internal void ParseProgram()
    {
        while (Peek.Kind != TokenKind.End)
        {
            ParseTopLevel();
        }
    }

    private void ParseTopLevel()
    {
        Tok t = Take();
        if (t.Kind != TokenKind.Name)
        {
            throw Error("expected a declaration or definition");
        }

        switch (t.Text)
        {
            case "strings": Declare(NameKind.String); break;
            case "integers": Declare(NameKind.Integer); break;
            case "booleans": Declare(NameKind.Boolean); break;
            case "routines": Declare(NameKind.Routine); break;
            case "externals": Declare(NameKind.External); break;
            case "groupings": Declare(NameKind.Grouping); break;
            case "backwardmode":
                Expect("(");
                _backwardMode = true;
                while (!IsSymbol(")"))
                {
                    ParseTopLevel();
                }

                _index++;
                _backwardMode = false;
                break;
            case "define":
                ParseDefine();
                break;
            default:
                throw Error($"unexpected '{t.Text}'");
        }
    }

    private void Declare(NameKind kind)
    {
        Expect("(");
        while (!IsSymbol(")"))
        {
            Tok n = Take();
            if (n.Kind != TokenKind.Name)
            {
                throw Error("expected a name");
            }

            if (kind == NameKind.External)
            {
                Names[n.Text] = NameKind.Routine;
                Externals.Add(n.Text);
            }
            else
            {
                Names[n.Text] = kind;
            }
        }

        _index++;
    }

    private void ParseDefine()
    {
        string name = Take().Text;
        if (!Names.TryGetValue(name, out NameKind kind))
        {
            throw Error($"'{name}' is not declared");
        }

        if (kind == NameKind.Grouping)
        {
            ParseGrouping(name);
            return;
        }

        Expect("as");
        _pendingSubstrings.Clear();
        Cmd body = ParseCmd();
        Routines.Add(new RoutineDef(name, body, _backwardMode));
    }

    private void ParseGrouping(string name)
    {
        HashSet<char> set = [];
        char op = '+';
        while (true)
        {
            Tok t = Take();
            HashSet<char> operand;
            if (t.Kind == TokenKind.String)
            {
                operand = [.. t.Text];
            }
            else if (t.Kind == TokenKind.Name && Groupings.TryGetValue(t.Text, out string? members))
            {
                operand = [.. members];
            }
            else
            {
                throw Error("expected a string or a defined grouping");
            }

            if (op == '+')
            {
                set.UnionWith(operand);
            }
            else
            {
                set.ExceptWith(operand);
            }

            if (IsSymbol("+") || IsSymbol("-"))
            {
                op = Take().Text[0];
                continue;
            }

            break;
        }

        Groupings[name] = new string([.. set.Order()]);
    }

    // ----- Commands -----

    private Cmd ParseCmd()
    {
        Tok t = Take();
        switch (t.Kind)
        {
            case TokenKind.String:
                return new Literal(t.Text);
            case TokenKind.Symbol:
                return ParseSymbolCmd(t.Text);
            case TokenKind.Name:
                return ParseNameCmd(t.Text);
            default:
                throw Error("expected a command");
        }
    }

    private Cmd ParseSymbolCmd(string symbol)
    {
        switch (symbol)
        {
            case "(":
                {
                    var items = new List<Cmd>();
                    while (!IsSymbol(")"))
                    {
                        Cmd left = ParseCmd();
                        while (IsName("and") || IsName("or"))
                        {
                            bool isAnd = Take().Text == "and";
                            Cmd right = ParseCmd();
                            left = isAnd ? new AndCmd(left, right) : new OrCmd(left, right);
                        }

                        items.Add(left);
                    }

                    _index++;
                    return new Seq(items);
                }

            case "$": return ParseDollar();
            case "[": return new Simple("[");
            case "]": return new Simple("]");
            case "?": return new Seq([]);
            case "->":
                return new SliceTo(NameOf(NameKind.String));
            case "<-":
                {
                    (string? lit, string? v) = StringOperand();
                    return new SliceFrom(lit, v);
                }

            case "<+":
                {
                    (string? lit, string? v) = StringOperand();
                    return new InsertCmd(lit, v, Attach: false);
                }

            default:
                throw Error($"unexpected symbol '{symbol}'");
        }
    }

    private string NameOf(NameKind kind)
    {
        Tok t = Take();
        if (t.Kind != TokenKind.Name || !Names.TryGetValue(t.Text, out NameKind k) || k != kind)
        {
            throw Error($"expected a {kind} name");
        }

        return t.Text;
    }

    private (string? Literal, string? Variable) StringOperand()
    {
        Tok t = Take();
        if (t.Kind == TokenKind.String)
        {
            return (t.Text, null);
        }

        if (t.Kind == TokenKind.Name && Names.TryGetValue(t.Text, out NameKind k) && k == NameKind.String)
        {
            return (null, t.Text);
        }

        throw Error("expected a string or a string name");
    }

    private Cmd ParseNameCmd(string name)
    {
        switch (name)
        {
            case "not" or "try" or "fail" or "test" or "do" or "goto" or "gopast" or "repeat" or "reverse" or "backwards":
                return new Monadic(name, ParseCmd());
            case "loop" or "atleast":
                {
                    Expr count = ParseExpr();
                    return new CountedCmd(name, count, ParseCmd());
                }

            case "hop": return new HopCmd(ParseExpr());
            case "next": return new Simple("next");
            case "true": return new Simple("true");
            case "false": return new Simple("false");
            case "delete": return new Simple("delete");
            case "tolimit": return new Simple("tolimit");
            case "atlimit": return new Simple("atlimit");
            case "insert":
                {
                    (string? lit, string? v) = StringOperand();
                    return new InsertCmd(lit, v, Attach: false);
                }

            case "attach":
                {
                    (string? lit, string? v) = StringOperand();
                    return new InsertCmd(lit, v, Attach: true);
                }

            case "setmark": return new MarkCmd("setmark", null, NameOf(NameKind.Integer));
            case "tomark": return new MarkCmd("tomark", ParseExpr(), null);
            case "atmark": return new MarkCmd("atmark", ParseExpr(), null);
            case "set": return new SetBool(NameOf(NameKind.Boolean), true);
            case "unset": return new SetBool(NameOf(NameKind.Boolean), false);
            case "setlimit":
                {
                    Cmd limit = ParseCmd();
                    Expect("for");
                    return new SetLimit(limit, ParseCmd());
                }

            case "non":
                {
                    if (IsSymbol("-"))
                    {
                        _index++;
                    }

                    return new GroupTest(NameOf(NameKind.Grouping), Negated: true);
                }

            case "substring":
                {
                    var sub = new Substring { Id = ++_substringCounter };
                    _pendingSubstrings.Push(sub);
                    return sub;
                }

            case "among":
                return ParseAmong();
            default:
                if (!Names.TryGetValue(name, out NameKind kind))
                {
                    throw Error($"unknown name '{name}'");
                }

                return kind switch
                {
                    NameKind.Routine => new Call(name),
                    NameKind.Grouping => new GroupTest(name, Negated: false),
                    NameKind.Boolean => new BoolTest(name),
                    NameKind.String => new StringTest(name),
                    _ => throw Error($"'{name}' cannot be used as a command"),
                };
        }
    }

    private Cmd ParseAmong()
    {
        Substring? sub = _pendingSubstrings.Count > 0 ? _pendingSubstrings.Pop() : null;
        Expect("(");
        Cmd? starter = null;
        var groups = new List<AmongGroup>();
        var current = new List<AmongEntryDef>();
        while (!IsSymbol(")"))
        {
            Tok t = Peek;
            if (t.Kind == TokenKind.String)
            {
                _index++;
                string? routine = null;
                if (Peek.Kind == TokenKind.Name && Names.TryGetValue(Peek.Text, out NameKind k) && k == NameKind.Routine)
                {
                    routine = Take().Text;
                }

                current.Add(new AmongEntryDef(t.Text, routine));
            }
            else if (IsSymbol("("))
            {
                Cmd body = ParseCmd();
                if (current.Count == 0)
                {
                    starter = body;
                }
                else
                {
                    groups.Add(new AmongGroup(current, body));
                    current = [];
                }
            }
            else
            {
                throw Error("unexpected token in among");
            }
        }

        _index++;
        if (current.Count > 0)
        {
            groups.Add(new AmongGroup(current, null));
        }

        var among = new Among(starter, groups);
        if (sub is null)
        {
            sub = new Substring { Id = ++_substringCounter };
            sub.Among = among;
            among.Sub = sub;
            return new Seq([sub, among]);
        }

        sub.Among = among;
        among.Sub = sub;
        return among;
    }

    private Cmd ParseDollar()
    {
        if (IsSymbol("("))
        {
            _index++;
            Expr left = ParseExpr();
            string op = Take().Text;
            Expr right = ParseExpr();
            Expect(")");
            return new IntTest(left, op, right);
        }

        Tok n = Take();
        if (n.Kind != TokenKind.Name || !Names.TryGetValue(n.Text, out NameKind kind))
        {
            throw Error("expected a name after $");
        }

        if (kind != NameKind.Integer)
        {
            throw Error("string commands ($s C) are not supported");
        }

        string op2 = Take().Text;
        Expr value = ParseExpr();
        return op2 is "=" or "+=" or "-=" or "*=" or "/=" ? new IntAssign(n.Text, op2, value) : new IntTest(new IntVar(n.Text), op2, value);
    }

    // ----- Arithmetic expressions: + - over * /, unary minus, parentheses -----

    private Expr ParseExpr()
    {
        Expr left = ParseTerm();
        while (IsSymbol("+") || IsSymbol("-"))
        {
            char op = Take().Text[0];
            left = new Binary(op, left, ParseTerm());
        }

        return left;
    }

    private Expr ParseTerm()
    {
        Expr left = ParseFactor();
        while (IsSymbol("*") || IsSymbol("/"))
        {
            char op = Take().Text[0];
            left = new Binary(op, left, ParseFactor());
        }

        return left;
    }

    private Expr ParseFactor()
    {
        Tok t = Take();
        if (t.Kind == TokenKind.Number)
        {
            return new Num(int.Parse(t.Text, CultureInfo.InvariantCulture));
        }

        if (t.Kind == TokenKind.Symbol && t.Text == "-")
        {
            return new Negate(ParseFactor());
        }

        if (t.Kind == TokenKind.Symbol && t.Text == "(")
        {
            Expr inner = ParseExpr();
            Expect(")");
            return inner;
        }

        if (t.Kind == TokenKind.Name)
        {
            switch (t.Text)
            {
                case "cursor" or "limit" or "size" or "len" or "maxint" or "minint":
                    return new Special(t.Text);
                case "sizeof" or "lenof":
                    {
                        (string? lit, string? v) = StringOperand();
                        return new SizeOf(lit, v, t.Text == "lenof");
                    }

                default:
                    if (Names.TryGetValue(t.Text, out NameKind k) && k == NameKind.Integer)
                    {
                        return new IntVar(t.Text);
                    }

                    break;
            }
        }

        throw Error($"bad arithmetic expression at '{t.Text}'");
    }
}
