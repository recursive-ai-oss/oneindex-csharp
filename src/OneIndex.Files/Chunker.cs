using OneIndex.Analysis.Unicode;

namespace OneIndex.Files;

/// <summary>Sizes of the passages the chunker cuts. Sizes are estimated tokens (see <see cref="Chunker.EstimateTokens"/>), not words.</summary>
public sealed class ChunkOptions
{
    /// <summary>The size a passage grows to before the next block starts a new one. Default 256.</summary>
    public int TargetTokens { get; init; } = 256;

    /// <summary>The size no passage exceeds unless a single word is larger. A block above it is cut at sentence ends (rows, for tables and code), then at word ends. Default 384.</summary>
    public int MaxTokens { get; init; } = 384;

    /// <summary>A passage smaller than this takes the next block even when that passes the target, as long as it stays under the maximum. Default 48.</summary>
    public int MinTokens { get; init; } = 48;

    internal void Validate()
    {
        if (TargetTokens < 8 || MaxTokens < TargetTokens || MinTokens < 0 || MinTokens > TargetTokens)
        {
            throw new ArgumentException($"Chunk sizes must satisfy 8 <= TargetTokens <= MaxTokens and 0 <= MinTokens <= TargetTokens; got {MinTokens}/{TargetTokens}/{MaxTokens}.");
        }
    }
}

/// <summary>A passage: a character range of one unit's text with the headings above it.</summary>
/// <param name="Start">First character.</param>
/// <param name="End">One past the last character.</param>
/// <param name="Heading">The headings in effect, outermost first, joined with <c> &gt; </c>.</param>
public readonly record struct Chunk(int Start, int End, string Heading);

/// <summary>
/// Cuts a unit into passages that follow its structure. A heading starts a new passage (a heading followed at once by a deeper heading stays
/// with it); blocks fill a passage up to the target size; a block larger than the maximum is cut at the sentence boundaries of UAX #29 (at
/// lines for tables and code), and a sentence larger than the maximum at word ends. A passage never spans units, and every passage is an
/// exact substring of the unit's text.
/// </summary>
public static class Chunker
{
    /// <summary>
    /// Estimates tokens for text without a tokenizer: four characters of Latin text make a token, two characters of other alphabetic scripts, and
    /// every CJK character or kana one. Close enough to size passages for an embedder that cuts its input at 512 tokens.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <returns>The estimate, at least 1 for non-empty text.</returns>
    public static int EstimateTokens(ReadOnlySpan<char> text)
    {
        long quarters = 0;
        foreach (char c in text)
        {
            quarters += c < 0x250 ? 1 : c < 0x2E80 ? 2 : 4;
        }

        return text.Length == 0 ? 0 : (int)Math.Max(1, (quarters + 3) / 4);
    }

    /// <summary>Cuts a unit into passages.</summary>
    /// <param name="unit">The unit.</param>
    /// <param name="options">Sizes; null for the defaults.</param>
    /// <returns>The passages in text order.</returns>
    public static List<Chunk> Cut(ExtractedUnit unit, ChunkOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        options ??= new ChunkOptions();
        options.Validate();
        return new State(unit.Text, options).Run(unit.Blocks);
    }

    private sealed class State(string text, ChunkOptions o)
    {
        private readonly List<Chunk> _chunks = [];
        private readonly List<string> _headings = [];
        private int _start = -1;
        private int _end;
        private int _cost;
        private bool _onlyHeadings;
        private string _path = string.Empty;

        public List<Chunk> Run(IReadOnlyList<TextBlock> blocks)
        {
            foreach (TextBlock b in blocks)
            {
                if (b.End <= b.Start)
                {
                    continue;
                }

                if (b.Kind == BlockKind.Heading)
                {
                    if (_start >= 0 && !_onlyHeadings)
                    {
                        Flush();
                    }

                    SetHeading(b);
                    Append(b.Start, b.End, EstimateTokens(text.AsSpan(b.Start, b.End - b.Start)), heading: true);
                    continue;
                }

                int cost = EstimateTokens(text.AsSpan(b.Start, b.End - b.Start));
                if (cost <= o.MaxTokens)
                {
                    AddPiece(b.Start, b.End, cost);
                }
                else
                {
                    foreach ((int s, int e, int c) in Split(b))
                    {
                        AddPiece(s, e, c);
                    }
                }
            }

            Flush();
            return _chunks;
        }

        private void SetHeading(TextBlock b)
        {
            int level = Math.Clamp(b.Level, 1, 6);
            while (_headings.Count > level - 1)
            {
                _headings.RemoveAt(_headings.Count - 1);
            }

            while (_headings.Count < level - 1)
            {
                _headings.Add(string.Empty);
            }

            _headings.Add(HeadingText(b));
            _path = string.Join(" > ", _headings.Where(h => h.Length > 0));
        }

