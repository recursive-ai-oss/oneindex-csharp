namespace OneIndex.Analysis;

/// <summary>
/// The words a <see cref="CompoundSplitter"/> may split a compound into, each with a count. A count can be a corpus frequency or 1 for
/// a plain word list. Words are stored in lower case; lookups compare the lower-cased candidate part.
/// </summary>
public sealed class CompoundVocabulary
{
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);

    /// <summary>The number of words.</summary>
    public int Count => _counts.Count;

    /// <summary>Whether the counts carry information: false for a plain word list (every count 1).</summary>
    public bool HasFrequencies { get; private set; }

    /// <summary>Builds a vocabulary from a plain word list (every count is 1).</summary>
    /// <param name="words">The words; empty entries are ignored.</param>
    /// <returns>The vocabulary.</returns>
    public static CompoundVocabulary FromWords(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var v = new CompoundVocabulary();
        foreach (string w in words)
        {
            v.Add(w, 1);
        }

        return v;
    }

    /// <summary>Builds a vocabulary from words with counts (for example a corpus frequency list).</summary>
    /// <param name="words">The words and their counts; entries with an empty word or a count below 1 are ignored; repeats add up.</param>
    /// <returns>The vocabulary.</returns>
    public static CompoundVocabulary FromFrequencies(IEnumerable<(string Word, long Count)> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        var v = new CompoundVocabulary();
        foreach ((string w, long c) in words)
        {
            v.Add(w, c);
        }

        return v;
    }

    /// <summary>Reads lines of <c>word</c> or <c>word TAB count</c>. A line that starts with '#' is a comment.</summary>
    /// <param name="reader">The text.</param>
    /// <returns>The vocabulary.</returns>
    public static CompoundVocabulary Read(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var v = new CompoundVocabulary();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            if (tab < 0)
            {
                v.Add(line, 1);
            }
            else if (long.TryParse(line.AsSpan(tab + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long count))
            {
                v.Add(line[..tab], count);
            }
        }

        return v;
    }

    private void Add(string word, long count)
    {
        if (word.Length == 0 || count < 1)
        {
            return;
        }

        string key = word.ToLowerInvariant();
        _counts[key] = _counts.TryGetValue(key, out long old) ? old + count : count;
        if (count > 1)
        {
            HasFrequencies = true;
        }
    }

    internal long CountOf(ReadOnlySpan<char> lowerCasePart)
        => _counts.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(lowerCasePart, out long c) ? c : 0;
}

/// <summary>How a <see cref="CompoundSplitter"/> decides.</summary>
public sealed class CompoundOptions
{
    /// <summary>The shortest part, in characters. Default 3.</summary>
    public int MinPartLength { get; init; } = 3;

    /// <summary>The shortest word that is tried for splitting. Default 6 (two parts of three).</summary>
    public int MinWordLength { get; init; } = 6;

    /// <summary>The most parts a compound is split into. Default 4.</summary>
    public int MaxParts { get; init; } = 4;

    /// <summary>
    /// Joining elements that may sit between two parts and belong to neither (German "Liebe-s-brief", Dutch "kinder-en"). Default none.
    /// </summary>
    public IReadOnlyList<string> LinkingElements { get; init; } = [];

    /// <summary>
    /// With frequencies, split a word only when the parts are more frequent than the word itself (the rule of Koehn and Knight 2003, section 3):
    /// a frequent compound stays whole. Ignored for a plain word list. Default <see langword="true"/>.
    /// </summary>
    public bool RequirePartsMoreFrequentThanWord { get; init; } = true;

    /// <summary>The options for a language: its joining elements. Languages without a preset get no joining elements.</summary>
    /// <param name="language">An ISO 639-1 code: <c>de</c>, <c>nl</c>, <c>sv</c>, <c>da</c>, <c>no</c>, <c>fi</c> or <c>is</c>.</param>
    /// <returns>New options.</returns>
    public static CompoundOptions For(string language)
    {
        ArgumentNullException.ThrowIfNull(language);
        string[] links = language switch
        {
            "de" => ["s", "es", "n", "en", "er", "e", "ens"],
            "nl" => ["s", "en", "e"],
            "sv" => ["s", "o", "u", "a", "e"],
            "da" => ["s", "e"],
            "no" => ["s", "e"],
            "is" => ["s", "a", "u", "i"],
            _ => [],
        };
        return new CompoundOptions { LinkingElements = links };
    }
}

/// <summary>
/// Splits a compound word into dictionary words by corpus-statistics decompounding (Koehn and Knight 2003, "Empirical methods for
/// compound splitting", section 3): every way to cover the word with vocabulary words (optionally joined by linking elements) is
/// scored by the geometric mean of the parts' counts, and the best wins; with fewer parts preferred on ties. Not thread-safe.
/// </summary>
public sealed class CompoundSplitter
{
    private readonly CompoundVocabulary _vocabulary;
    private readonly CompoundOptions _options;
    private double[] _best = new double[64];
    private int[] _from = new int[64], _count = new int[64], _partStart = new int[64], _partLength = new int[64];
    private char[] _lower = new char[64];

    /// <summary>Creates a splitter.</summary>
    /// <param name="vocabulary">The words parts may be.</param>
    /// <param name="options">The decision options; defaults when <see langword="null"/>.</param>
    public CompoundSplitter(CompoundVocabulary vocabulary, CompoundOptions? options = null)
    {
        _vocabulary = vocabulary ?? throw new ArgumentNullException(nameof(vocabulary));
        _options = options ?? new CompoundOptions();
    }

