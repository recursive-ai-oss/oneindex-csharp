using System.Text;

namespace OneIndex.Files;

/// <summary>
/// Reads text files: plain text, Markdown and source code. The unit's text is the decoded file itself (a byte order mark is dropped, text that
/// is not valid UTF-8 is read as Latin-1), so a citation's character range counts UTF-16 code units of the file as decoded and Markdown
/// passages are slices of the Markdown source.
/// </summary>
public sealed class TextExtractor : IFileExtractor
{
    private static readonly HashSet<string> MarkdownExtensions = new(StringComparer.OrdinalIgnoreCase) { ".md", ".markdown", ".mdown", ".mkd" };

    private static readonly HashSet<string> CodeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csx", ".fs", ".vb", ".java", ".kt", ".scala", ".go", ".rs", ".c", ".h", ".cc", ".cpp", ".hpp", ".py", ".rb", ".js", ".mjs", ".ts", ".tsx", ".jsx",
        ".php", ".swift", ".sh", ".bash", ".ps1", ".sql", ".lua", ".pl", ".r", ".json", ".yaml", ".yml", ".toml", ".ini", ".xml", ".csproj", ".props", ".css", ".scss",
    };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".log", ".csv", ".tsv", ".rst", ".adoc", ".tex", ".org", ".nfo", ".srt", ".vtt", ".cfg", ".conf", ".properties",
    };

    private readonly long _maxBytes;

    /// <summary>Creates the extractor.</summary>
    /// <param name="maxBytes">Largest file read; a bigger one is an error document. Default 64 MiB.</param>
    public TextExtractor(long maxBytes = 64L << 20)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBytes);
        _maxBytes = maxBytes;
    }

    /// <inheritdoc />
    public string Name => "text-1";

    /// <inheritdoc />
    public bool Accepts(string fileName)
    {
        string ext = Path.GetExtension(fileName);
        return MarkdownExtensions.Contains(ext) || CodeExtensions.Contains(ext) || TextExtensions.Contains(ext);
    }

    /// <summary>Whether the bytes look like text: no NUL and no control characters in the first 8 KiB, and the file decodes.</summary>
    /// <param name="content">A seekable stream; its position is restored.</param>
    public static bool LooksLikeText(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        long position = content.Position;
        try
        {
            Span<byte> head = stackalloc byte[512]; // bounded: a fixed sniffing window
            int total = 0;
            int controls = 0;
            while (total < 8192)
            {
                int n = content.Read(head);
                if (n == 0)
                {
                    break;
                }

                total += n;
                for (int i = 0; i < n; i++)
                {
                    byte b = head[i];
                    if (b == 0)
                    {
                        return false;
                    }

                    if (b < 32 && b != '\n' && b != '\r' && b != '\t' && b != '\f' && b != 27)
                    {
                        controls++;
                    }
                }
            }

            return controls * 100 <= Math.Max(total, 1);
        }
        finally
        {
            content.Position = position;
        }
    }

    /// <inheritdoc />
    public ExtractedFile Extract(Stream content, string fileName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        string ext = Path.GetExtension(fileName);
        string text = Decode(content, _maxBytes, cancellationToken);
        bool markdown = MarkdownExtensions.Contains(ext);
        bool code = CodeExtensions.Contains(ext);
        List<TextBlock> blocks = markdown ? SplitMarkdown(text) : SplitPlain(text, code ? BlockKind.Code : BlockKind.Paragraph);
        if (text.Length == 0 || blocks.Count == 0)
        {
            return new ExtractedFile(markdown ? "markdown" : "text", []);
        }

        return new ExtractedFile(markdown ? "markdown" : "text", [new ExtractedUnit(0, text, blocks)]);
    }

    /// <summary>Decodes a file the way the extractor reads it.</summary>
    /// <param name="content">The file's bytes.</param>
    /// <param name="maxBytes">The size limit.</param>
    /// <param name="cancellationToken">Cancels reading.</param>
    /// <exception cref="InvalidDataException">The file is larger than the limit.</exception>
    public static string Decode(Stream content, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        long length = content.CanSeek ? content.Length - content.Position : 0;
        if (length > maxBytes)
        {
            throw new InvalidDataException($"The file has {length} bytes, more than the {maxBytes} byte limit for text files.");
        }

        using var buffer = new MemoryStream(length > 0 ? (int)length : 4096);
        byte[] chunk = new byte[81920];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new InvalidDataException($"The file is larger than the {maxBytes} byte limit for text files.");
            }
        }

        ReadOnlySpan<byte> bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes[3..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes[2..]);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>Paragraphs separated by blank lines.</summary>
    internal static List<TextBlock> SplitPlain(string text, BlockKind kind)
    {
        var blocks = new List<TextBlock>();
        int blockStart = -1;
        int blockEnd = -1;
        foreach ((int start, int end, int _) in Lines(text))
        {
            if (IsBlank(text, start, end))
            {
                if (blockStart >= 0)
                {
                    blocks.Add(new TextBlock(blockStart, blockEnd, kind));
                    blockStart = -1;
                }

                continue;
            }

            if (blockStart < 0)
            {
                blockStart = start;
            }

            blockEnd = end;
        }

        if (blockStart >= 0)
        {
            blocks.Add(new TextBlock(blockStart, blockEnd, kind));
        }

        return blocks;
    }

    /// <summary>
    /// Markdown blocks: ATX and setext headings, fenced code, pipe tables, list items (with their continuation lines) and paragraphs. This is the
    /// block structure of CommonMark reduced to what the chunker needs; inline syntax is left in the text.
    /// </summary>
    internal static List<TextBlock> SplitMarkdown(string text)
    {
        var blocks = new List<TextBlock>();
        int paraStart = -1;
        int paraEnd = -1;
        int tableStart = -1;
        int tableEnd = -1;
        int listStart = -1;
        int listEnd = -1;
        char fence = '\0';
        int fenceLen = 0;
        int fenceStart = -1;

        void FlushParagraph()
        {
            if (paraStart >= 0)
            {
                blocks.Add(new TextBlock(paraStart, paraEnd, BlockKind.Paragraph));
                paraStart = -1;
            }
        }

        void FlushTable()
        {
            if (tableStart >= 0)
            {
                blocks.Add(new TextBlock(tableStart, tableEnd, BlockKind.Table));
                tableStart = -1;
            }
        }

        void FlushList()
        {
            if (listStart >= 0)
            {
                blocks.Add(new TextBlock(listStart, listEnd, BlockKind.ListItem));
                listStart = -1;
            }
        }

        void FlushAll()
        {
            FlushParagraph();
            FlushTable();
            FlushList();
        }

        int lastEnd = 0;
        foreach ((int start, int end, int _) in Lines(text))
        {
            lastEnd = end;
            ReadOnlySpan<char> line = text.AsSpan(start, end - start);
            if (fence != '\0')
            {
                if (IsFenceClose(line, fence, fenceLen))
                {
                    blocks.Add(new TextBlock(fenceStart, end, BlockKind.Code));
                    fence = '\0';
                }

                continue;
            }

            if (IsFenceOpen(line, out char f, out int fl))
            {
                FlushAll();
                fence = f;
                fenceLen = fl;
                fenceStart = start;
                continue;
            }

            if (IsBlank(text, start, end))
            {
                FlushAll();
                continue;
            }

            int heading = AtxLevel(line);
            if (heading > 0)
            {
                FlushAll();
                blocks.Add(new TextBlock(start, end, BlockKind.Heading, heading));
                continue;
            }

            if (paraStart >= 0 && listStart < 0 && tableStart < 0 && SetextLevel(line) is int setext and > 0)
            {
                blocks.Add(new TextBlock(paraStart, end, BlockKind.Heading, setext));
                paraStart = -1;
                continue;
            }

            bool tableLine = IsTableLine(line);
            if (tableLine)
            {
                FlushParagraph();
                FlushList();
                if (tableStart < 0)
                {
                    tableStart = start;
                }

                tableEnd = end;
                continue;
            }

            FlushTable();
            if (IsListItem(line))
            {
                FlushParagraph();
                FlushList();
                listStart = start;
                listEnd = end;
                continue;
            }

            if (listStart >= 0 && (line[0] == ' ' || line[0] == '\t'))
            {
                listEnd = end; // a continuation line of the item
                continue;
            }

            FlushList();
            if (paraStart < 0)
            {
                paraStart = start;
            }

            paraEnd = end;
        }

        if (fence != '\0')
        {
            blocks.Add(new TextBlock(fenceStart, lastEnd, BlockKind.Code)); // an unclosed fence runs to the end of the file
        }

        FlushAll();
        return blocks;
    }

    private static bool IsBlank(string text, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static int AtxLevel(ReadOnlySpan<char> line)
    {
        int i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        int hashes = 0;
        while (i + hashes < line.Length && line[i + hashes] == '#')
        {
            hashes++;
        }

        if (hashes is < 1 or > 6)
        {
            return 0;
        }

        int after = i + hashes;
        return after == line.Length || line[after] == ' ' || line[after] == '\t' ? hashes : 0;
    }

    private static int SetextLevel(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> t = line.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        char c = t[0];
        if (c is not ('=' or '-'))
        {
            return 0;
        }

        foreach (char x in t)
        {
            if (x != c)
            {
                return 0;
            }
        }

        return c == '=' ? 1 : 2;
    }

    private static bool IsFenceOpen(ReadOnlySpan<char> line, out char fence, out int length)
    {
        ReadOnlySpan<char> t = line.TrimStart();
        fence = '\0';
        length = 0;
        if (line.Length - t.Length > 3 || t.Length < 3 || (t[0] != '`' && t[0] != '~'))
        {
            return false;
        }

        int n = 0;
        while (n < t.Length && t[n] == t[0])
        {
            n++;
        }

        if (n < 3)
        {
            return false;
        }

        fence = t[0];
        length = n;
        return true;
    }

    private static bool IsFenceClose(ReadOnlySpan<char> line, char fence, int length)
    {
        ReadOnlySpan<char> t = line.Trim();
        if (t.Length < length)
        {
            return false;
        }

        foreach (char c in t)
        {
            if (c != fence)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsTableLine(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> t = line.TrimStart();
        return t.Length > 1 && t[0] == '|';
    }

    private static bool IsListItem(ReadOnlySpan<char> line)
    {
        ReadOnlySpan<char> t = line.TrimStart();
        if (t.Length >= 2 && (t[0] is '-' or '*' or '+') && t[1] is ' ' or '\t')
        {
            return true;
        }

        int d = 0;
        while (d < t.Length && d < 9 && char.IsAsciiDigit(t[d]))
        {
            d++;
        }

        return d > 0 && d + 1 < t.Length && (t[d] is '.' or ')') && t[d + 1] is ' ' or '\t';
    }

    /// <summary>Lines of a text with the offset of their first character, the offset just before the line break, and the next line's start.</summary>
    internal static IEnumerable<(int Start, int End, int Next)> Lines(string text)
    {
        int pos = 0;
        while (pos < text.Length)
        {
            int nl = text.IndexOf('\n', pos);
            int next = nl < 0 ? text.Length : nl + 1;
            int end = nl < 0 ? text.Length : nl;
            if (end > pos && text[end - 1] == '\r')
            {
                end--;
            }

            yield return (pos, end, next);
            pos = next;
        }
    }
}
