using System.Buffers.Binary;
using System.Globalization;

namespace OneIndex.Analysis.Asian;

/// <summary>
/// A sorted word list, optionally with a frequency per word, used by the Chinese segmenter and the Thai, Lao, Khmer and Myanmar
/// breakers. Words are sorted by UTF-16 code units and found by a common-prefix search. Immutable and thread-safe.
/// </summary>
public sealed class WordList
{
    internal const uint Kind = 2;
    internal const uint SecWords = 1, SecOffsets = 2, SecFrequency = 3;

    private readonly char[] _chars;
    private readonly int[] _offsets;
    private readonly float[] _logProbability;

    private WordList(Dictionary<uint, byte[]> s, DictionaryLimits limits)
    {
        byte[] chars = s[SecWords];
        _chars = new char[chars.Length / 2];
        for (int i = 0; i < _chars.Length; i++)
        {
            _chars[i] = (char)BinaryPrimitives.ReadUInt16LittleEndian(chars.AsSpan(i * 2));
        }

        byte[] o = s[SecOffsets];
        if (o.Length % 4 != 0 || o.Length < 4)
        {
            throw new DictionaryFormatException("The word offsets are malformed.");
        }

        int n = o.Length / 4 - 1;
        if (n > limits.MaxEntries)
        {
            throw new DictionaryFormatException("The word count is above the limit.");
        }

        _offsets = new int[n + 1];
        int prev = 0;
        for (int i = 0; i <= n; i++)
        {
            int v = BinaryPrimitives.ReadInt32LittleEndian(o.AsSpan(i * 4));
            if (v < prev || v > _chars.Length)
            {
                throw new DictionaryFormatException("Word offsets are not increasing or run past the character data.");
            }

            _offsets[i] = v;
            prev = v;
        }

        if (_offsets[0] != 0 || _offsets[n] != _chars.Length)
        {
            throw new DictionaryFormatException("Word offsets do not cover the character data.");
        }

        byte[] f = s[SecFrequency];
        if (f.Length != n * 4)
        {
            throw new DictionaryFormatException("The frequency column has the wrong length.");
        }

        long total = 0;
        var freq = new uint[n];
        for (int i = 0; i < n; i++)
        {
            freq[i] = BinaryPrimitives.ReadUInt32LittleEndian(f.AsSpan(i * 4));
            total += freq[i];
        }

        // Words without a frequency count as 1, so the probabilities stay defined.
        double logTotal = Math.Log(Math.Max(total, 1) + n);
        _logProbability = new float[n];
        for (int i = 0; i < n; i++)
        {
            _logProbability[i] = (float)(Math.Log(freq[i] + 1.0) - logTotal);
        }

        Count = n;
        LogUnknown = (float)(Math.Log(1.0) - logTotal);
        int max = 1;
        for (int i = 0; i < n; i++)
        {
            max = Math.Max(max, _offsets[i + 1] - _offsets[i]);
        }

        MaxWordLength = max;
    }

    /// <summary>The number of words.</summary>
    public int Count { get; }

    internal int MaxWordLength { get; }

    internal float LogUnknown { get; }

    internal float LogProbability(int word) => _logProbability[word];

    /// <summary>Reads a word-list dictionary file.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="limits">Ceilings checked before allocating; defaults when <see langword="null"/>.</param>
    /// <returns>The dictionary.</returns>
    /// <exception cref="DictionaryFormatException">The file is malformed, truncated, fails its checksum or exceeds the limits.</exception>
    public static WordList Load(ReadOnlySpan<byte> file, DictionaryLimits? limits = null)
    {
        limits ??= DictionaryLimits.Default;
        Dictionary<uint, byte[]> sections = DictionaryContainer.Read(file, Kind, limits);
        foreach (uint id in new[] { SecWords, SecOffsets, SecFrequency })
        {
            if (!sections.ContainsKey(id))
            {
                throw new DictionaryFormatException($"Section {id} is missing.");
            }
        }

        return new WordList(sections, limits);
    }

    /// <summary>Tries to read a word-list dictionary file without throwing.</summary>
    /// <param name="file">The whole file.</param>
    /// <param name="dictionary">The dictionary on success.</param>
    /// <returns><see langword="true"/> when the file is valid.</returns>
    public static bool TryLoad(ReadOnlySpan<byte> file, out WordList? dictionary)
    {
        try
        {
            dictionary = Load(file);
            return true;
        }
        catch (DictionaryFormatException)
        {
            dictionary = null;
            return false;
        }
    }

    /// <summary>Whether the exact word is listed.</summary>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> when it is in the dictionary.</returns>
    public bool Contains(ReadOnlySpan<char> word)
    {
        int lo = 0, hi = Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = _chars.AsSpan(_offsets[mid], _offsets[mid + 1] - _offsets[mid]).SequenceCompareTo(word);
            if (c == 0)
            {
                return true;
            }

            if (c < 0)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return false;
    }

