namespace OneIndex.Analysis.Asian;

/// <summary>One piece of text the segmenter found.</summary>
/// <param name="Start">Index of the first UTF-16 unit.</param>
/// <param name="Length">Length in UTF-16 units; at least 1.</param>
/// <param name="Word">The dictionary entry, or -1 for an unknown word built from character classes.</param>
/// <param name="Pos">The index of the part-of-speech label (see <see cref="LatticeLexicon.PartOfSpeech"/>).</param>
/// <param name="Stacked">The piece sits at the same position as the segment before it: a part of a longer word, added for search recall.</param>
public readonly record struct Segment(int Start, int Length, int Word, int Pos, bool Stacked = false);

/// <summary>How <see cref="LatticeSegmenter"/> scores long words.</summary>
/// <param name="HanThreshold">A word of more than this many Han or Hanja characters pays <paramref name="HanPenalty"/> per extra character; 0 turns the penalty off. Default 2.</param>
/// <param name="HanPenalty">Cost units per extra Han character. Default 3000 (a typical word cost is 3000 to 10000).</param>
/// <param name="KatakanaThreshold">The same for Katakana words, from this length. Default 7.</param>
/// <param name="KatakanaPenalty">Cost units per extra Katakana character. Default 10000.</param>
public sealed record LatticeOptions(int HanThreshold = 2, int HanPenalty = 3000, int KatakanaThreshold = 7, int KatakanaPenalty = 10000)
{
    /// <summary>Search mode: long compounds are split into the parts that users query for.</summary>
    public static LatticeOptions Search { get; } = new();

