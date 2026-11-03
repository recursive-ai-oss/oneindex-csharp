using System.Globalization;
using System.Text;

namespace OneIndex.Models.Tokenization;

/// <summary>
/// Turns text into SentencePiece unigram pieces (Kudo 2018, "Subword regularization", section 3: the segmentation with the highest total
/// piece log-probability, found by the Viterbi algorithm over the lattice of all pieces) after the normalization the model names.
/// <para>
/// Normalization for <c>nmt_nfkc</c> models is a table of rules, applied longest match first at each position: single characters
/// (control characters and zero-width characters become a space or vanish, compatibility characters take their NFKC form) and short
/// sequences (a base letter and its accent become the composed letter, half-width kana and its voicing mark, Hangul jamo), then the
/// model's whitespace rules: runs of spaces collapse and the ends are trimmed, a space is put in front, and spaces are written as
/// U+2581. The rules were observed from the released SentencePiece normalizer (<c>tools/models/spm_normalizer_dump.py</c>); the Hangul
/// jamo composition is the algorithm of the Unicode standard; <c>identity</c> models skip all of it.
/// Characters no piece covers become one unknown piece per run. Instances are thread-safe.
/// </para>
/// </summary>
public sealed class SentencePieceTokenizer
{
    private const char Escaped = '\u2581';
    private const float UnknownPenalty = 10f;

    private readonly SentencePieceModel _model;
    private readonly Dictionary<int, List<(int[] Source, string Target)>>? _table;
    private readonly Dictionary<string, int> _pieceIds = new(StringComparer.Ordinal);
    private readonly int _maxPieceChars;
    private readonly float _unknownScore;

    private SentencePieceTokenizer(SentencePieceModel model, Dictionary<int, List<(int[] Source, string Target)>>? table)
    {
        _model = model;
        _table = table;
        float min = float.MaxValue;
        for (int i = 0; i < model.Pieces.Count; i++)
        {
            SentencePieceType t = model.Types[i];
            if (t is SentencePieceType.Normal or SentencePieceType.UserDefined)
            {
                _pieceIds.TryAdd(model.Pieces[i], i);
                _maxPieceChars = Math.Max(_maxPieceChars, model.Pieces[i].Length);
                if (t == SentencePieceType.Normal)
                {
                    min = Math.Min(min, model.Scores[i]);
                }
            }
        }

        _unknownScore = (min == float.MaxValue ? 0f : min) - UnknownPenalty;
    }

    /// <summary>The model.</summary>
    public SentencePieceModel Model => _model;

    /// <summary>Creates a tokenizer.</summary>
    /// <param name="model">The model.</param>
    /// <param name="normalizationTable">The rules of an <c>nmt_nfkc</c> model (lines of source code points in hex separated by spaces, a tab, then the hex code points it becomes); the table packed with this assembly when <see langword="null"/>.</param>
    /// <returns>The tokenizer.</returns>
    /// <exception cref="SentencePieceFormatException">The model names a normalizer this reader does not support, or the table is malformed.</exception>
    public static SentencePieceTokenizer Create(SentencePieceModel model, TextReader? normalizationTable = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        Dictionary<int, List<(int[] Source, string Target)>>? table;
        switch (model.NormalizerName)
        {
            case "nmt_nfkc":
                table = ReadTable(normalizationTable);
                break;
            case "identity" or "":
                table = null;
                break;
            default:
                throw new SentencePieceFormatException($"The normalizer '{model.NormalizerName}' is not supported (supported: nmt_nfkc, identity).");
        }

        return new SentencePieceTokenizer(model, table);
    }

    private static Dictionary<int, List<(int[] Source, string Target)>> ReadTable(TextReader? reader)
    {
        TextReader r = reader ?? new StreamReader(
            typeof(SentencePieceTokenizer).Assembly.GetManifestResourceStream("OneIndex.Models.Tokenization.nmt_nfkc.tsv")
                ?? throw new SentencePieceFormatException("The packed normalization table is missing from this build."), Encoding.UTF8);
        try
        {
            var table = new Dictionary<int, List<(int[] Source, string Target)>>();
            while (r.ReadLine() is { } line)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                int tab = line.IndexOf('\t', StringComparison.Ordinal);
                if (tab < 0)
                {
                    throw new SentencePieceFormatException("A normalization table line has no tab.");
                }

                int[] source = [.. line[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
                if (source.Length == 0)
                {
                    throw new SentencePieceFormatException("A normalization rule has no source.");
                }

                var sb = new StringBuilder();
                foreach (string part in line[(tab + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    sb.Append(char.ConvertFromUtf32(int.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));
                }

                if (!table.TryGetValue(source[0], out List<(int[] Source, string Target)>? rules))
                {
                    table[source[0]] = rules = [];
                }

                rules.Add((source, sb.ToString()));
            }

            foreach (List<(int[] Source, string Target)> rules in table.Values)
            {
                rules.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));
            }

            return table;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentOutOfRangeException or OverflowException)
        {
            throw new SentencePieceFormatException("The normalization table is malformed.");
        }
        finally
        {
            if (reader is null)
            {
                r.Dispose();
            }
        }
    }

    /// <summary>Normalizes text the way the model does before segmenting it, including the dummy prefix and U+2581 escaping.</summary>
    /// <param name="text">The text; lone surrogates are replaced by U+FFFD.</param>
    /// <returns>The normalized text; empty when nothing is left after trimming.</returns>
    public string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int[] cps = new int[text.Length];
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cps[n++] = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else
            {
                cps[n++] = char.IsSurrogate(c) ? 0xFFFD : c;
            }
        }