    internal void CommonPrefixSearch(ReadOnlySpan<char> text, List<(int Word, int Length)> results)
    {
        int lo = 0, hi = Count;
        for (int k = 0; lo < hi; k++)
        {
            if (k > 0)
            {
                while (lo < hi && _offsets[lo + 1] - _offsets[lo] == k)
                {
                    results.Add((lo, k));
                    lo++;
                }
            }

            if (k >= text.Length || lo >= hi)
            {
                break;
            }

            char c = text[k];
            int a = lo, b = hi;
            while (a < b)
            {
                int mid = (a + b) >>> 1;
                if (_chars[_offsets[mid] + k] < c)
                {
                    a = mid + 1;
                }
                else
                {
                    b = mid;
                }
            }

            int start = a;
            b = hi;
            while (a < b)
            {
                int mid = (a + b) >>> 1;
                if (_chars[_offsets[mid] + k] <= c)
                {
                    a = mid + 1;
                }
                else
                {
                    b = mid;
                }
            }

            lo = start;
            hi = a;
        }
    }

    internal static byte[] Build(IEnumerable<(string Word, uint Frequency)> words)
    {
        var sorted = words.Where(w => w.Word.Length > 0).GroupBy(w => w.Word, StringComparer.Ordinal)
            .Select(g => (Word: g.Key, Frequency: (uint)Math.Min(g.Sum(x => (long)x.Frequency), uint.MaxValue))).ToList();
        sorted.Sort((a, b) => string.CompareOrdinal(a.Word, b.Word));
        int n = sorted.Count;
        var chars = new MemoryStream();
        byte[] offsets = new byte[(n + 1) * 4];
        byte[] freq = new byte[n * 4];
        byte[] two = new byte[2];
        int total = 0;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(offsets.AsSpan(i * 4), total);
            foreach (char c in sorted[i].Word)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(two, c);
                chars.Write(two);
            }

            total += sorted[i].Word.Length;
            BinaryPrimitives.WriteUInt32LittleEndian(freq.AsSpan(i * 4), sorted[i].Frequency);
        }

        BinaryPrimitives.WriteInt32LittleEndian(offsets.AsSpan(n * 4), total);
        return DictionaryContainer.Write(Kind, [(SecWords, chars.ToArray()), (SecOffsets, offsets), (SecFrequency, freq)]);
    }
}

/// <summary>
/// Chinese word segmentation by the most probable path through the word graph: each position lists the dictionary words that start
/// there, a word's score is the log of its relative frequency (a unigram language model), and a dynamic program from the end of the
/// text picks the path with the highest total. A character no word covers becomes a one-character word with the lowest probability.
/// In search mode, a word of three or more characters also stacks its shorter dictionary words as extra tokens at its position.
/// Not thread-safe (scratch buffers).
/// </summary>
public sealed class UnigramSegmenter
{
    private readonly WordList _dictionary;
    private readonly bool _searchMode;
    private readonly List<(int Word, int Length)> _matches = [];
    private float[] _best = new float[128];
    private int[] _choice = new int[128], _choiceWord = new int[128];

    /// <summary>Creates a segmenter.</summary>
    /// <param name="dictionary">The word list with frequencies.</param>
    /// <param name="searchMode">Whether long words also stack their parts. Default <see langword="true"/>.</param>
    public UnigramSegmenter(WordList dictionary, bool searchMode = true)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
        _searchMode = searchMode;
    }

    /// <summary>Segments <paramref name="text"/>.</summary>
    /// <param name="text">The text, normally a run of Han characters.</param>
    /// <param name="output">Receives the segments in order (parts follow their word with <see cref="Segment.Stacked"/>); cleared first.</param>
    public void Segment(ReadOnlySpan<char> text, List<Segment> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        int n = text.Length;
        if (n == 0)
        {
            return;
        }

        if (_best.Length < n + 1)
        {
            int cap = Math.Max(n + 1, _best.Length * 2);
            _best = new float[cap];
            _choice = new int[cap];
            _choiceWord = new int[cap];
        }

        _best[n] = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            // A lone character is always possible, as an unknown word unless the dictionary lists it.
            int step = char.IsHighSurrogate(text[i]) && i + 1 < n && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            float bestScore = _dictionary.LogUnknown + _best[i + step];
            int bestLen = step, bestWord = -1;
            _matches.Clear();
            _dictionary.CommonPrefixSearch(text.Slice(i, Math.Min(n - i, _dictionary.MaxWordLength)), _matches);
            foreach ((int word, int len) in _matches)
            {
                float score = _dictionary.LogProbability(word) + _best[i + len];
                if (score > bestScore || (score == bestScore && len > bestLen))
                {
                    bestScore = score;
                    bestLen = len;
                    bestWord = word;
                }
            }

            _best[i] = bestScore;
            _choice[i] = bestLen;
            _choiceWord[i] = bestWord;
        }

        for (int i = 0; i < n;)
        {
            int len = _choice[i];
            output.Add(new Segment(i, len, _choiceWord[i], 0));
            if (_searchMode && len >= 3)
            {
                _matches.Clear();
                _dictionary.CommonPrefixSearch(text.Slice(i, len), _matches);
                foreach ((int word, int partLen) in _matches)
                {
                    if (partLen >= 2 && partLen < len)
                    {
                        output.Add(new Segment(i, partLen, word, 0, true));
                    }
                }

                // Parts that start later inside the word.
                for (int j = i + 1; j < i + len - 1; j++)
                {
                    _matches.Clear();
                    _dictionary.CommonPrefixSearch(text.Slice(j, i + len - j), _matches);
                    foreach ((int word, int partLen) in _matches)
                    {
                        if (partLen >= 2)
                        {
                            output.Add(new Segment(j, partLen, word, 0, true));
                        }
                    }
                }
            }

            i += len;
        }
    }
}