        private string HeadingText(TextBlock b)
        {
            ReadOnlySpan<char> s = text.AsSpan(b.Start, b.End - b.Start);
            // Markdown markers and setext underlines are not part of the heading's words.
            int nl = s.IndexOf('\n');
            if (nl >= 0)
            {
                s = s[..nl];
            }

            s = s.Trim().TrimStart('#').Trim().TrimEnd('#').Trim();
            return s.Length > 160 ? s[..160].ToString() : s.ToString();
        }

        private void AddPiece(int start, int end, int cost)
        {
            if (_start >= 0 && !_onlyHeadings)
            {
                bool fits = _cost + cost <= o.TargetTokens;
                bool smallRoom = _cost < o.MinTokens && _cost + cost <= o.MaxTokens;
                if (!fits && !smallRoom)
                {
                    Flush();
                }
            }

            Append(start, end, cost, heading: false);
        }

        private void Append(int start, int end, int cost, bool heading)
        {
            if (_start < 0)
            {
                _start = start;
                _cost = 0;
                _onlyHeadings = true;
            }

            _end = end;
            _cost += cost;
            if (!heading)
            {
                _onlyHeadings = false;
            }
        }

        private void Flush()
        {
            if (_start >= 0)
            {
                _chunks.Add(new Chunk(_start, _end, _path));
            }

            _start = -1;
        }

        // Pieces of an oversized block, each within the target where the text allows.
        private IEnumerable<(int Start, int End, int Cost)> Split(TextBlock b)
        {
            bool byLine = b.Kind is BlockKind.Table or BlockKind.Code;
            int pieceStart = -1;
            int pieceEnd = 0;
            int pieceCost = 0;
            foreach ((int s, int e) in Atoms(b, byLine))
            {
                int c = EstimateTokens(text.AsSpan(s, e - s));
                if (c > o.MaxTokens)
                {
                    if (pieceStart >= 0)
                    {
                        yield return (pieceStart, pieceEnd, pieceCost);
                        pieceStart = -1;
                    }

                    foreach ((int ws, int we, int wc) in SplitWords(s, e))
                    {
                        yield return (ws, we, wc);
                    }

                    continue;
                }

                if (pieceStart >= 0 && pieceCost + c > o.TargetTokens)
                {
                    yield return (pieceStart, pieceEnd, pieceCost);
                    pieceStart = -1;
                }

                if (pieceStart < 0)
                {
                    pieceStart = s;
                    pieceCost = 0;
                }

                pieceEnd = e;
                pieceCost += c;
            }

            if (pieceStart >= 0)
            {
                yield return (pieceStart, pieceEnd, pieceCost);
            }
        }

        // Sentences (or lines) of a block as trimmed ranges.
        private List<(int Start, int End)> Atoms(TextBlock b, bool byLine)
        {
            var atoms = new List<(int Start, int End)>();
            ReadOnlySpan<char> all = text.AsSpan(b.Start, b.End - b.Start);
            int pos = 0;
            while (pos < all.Length)
            {
                int next;
                if (byLine)
                {
                    int nl = all[pos..].IndexOf('\n');
                    next = nl < 0 ? all.Length : pos + nl + 1;
                }
                else
                {
                    next = SentenceBreaker.Next(all, pos);
                    if (next <= pos)
                    {
                        next = all.Length;
                    }
                }

                (int s, int e) = Trim(b.Start + pos, b.Start + next);
                if (e > s)
                {
                    atoms.Add((s, e));
                }

                pos = next;
            }

            return atoms;
        }

        private (int Start, int End) Trim(int s, int e)
        {
            while (s < e && char.IsWhiteSpace(text[s]))
            {
                s++;
            }

            while (e > s && char.IsWhiteSpace(text[e - 1]))
            {
                e--;
            }

            return (s, e);
        }

        // Last resort for a sentence over the maximum (a minified file, a log line): cut at word ends, then anywhere.
        private IEnumerable<(int Start, int End, int Cost)> SplitWords(int start, int end)
        {
            int pos = start;
            while (pos < end)
            {
                int limit = pos;
                int cost = 0;
                int lastSpace = -1;
                while (limit < end)
                {
                    char c = text[limit];
                    int q = c < 0x250 ? 1 : c < 0x2E80 ? 2 : 4;
                    if (cost + q > o.TargetTokens * 4)
                    {
                        break;
                    }

                    cost += q;
                    if (char.IsWhiteSpace(c))
                    {
                        lastSpace = limit;
                    }

                    limit++;
                }

                int cut = limit;
                if (limit < end)
                {
                    if (lastSpace > pos)
                    {
                        cut = lastSpace;
                    }
                    else if (char.IsHighSurrogate(text[cut - 1]) && cut < end)
                    {
                        cut--;
                    }
                }

                (int s, int e) = Trim(pos, cut);
                if (e > s)
                {
                    yield return (s, e, EstimateTokens(text.AsSpan(s, e - s)));
                }

                pos = cut <= pos ? pos + 1 : cut;
            }
        }
    }
}