    /// <summary>Splits a word.</summary>
    /// <param name="word">The word as written; case is ignored.</param>
    /// <param name="parts">Receives the parts as (start, length) in <paramref name="word"/>, in order; cleared first. Empty when the word is not split.</param>
    /// <returns><see langword="true"/> when the word was split into at least two parts.</returns>
    public bool TrySplit(ReadOnlySpan<char> word, List<(int Start, int Length)> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        parts.Clear();
        int n = word.Length;
        if (n < _options.MinWordLength || n < 2 * _options.MinPartLength)
        {
            return false;
        }

        if (_lower.Length < n)
        {
            _lower = new char[Math.Max(n, _lower.Length * 2)];
        }

        for (int i = 0; i < n; i++)
        {
            _lower[i] = char.ToLowerInvariant(word[i]);
        }

        // Dynamic program over end positions: best[j][k] would need the part count, so keep the best split per (end, count) and
        // compare means at the end. Counts are small (at most MaxParts), so the table is stored flat.
        int maxParts = _options.MaxParts;
        int cells = (n + 1) * (maxParts + 1);
        if (_best.Length < cells)
        {
            int cap = Math.Max(cells, _best.Length * 2);
            _best = new double[cap];
            _from = new int[cap];
            _count = new int[cap];
            _partStart = new int[cap];
            _partLength = new int[cap];
        }

        Array.Fill(_best, double.NegativeInfinity, 0, cells);
        _best[0] = 0; // zero parts cover zero characters; sum of log counts
        ReadOnlySpan<char> lower = _lower.AsSpan(0, n);
        for (int end = 1; end <= n; end++)
        {
            for (int start = 0; start <= end - _options.MinPartLength; start++)
            {
                long count = _vocabulary.CountOf(lower[start..end]);
                if (count == 0)
                {
                    continue;
                }

                double log = Math.Log(count);
                // The part may follow another part directly, or after a linking element.
                for (int k = 1; k <= maxParts; k++)
                {
                    TryExtend(start, end, k, log, 0);
                    foreach (string link in _options.LinkingElements)
                    {
                        if (start >= link.Length && lower[(start - link.Length)..start].SequenceEqual(link))
                        {
                            TryExtend(start - link.Length, end, k, log, link.Length);
                        }
                    }
                }
            }
        }

        // Choose the part count with the best geometric mean; fewer parts win ties.
        double bestMean = double.NegativeInfinity;
        int bestK = 0;
        for (int k = 2; k <= maxParts; k++)
        {
            double sum = _best[(n * (maxParts + 1)) + k];
            if (sum == double.NegativeInfinity)
            {
                continue;
            }

            double mean = sum / k;
            if (mean > bestMean + 1e-12)
            {
                bestMean = mean;
                bestK = k;
            }
        }

        if (bestK == 0)
        {
            return false;
        }

        if (_options.RequirePartsMoreFrequentThanWord && _vocabulary.HasFrequencies)
        {
            long whole = _vocabulary.CountOf(lower);
            if (whole > 0 && Math.Log(whole) >= bestMean)
            {
                return false;
            }
        }

        int pos = n, kk = bestK;
        var reversed = new List<(int, int)>(bestK);
        while (kk > 0)
        {
            int cell = (pos * (maxParts + 1)) + kk;
            reversed.Add((_partStart[cell], _partLength[cell]));
            pos = _from[cell];
            kk--;
        }

        for (int i = reversed.Count - 1; i >= 0; i--)
        {
            parts.Add(reversed[i]);
        }

        return true;
    }

    // A part [start, end) preceded by linkLength linking characters extends the best split of the text before them.
    private void TryExtend(int gapStart, int end, int k, double log, int linkLength)
    {
        int partStart = gapStart + linkLength;
        int stride = _options.MaxParts + 1;
        double before = _best[(gapStart * stride) + (k - 1)];
        if (before == double.NegativeInfinity)
        {
            return;
        }

        // A linking element only joins two parts, never starts the word.
        if (linkLength > 0 && gapStart == 0)
        {
            return;
        }

        double total = before + log;
        int cell = (end * stride) + k;
        if (total > _best[cell])
        {
            _best[cell] = total;
            _from[cell] = gapStart;
            _partStart[cell] = partStart;
            _partLength[cell] = end - partStart;
            _count[cell] = k;
        }
    }
}

/// <summary>
/// Indexes both a compound and its parts: each token that <see cref="CompoundSplitter"/> splits is passed on whole and followed by its
/// parts at the same position (increment 0, type <see cref="TokenType.Synonym"/>), all with the compound's offsets. A query analyzed
/// with the same filter finds the compound through either form. Terms the splitter does not split pass through unchanged. Put it
/// after case folding.
/// </summary>
/// <param name="input">The stream to read from.</param>
/// <param name="splitter">The splitter; not shared between threads.</param>
public sealed class DecompoundFilter(TokenSource input, CompoundSplitter splitter) : TokenFilter(input)
{
    private readonly CompoundSplitter _splitter = splitter ?? throw new ArgumentNullException(nameof(splitter));
    private readonly List<(int Start, int Length)> _parts = [];
    private char[] _word = new char[64];
    private int _next;

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        base.Reset(text);
        _parts.Clear();
        _next = 0;
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        Token t = Token;
        if (_next < _parts.Count)
        {
            (int start, int length) = _parts[_next++];
            t.SetTerm(_word.AsSpan(start, length));
            t.PositionIncrement = 0;
            t.Type = TokenType.Synonym;
            return true;
        }

        _parts.Clear();
        _next = 0;
        if (!Input.MoveNext())
        {
            return false;
        }

        ReadOnlySpan<char> term = t.Term;
        if (_splitter.TrySplit(term, _parts))
        {
            if (_word.Length < term.Length)
            {
                _word = new char[Math.Max(term.Length, _word.Length * 2)];
            }

            term.CopyTo(_word);
        }

        return true;
    }
}