        var sb = new StringBuilder(text.Length + 1);
        for (int i = 0; i < n;)
        {
            i += _table is null ? Append(sb, cps[i], 1) : ApplyRule(cps, n, i, sb);
        }

        string s = sb.ToString();
        if (_model.RemoveExtraWhitespaces)
        {
            sb.Clear();
            bool pendingSpace = false;
            foreach (char c in s)
            {
                if (c == ' ')
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }

                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }

                sb.Append(c);
            }

            s = sb.ToString();
        }

        if (s.Length == 0)
        {
            return s;
        }

        if (_model.AddDummyPrefix)
        {
            s = " " + s;
        }

        return _model.EscapeWhitespaces ? s.Replace(' ', Escaped) : s;
    }

    private static int Append(StringBuilder sb, int cp, int consumed)
    {
        if (cp > 0xFFFF)
        {
            sb.Append(char.ConvertFromUtf32(cp));
        }
        else
        {
            sb.Append((char)cp);
        }

        return consumed;
    }

    // Longest match at position i: a table rule (single characters and short sequences) or the Hangul jamo composition of the Unicode
    // standard (leading consonant + vowel [+ trailing consonant] become a syllable); otherwise the character is copied.
    private int ApplyRule(int[] cps, int n, int i, StringBuilder sb)
    {
        int c = cps[i];
        if (c is >= 0x1100 and <= 0x1112 && i + 1 < n && cps[i + 1] is >= 0x1161 and <= 0x1175)
        {
            int syllable = 0xAC00 + ((c - 0x1100) * 21 + (cps[i + 1] - 0x1161)) * 28;
            if (i + 2 < n && cps[i + 2] is >= 0x11A8 and <= 0x11C2)
            {
                sb.Append((char)(syllable + cps[i + 2] - 0x11A7));
                return 3;
            }

            sb.Append((char)syllable);
            return 2;
        }

        if (_table!.TryGetValue(c, out List<(int[] Source, string Target)>? rules))
        {
            foreach ((int[] source, string target) in rules)
            {
                if (i + source.Length > n)
                {
                    continue;
                }

                bool match = true;
                for (int k = 1; k < source.Length; k++)
                {
                    if (cps[i + k] != source[k])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    sb.Append(target);
                    return source.Length;
                }
            }
        }

        return Append(sb, c, 1);
    }

    /// <summary>Segments text into piece ids (the model's own ids, with the unknown symbol for uncovered characters).</summary>
    /// <param name="text">The text.</param>
    /// <param name="ids">Receives the ids in order, appended to what it holds.</param>
    /// <returns>The number of ids appended.</returns>
    public int Encode(string text, List<int> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        string s = Normalize(text);
        int n = s.Length;
        if (n == 0)
        {
            return 0;
        }

        float[] best = new float[n + 1];
        int[] from = new int[n + 1];
        int[] piece = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        var lookup = _pieceIds.GetAlternateLookup<ReadOnlySpan<char>>();
        for (int i = 0; i < n;)
        {
            int step = char.IsHighSurrogate(s[i]) && i + 1 < n && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            if (float.IsNegativeInfinity(best[i]))
            {
                i += step;
                continue;
            }

            bool single = false;
            int max = Math.Min(_maxPieceChars, n - i);
            for (int len = 1; len <= max; len++)
            {
                int end = i + len;
                if (end < n && char.IsLowSurrogate(s[end]) && char.IsHighSurrogate(s[end - 1]))
                {
                    continue; // never end a piece inside a surrogate pair
                }

                if (lookup.TryGetValue(s.AsSpan(i, len), out int id))
                {
                    // Equal totals keep the earlier start. The reference encoder breaks a few exact ties the other way (see docs/models.md).
                    float candidate = best[i] + _model.Scores[id];
                    if (candidate > best[end])
                    {
                        best[end] = candidate;
                        from[end] = i;
                        piece[end] = id;
                    }

                    if (len == step)
                    {
                        single = true;
                    }
                }
            }

            if (!single)
            {
                float candidate = best[i] + _unknownScore;
                if (candidate > best[i + step])
                {
                    best[i + step] = candidate;
                    from[i + step] = i;
                    piece[i + step] = -1;
                }
            }

            i += step;
        }

        int count = 0;
        var reversed = new List<int>();
        for (int j = n; j > 0; j = from[j])
        {
            reversed.Add(piece[j]);
        }

        bool previousUnknown = false;
        for (int k = reversed.Count - 1; k >= 0; k--)
        {
            int id = reversed[k];
            if (id < 0)
            {
                if (!previousUnknown)
                {
                    ids.Add(_model.UnkId);
                    count++;
                }

                previousUnknown = true;
            }
            else
            {
                ids.Add(id);
                count++;
                previousUnknown = false;
            }
        }

        return count;
    }
}
