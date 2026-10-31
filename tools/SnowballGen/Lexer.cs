using System.Globalization;
using System.Text;

namespace OneIndex.Tools.SnowballGen;

internal enum TokenKind { Name, Number, String, Symbol, End }

internal readonly record struct Tok(TokenKind Kind, string Text, int Line);

/// <summary>
/// Turns a Snowball script into tokens, following the lexical rules of the Snowball manual: names, numbers, quoted strings,
/// the symbols, both comment styles, and the <c>stringescapes</c> and <c>stringdef</c> directives, which the lexer applies itself so
/// that string tokens come out with their escapes already expanded.
/// </summary>
internal sealed class Lexer
{
    private static readonly string[] TwoCharSymbols = ["->", "<-", "=>", "<+", "+=", "-=", "*=", "/=", "==", "!=", ">=", "<="];
    private readonly string _src;
    private readonly string _file;
    private int _pos;
    private int _line = 1;
    private char _open;
    private char _close;
    private readonly Dictionary<string, string> _macros = new(StringComparer.Ordinal);

    internal Lexer(string source, string file)
    {
        _src = source;
        _file = file;
    }

    internal List<Tok> Tokenize()
    {
        var list = new List<Tok>();
        while (true)
        {
            Tok t = Next();
            list.Add(t);
            if (t.Kind == TokenKind.End)
            {
                return list;
            }
        }
    }

    private InvalidDataException Error(string message) => new InvalidDataException($"{_file}:{_line}: {message}");

    private void SkipSpace()
    {
        while (_pos < _src.Length)
        {
            char c = _src[_pos];
            if (c == '\n')
            {
                _line++;
                _pos++;
            }
            else if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/')
            {
                while (_pos < _src.Length && _src[_pos] != '\n')
                {
                    _pos++;
                }
            }
            else if (c == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '*')
            {
                _pos += 2;
                while (_pos + 1 < _src.Length && !(_src[_pos] == '*' && _src[_pos + 1] == '/'))
                {
                    if (_src[_pos] == '\n')
                    {
                        _line++;
                    }

                    _pos++;
                }

                _pos += 2;
            }
            else
            {
                return;
            }
        }
    }

    private Tok Next()
    {
        while (true)
        {
            SkipSpace();
            if (_pos >= _src.Length)
            {
                return new Tok(TokenKind.End, string.Empty, _line);
            }

            char c = _src[_pos];
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z'))
            {
                int start = _pos;
                while (_pos < _src.Length && (char.IsAsciiLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
                {
                    _pos++;
                }

                string name = _src[start.._pos];
                if (name == "stringescapes")
                {
                    SkipSpace();
                    _open = _src[_pos++];
                    SkipSpace();
                    _close = _src[_pos++];
                    continue;
                }

                if (name == "stringdef")
                {
                    ReadStringDef();
                    continue;
                }

                if (name == "get")
                {
                    throw Error("the get directive is not supported");
                }

                return new Tok(TokenKind.Name, name, _line);
            }

            if (char.IsAsciiDigit(c))
            {
                int start = _pos;
                while (_pos < _src.Length && char.IsAsciiDigit(_src[_pos]))
                {
                    _pos++;
                }

                return new Tok(TokenKind.Number, _src[start.._pos], _line);
            }

            if (c == '\'')
            {
                _pos++;
                return new Tok(TokenKind.String, ReadLiteral(), _line);
            }

            foreach (string two in TwoCharSymbols)
            {
                if (string.CompareOrdinal(_src, _pos, two, 0, 2) == 0)
                {
                    _pos += 2;
                    return new Tok(TokenKind.Symbol, two, _line);
                }
            }

            _pos++;
            return new Tok(TokenKind.Symbol, c.ToString(), _line);
        }
    }

    private void ReadStringDef()
    {
        SkipSpace();
        int start = _pos;
        while (_pos < _src.Length && !char.IsWhiteSpace(_src[_pos]))
        {
            _pos++;
        }

        string macro = _src[start.._pos];
        SkipSpace();
        string? radix = null;
        if (_src[_pos] != '\'')
        {
            int s = _pos;
            while (!char.IsWhiteSpace(_src[_pos]))
            {
                _pos++;
            }

            radix = _src[s.._pos];
            SkipSpace();
        }

        if (_src[_pos] != '\'')
        {
            throw Error("stringdef needs a quoted string");
        }

        _pos++;
        string raw = ReadLiteral();
        if (radix is null)
        {
            _macros[macro] = raw;
            return;
        }

        var sb = new StringBuilder();
        foreach (string part in raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            sb.Append(char.ConvertFromUtf32(int.Parse(part, radix == "hex" ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture)));
        }

        _macros[macro] = sb.ToString();
    }

    // Reads up to the closing quote, expanding {...} escapes. The opening quote has been consumed.
    private string ReadLiteral()
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (_pos >= _src.Length)
            {
                throw Error("unterminated string");
            }

            char c = _src[_pos++];
            if (c == '\n')
            {
                _line++;
            }

            if (c == '\'')
            {
                return sb.ToString();
            }

            if (_open != '\0' && c == _open)
            {
                int end = _src.IndexOf(_close, _pos);
                if (end < 0)
                {
                    throw Error("unterminated escape");
                }

                string content = _src[_pos..end];
                _line += content.Count(ch => ch == '\n');
                _pos = end + 1;
                if (_macros.TryGetValue(content, out string? value))
                {
                    sb.Append(value);
                }
                else if (content.StartsWith("U+", StringComparison.Ordinal))
                {
                    sb.Append(char.ConvertFromUtf32(int.Parse(content[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }
                else if (content == "'" || content.Length == 1 && content[0] == _open)
                {
                    sb.Append(content);
                }
                else if (content.Trim().Length == 0 && content.Contains('\n', StringComparison.Ordinal))
                {
                    // {W}: white space with a newline is ignored, so long strings can span lines.
                }
                else
                {
                    throw Error($"unknown string escape {{{content}}}");
                }

                continue;
            }

            sb.Append(c);
        }
    }
}