    /// <summary>Normal mode: the cheapest path under the dictionary's own costs, no length penalty.</summary>
    public static LatticeOptions Normal { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Segments text by finding the cheapest path through the word lattice (Kudo, Yamamoto and Matsumoto 2004, section 2.2; the path is
/// found with the Viterbi algorithm): every dictionary word and every unknown-word candidate that starts at a position becomes a node, and a
/// path costs the sum of its words' costs and the connection costs between neighbours. Unknown candidates follow the dictionary's
/// character classes: <c>Invoke</c> always builds them, otherwise only where no word matches; <c>Group</c> builds one candidate
/// from a run of same-class characters; <c>Length</c> also builds candidates of 1 to that many characters.
/// An instance keeps scratch buffers: it is not thread-safe, so each analyzer instance owns one.
/// </summary>
public sealed class LatticeSegmenter
{
    private const int MaxUnknownRun = 255;

    private readonly LatticeLexicon _dictionary;
    private readonly LatticeOptions _options;
    private readonly List<(int Word, int Length)> _matches = [];
    private readonly bool[] _isHan, _isKatakana;
    private int[] _start = new int[256], _length = new int[256], _left = new int[256], _right = new int[256], _word = new int[256],
        _pos = new int[256], _best = new int[256], _prev = new int[256], _next = new int[256];
    private int[] _endHead = new int[128];
    private int _nodes;

    /// <summary>Creates a segmenter.</summary>
    /// <param name="dictionary">The dictionary.</param>
    /// <param name="options">Length penalties; <see cref="LatticeOptions.Search"/> when <see langword="null"/>.</param>
    public LatticeSegmenter(LatticeLexicon dictionary, LatticeOptions? options = null)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _options = options ?? LatticeOptions.Search;
        _isHan = new bool[dictionary.Classes.Count];
        _isKatakana = new bool[dictionary.Classes.Count];
        for (int i = 0; i < dictionary.Classes.Count; i++)
        {
            string name = dictionary.Classes[i].Name;
            _isHan[i] = name is "KANJI" or "HANJA";
            _isKatakana[i] = name == "KATAKANA";
        }
    }

    /// <summary>The dictionary in use.</summary>
    public LatticeLexicon Dictionary => _dictionary;

    /// <summary>Segments <paramref name="text"/>, which should not contain white space (callers split at white space first).</summary>
    /// <param name="text">The text.</param>
    /// <param name="output">Receives the segments in order; it is cleared first. They cover the whole text without gaps.</param>
    public void Segment(ReadOnlySpan<char> text, List<Segment> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        int n = text.Length;
        if (n == 0)
        {
            return;
        }

        if (_endHead.Length < n + 1)
        {
            _endHead = new int[Math.Max(n + 1, _endHead.Length * 2)];
        }

        Array.Fill(_endHead, -1, 0, n + 1);
        _nodes = 0;
        int bos = NewNode(0, 0, 0, 0, -1, 0);
        _best[bos] = 0;
        _prev[bos] = -1;
        Link(0, bos);

        for (int i = 0; i < n; i++)
        {
            if (_endHead[i] < 0)
            {
                continue;
            }

            int firstNew = _nodes;
            _matches.Clear();
            int window = Math.Min(n - i, _dictionary.MaxWordLength);
            _dictionary.CommonPrefixSearch(text.Slice(i, window), _matches);
            foreach ((int word, int len) in _matches)
            {
                int cls = _dictionary.ClassOf(CodePointAt(text, i));
                int cost = _dictionary.Cost(word) + Penalty(text, i, len, cls);
                int id = NewNode(i, len, _dictionary.Left(word), _dictionary.Right(word), word, _dictionary.PosIndex(word));
                _best[id] = cost; // word cost only for now; the connection is added below.
            }

            AddUnknown(text, i, _matches.Count > 0);
            for (int id = firstNew; id < _nodes; id++)
            {
                int wordCost = _best[id];
                int bestTotal = int.MaxValue, bestPrev = -1;
                for (int p = _endHead[i]; p >= 0; p = _next[p])
                {
                    int total = _best[p] + _dictionary.Connection(_right[p], _left[id]) + wordCost;
                    if (total < bestTotal)
                    {
                        bestTotal = total;
                        bestPrev = p;
                    }
                }

                _best[id] = bestTotal;
                _prev[id] = bestPrev;
            }

            // Link after all nodes at i are scored, so a node never connects to a node that starts at the same position.
            for (int id = firstNew; id < _nodes; id++)
            {
                Link(_start[id] + _length[id], id);
            }
        }

        int end = -1, endCost = int.MaxValue;
        for (int p = _endHead[n]; p >= 0; p = _next[p])
        {
            int total = _best[p] + _dictionary.Connection(_right[p], 0);
            if (total < endCost)
            {
                endCost = total;
                end = p;
            }
        }

        // The unknown candidates guarantee a path; this guard keeps a damaged dictionary from looping or throwing.
        if (end < 0)
        {
            output.Add(new Segment(0, n, -1, _dictionary.PosCount > 0 ? 0 : 0));
            return;
        }

        for (int p = end; p > 0; p = _prev[p])
        {
            output.Add(new Segment(_start[p], _length[p], _word[p], _pos[p]));
        }

        output.Reverse();
    }

    private int Penalty(ReadOnlySpan<char> text, int start, int length, int cls)
    {
        if (length <= 1)
        {
            return 0;
        }

        if (_isHan[cls] && _options.HanThreshold > 0 && length > _options.HanThreshold)
        {
            // Count only when the whole word is Han; a mixed word (okurigana) is not a compound.
            for (int k = 1; k < length; k++)
            {
                if (!_isHan[_dictionary.ClassOf(CodePointAt(text, start + k))])
                {
                    return 0;
                }
            }

            return (length - _options.HanThreshold) * _options.HanPenalty;
        }

        if (_isKatakana[cls] && _options.KatakanaThreshold > 0 && length > _options.KatakanaThreshold)
        {
            for (int k = 1; k < length; k++)
            {
                if (!_isKatakana[_dictionary.ClassOf(CodePointAt(text, start + k))])
                {
                    return 0;
                }
            }

            return (length - _options.KatakanaThreshold) * _options.KatakanaPenalty;
        }

        return 0;
    }

    private void AddUnknown(ReadOnlySpan<char> text, int i, bool hadWords)
    {
        int cp = CodePointAt(text, i);
        int cls = _dictionary.ClassOf(cp);
        CharacterClass info = _dictionary.Classes[cls];
        if (hadWords && !info.Invoke)
        {
            return;
        }

        int first = CharLength(cp);
        int groupEnd = i + first;
        if (info.Group)
        {
            while (groupEnd < text.Length && groupEnd - i < MaxUnknownRun)
            {
                int c2 = CodePointAt(text, groupEnd);
                if (_dictionary.ClassOf(c2) != cls)
                {
                    break;
                }

                groupEnd += CharLength(c2);
            }

            EmitUnknown(text, i, groupEnd - i, cls);
        }

        if (info.Length > 0 || !info.Group)
        {
            int end = i + first;
            int chars = 1;
            int max = Math.Max(info.Length, 1);
            while (true)
            {
                if (!(info.Group && end - i == groupEnd - i))
                {
                    EmitUnknown(text, i, end - i, cls);
                }

                if (chars >= max || end >= text.Length)
                {
                    break;
                }

                int c2 = CodePointAt(text, end);
                if (_dictionary.ClassOf(c2) != cls)
                {
                    break;
                }

                end += CharLength(c2);
                chars++;
            }
        }
    }

    private void EmitUnknown(ReadOnlySpan<char> text, int start, int length, int cls)
    {
        ReadOnlySpan<LatticeLexicon.UnknownEntry> entries = _dictionary.UnknownFor(cls);
        if (entries.Length == 0)
        {
            entries = _dictionary.UnknownFor(_dictionary.DefaultClass);
        }

        foreach (LatticeLexicon.UnknownEntry u in entries)
        {
            int id = NewNode(start, length, u.Left, u.Right, -1, u.Pos);
            _best[id] = u.Cost + Penalty(text, start, length, cls);
        }
    }

    private int NewNode(int start, int length, int left, int right, int word, int pos)
    {
        if (_nodes == _start.Length)
        {
            int cap = _nodes * 2;
            Array.Resize(ref _start, cap);
            Array.Resize(ref _length, cap);
            Array.Resize(ref _left, cap);
            Array.Resize(ref _right, cap);
            Array.Resize(ref _word, cap);
            Array.Resize(ref _pos, cap);
            Array.Resize(ref _best, cap);
            Array.Resize(ref _prev, cap);
            Array.Resize(ref _next, cap);
        }

        int id = _nodes++;
        _start[id] = start;
        _length[id] = length;
        _left[id] = left;
        _right[id] = right;
        _word[id] = word;
        _pos[id] = pos;
        _best[id] = 0;
        _prev[id] = -1;
        _next[id] = -1;
        return id;
    }

    private void Link(int endPosition, int id)
    {
        _next[id] = _endHead[endPosition];
        _endHead[endPosition] = id;
    }

    private static int CodePointAt(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        return c;
    }

    private static int CharLength(int codePoint) => codePoint > 0xFFFF ? 2 : 1;
}
