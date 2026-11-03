using System.Text;
using RecursiveAI.OneHtml;

namespace OneIndex.Files;

/// <summary>
/// Reads HTML through onehtml. The unit's text is what a reader sees: the text of the body in document order with whitespace collapsed,
/// block elements (paragraphs, headings, list items, preformatted text, tables) as blocks separated by a blank line, a table's rows by a
/// line feed and its cells by a tab. Scripts, styles, templates, scripts' fallbacks and SVG/MathML are left out. A character range
/// therefore counts characters of this extracted text, not of the HTML source.
/// </summary>
public sealed class HtmlExtractor : IFileExtractor
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal)
    {
        "script", "style", "noscript", "template", "head", "svg", "math", "iframe", "object", "embed", "canvas", "audio", "video", "select", "datalist",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "dd", "details", "div", "dl", "dt", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "ul", "legend", "tr", "table", "html",
    };

    private readonly long _maxBytes;

    /// <summary>Creates the extractor.</summary>
    /// <param name="maxBytes">Largest file read. Default 32 MiB.</param>
    public HtmlExtractor(long maxBytes = 32L << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public string Name => "html-1";

    /// <inheritdoc />
    public bool Accepts(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return ext.Equals(".html", StringComparison.OrdinalIgnoreCase) || ext.Equals(".htm", StringComparison.OrdinalIgnoreCase) || ext.Equals(".xhtml", StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        long length = content.CanSeek ? content.Length - content.Position : 0;
        if (length > _maxBytes)
        {
            throw new InvalidDataException($"The file has {length} bytes, more than the {_maxBytes} byte limit for HTML.");
        }

        using var buffer = new MemoryStream(length > 0 ? (int)length : 4096);
        content.CopyTo(buffer);
        Document doc = Document.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), null, cancellationToken);
        var walker = new Walker();
        walker.Walk(doc.Root, cancellationToken);
        walker.Flush();
        if (walker.Blocks.Count == 0)
        {
            return new ExtractedFile("html", []);
        }

        return new ExtractedFile("html", [new ExtractedUnit(0, walker.Text.ToString(), walker.Blocks)]);
    }

    private sealed class Walker
    {
        public readonly StringBuilder Text = new();
        public readonly List<TextBlock> Blocks = [];
        private readonly StringBuilder _cur = new();
        private BlockKind _kind = BlockKind.Paragraph;
        private int _level;
        private int _preDepth;
        private int _tableDepth;
        private List<string>? _rows;
        private List<string>? _cells;

        // Depth-first walk with parent and sibling links, so a page nested ten thousand levels deep cannot overflow the stack.
        public void Walk(Node root, CancellationToken ct)
        {
            Node n = root.FirstChild;
            int visited = 0;
            while (!n.IsNull)
            {
                if ((++visited & 1023) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                bool descend = Enter(n);
                if (descend && n.HasChildNodes)
                {
                    n = n.FirstChild;
                    continue;
                }

                Exit(n);
                while (true)
                {
                    Node next = n.NextSibling;
                    if (!next.IsNull)
                    {
                        n = next;
                        break;
                    }

                    n = n.Parent;
                    if (n.IsNull || n == root)
                    {
                        n = default;
                        break;
                    }

                    Exit(n);
                }
            }
        }

        private bool Enter(Node n)
        {
            if (n.Kind == NodeKind.Text)
            {
                AppendText(n.Data);
                return false;
            }

            if (n.Kind != NodeKind.Element)
            {
                return false;
            }

            string name = n.LocalName;
            if (Skipped.Contains(name))
            {
                return false;
            }

            if (name == "br")
            {
                AppendBreak();
                return false;
            }

            if (_tableDepth > 0 || name == "table")
            {
                EnterTableContent(name);
            }
            else if (BlockElements.Contains(name))
            {
                Flush();
                SetKind(name);
            }

            if (name == "pre")
            {
                _preDepth++;
            }

            return true;
        }

        private void Exit(Node n)
        {
            if (n.Kind != NodeKind.Element)
            {
                return;
            }

            string name = n.LocalName;
            if (Skipped.Contains(name) || name == "br")
            {
                return;
            }

            if (name == "pre" && _preDepth > 0)
            {
                _preDepth--;
            }

            if (_tableDepth > 0)
            {
                ExitTableContent(name);
            }
            else if (BlockElements.Contains(name))
            {
                Flush();
            }
        }

        private void SetKind(string name)
        {
            _kind = BlockKind.Paragraph;
            _level = 0;
            if (name.Length == 2 && name[0] == 'h' && name[1] is >= '1' and <= '6')
            {
                _kind = BlockKind.Heading;
                _level = name[1] - '0';
            }
            else if (name == "li")
            {
                _kind = BlockKind.ListItem;
            }
            else if (name == "pre")
            {
                _kind = BlockKind.Code;
            }
        }

        private void EnterTableContent(string name)
        {
            switch (name)
            {
                case "table":
                    if (_tableDepth == 0)
                    {
                        Flush();
                        _rows = [];
                    }

                    _tableDepth++;
                    break;
                case "tr" when _tableDepth == 1:
                    _cur.Clear();
                    _cells = [];
                    break;
                case "td" or "th" when _tableDepth == 1:
                    _cur.Clear();
                    break;
                default:
                    if (BlockElements.Contains(name) && name != "caption" && _cur.Length > 0 && _cur[^1] != ' ')
                    {
                        _cur.Append(' ');
                    }

                    break;
            }
        }

        private void ExitTableContent(string name)
        {
            switch (name)
            {
                case "td" or "th" when _tableDepth == 1:
                    _cells?.Add(Trimmed());
                    _cur.Clear();
                    break;
                case "tr" when _tableDepth == 1:
                    if (_cells is { Count: > 0 } && _cells.Exists(c => c.Length > 0))
                    {
                        _rows!.Add(string.Join('\t', _cells));
                    }

                    _cells = null;
                    _cur.Clear();
                    break;
                case "table":
                    _tableDepth--;
                    if (_tableDepth == 0)
                    {
                        if (_rows is { Count: > 0 })
                        {
                            AddBlock(string.Join('\n', _rows), BlockKind.Table, 0);
                        }

                        _rows = null;
                        _cells = null;
                        _cur.Clear();
                        _kind = BlockKind.Paragraph;
                    }

                    break;
            }
        }

        private string Trimmed()
        {
            string s = _cur.ToString();
            return _preDepth > 0 ? s.Trim('\n', '\r') : s.Trim(' ', '\n');
        }

        private void AppendText(ReadOnlySpan<char> data)
        {
            if (_preDepth > 0)
            {
                _cur.Append(data);
                return;
            }

            foreach (char c in data)
            {
                if (char.IsWhiteSpace(c))
                {
                    if (_cur.Length > 0 && _cur[^1] is not (' ' or '\n'))
                    {
                        _cur.Append(' ');
                    }
                }
                else
                {
                    _cur.Append(c);
                }
            }
        }

        private void AppendBreak()
        {
            if (_preDepth > 0)
            {
                _cur.Append('\n');
                return;
            }

            while (_cur.Length > 0 && _cur[^1] == ' ')
            {
                _cur.Length--;
            }

            if (_cur.Length > 0 && _cur[^1] != '\n')
            {
                _cur.Append('\n');
            }
        }

        public void Flush()
        {
            if (_tableDepth > 0)
            {
                return;
            }

            string s = _kind == BlockKind.Code ? _cur.ToString().Trim('\n', '\r') : Trimmed();
            _cur.Clear();
            if (s.Length > 0)
            {
                AddBlock(s, _kind, _level);
            }
        }

        private void AddBlock(string s, BlockKind kind, int level)
        {
            if (Text.Length > 0)
            {
                Text.Append("\n\n");
            }

            int start = Text.Length;
            Text.Append(s);
            Blocks.Add(new TextBlock(start, Text.Length, kind, level));
            _kind = BlockKind.Paragraph;
            _level = 0;
        }
    }
}