/// <summary>
/// Word breaking for scripts written without spaces (Thai, Lao, Khmer, Myanmar) by maximal matching over a word list: the path with
/// the fewest characters left unmatched wins, then the fewest words, then the longer first word. A boundary never falls before a
/// combining mark or a spacing vowel sign, and unmatched characters are grouped into one token. Not thread-safe.
/// </summary>
public sealed class MaximalMatchSegmenter
{
    private readonly WordList _dictionary;
    private readonly List<(int Word, int Length)> _matches = [];
    private int[] _unmatched = new int[128], _words = new int[128], _next = new int[128];

    /// <summary>Creates a segmenter.</summary>
    /// <param name="dictionary">The word list (frequencies are ignored).</param>
    public MaximalMatchSegmenter(WordList dictionary)
    {
        _dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary));
    }

    /// <summary>Breaks <paramref name="text"/> into words.</summary>
    /// <param name="text">The text, normally one run of a single script.</param>
    /// <param name="output">Receives the segments in order, covering the text; cleared first.</param>
    public void Segment(ReadOnlySpan<char> text, List<Segment> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        output.Clear();
        int n = text.Length;
        if (n == 0)
        {
            return;
        }

        if (_unmatched.Length < n + 1)
        {
            int cap = Math.Max(n + 1, _unmatched.Length * 2);
            _unmatched = new int[cap];
            _words = new int[cap];
            _next = new int[cap];
        }

        _unmatched[n] = 0;
        _words[n] = 0;
        for (int i = n - 1; i >= 0; i--)
        {
            // Unmatched cluster: one base character plus the marks that must stay with it.
            int cl = i + 1;
            while (cl < n && !CanBreakBefore(text, cl))
            {
                cl++;
            }

            int bestU = (cl - i) + _unmatched[cl], bestW = 1 + _words[cl], bestNext = cl;
            _matches.Clear();
            _dictionary.CommonPrefixSearch(text.Slice(i, Math.Min(n - i, _dictionary.MaxWordLength)), _matches);
            foreach ((int _, int len) in _matches)
            {
                int j = i + len;
                if (j < n && !CanBreakBefore(text, j))
                {
                    continue;
                }

                int u = _unmatched[j], w = 1 + _words[j];
                if (u < bestU || (u == bestU && (w < bestW || (w == bestW && j > bestNext))))
                {
                    bestU = u;
                    bestW = w;
                    bestNext = j;
                }
            }

            _unmatched[i] = bestU;
            _words[i] = bestW;
            _next[i] = bestNext;
        }

        int start = 0;
        int unknownStart = -1;
        while (start < n)
        {
            int next = _next[start];
            bool known = false;
            if (_unmatched[start] == _unmatched[next] && next - start > 0)
            {
                // The step matched a word when it did not add unmatched characters.
                known = true;
            }

            if (known)
            {
                if (unknownStart >= 0)
                {
                    output.Add(new Segment(unknownStart, start - unknownStart, -1, 0));
                    unknownStart = -1;
                }

                output.Add(new Segment(start, next - start, 0, 0));
            }
            else if (unknownStart < 0)
            {
                unknownStart = start;
            }

            start = next;
        }

        if (unknownStart >= 0)
        {
            output.Add(new Segment(unknownStart, n - unknownStart, -1, 0));
        }
    }

    private static bool CanBreakBefore(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];
        if (char.IsLowSurrogate(c))
        {
            return false;
        }

        UnicodeCategory cat = CharUnicodeInfo.GetUnicodeCategory(c);
        if (cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format)
        {
            return false;
        }

        // Khmer and Myanmar write a subscript consonant after a coeng / virama; a break right after it would split the cluster.
        char prev = text[index - 1];
        return prev is not ('\u17D2' or '\u1039' or '\u200D');
    }
}
