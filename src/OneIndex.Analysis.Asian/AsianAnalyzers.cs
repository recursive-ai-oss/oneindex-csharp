namespace OneIndex.Analysis.Asian;

/// <summary>
/// The dictionaries that ship with the package, loaded on first use and cached, plus loaders for dictionaries you supply.
/// A dictionary is immutable and shared by every analyzer of its language.
/// </summary>
public static class AsianDictionaries
{
    private static readonly Lazy<LatticeLexicon> JapaneseLazy = new(() => LatticeLexicon.Load(Resource("ja")));
    private static readonly Lazy<LatticeLexicon> KoreanLazy = new(() => LatticeLexicon.Load(Resource("ko")));
    private static readonly Lazy<WordList> ChineseLazy = new(() => WordList.Load(Resource("zh")));
    private static readonly Lazy<WordList> ThaiLazy = new(() => WordList.Load(Resource("th")));
    private static readonly Lazy<WordList> LaoLazy = new(() => WordList.Load(Resource("lo")));
    private static readonly Lazy<WordList> KhmerLazy = new(() => WordList.Load(Resource("km")));
    private static readonly Lazy<WordList> MyanmarLazy = new(() => WordList.Load(Resource("my")));

    /// <summary>The Japanese lattice dictionary (a word list with connection costs derived from a published morphological dictionary).</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static LatticeLexicon Japanese => JapaneseLazy.Value;

    /// <summary>The Korean lattice dictionary.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static LatticeLexicon Korean => KoreanLazy.Value;

    /// <summary>The Chinese word list with frequencies.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static WordList Chinese => ChineseLazy.Value;

    /// <summary>The Thai word list.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static WordList Thai => ThaiLazy.Value;

    /// <summary>The Lao word list.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static WordList Lao => LaoLazy.Value;

    /// <summary>The Khmer word list.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static WordList Khmer => KhmerLazy.Value;

    /// <summary>The Myanmar (Burmese) word list.</summary>
    /// <exception cref="InvalidOperationException">The build of this package carries no packed dictionary.</exception>
    public static WordList Myanmar => MyanmarLazy.Value;

    private static byte[] Resource(string code)
    {
        string name = "OneIndex.Analysis.Asian.Dictionaries." + code + ".oidict";
        using Stream? s = typeof(AsianDictionaries).Assembly.GetManifestResourceStream(name);
        if (s is null)
        {
            throw new InvalidOperationException($"This build of RecursiveAI.OneIndex.Analysis.Asian has no packed '{code}' dictionary. Load one with LatticeLexicon.Load or WordList.Load and pass it to the analyzer.");
        }

        var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}

/// <summary>
/// Registers the Asian-language analyzers with <see cref="AnalyzerRegistry"/> under the names schemas store: <c>ja</c>, <c>zh</c>,
/// <c>ko</c>, <c>th</c>, <c>lo</c>, <c>km</c> and <c>my</c>. Call <see cref="Register"/> once at start-up, before opening an index that
/// uses one of them. Registration is explicit; nothing is discovered by reflection.
/// </summary>
public static class AsianAnalyzers
{
    /// <summary>Registers every analyzer in this package.</summary>
    public static void Register()
    {
        AnalyzerRegistry.Register(JapaneseAnalyzer.RegisteredName, () => new JapaneseAnalyzer());
        AnalyzerRegistry.Register(ChineseAnalyzer.RegisteredName, () => new ChineseAnalyzer());
        AnalyzerRegistry.Register(KoreanAnalyzer.RegisteredName, () => new KoreanAnalyzer());
        AnalyzerRegistry.Register(WordBreakAnalyzer.ThaiName, () => WordBreakAnalyzer.Thai());
        AnalyzerRegistry.Register(WordBreakAnalyzer.LaoName, () => WordBreakAnalyzer.Lao());
        AnalyzerRegistry.Register(WordBreakAnalyzer.KhmerName, () => WordBreakAnalyzer.Khmer());
        AnalyzerRegistry.Register(WordBreakAnalyzer.MyanmarName, () => WordBreakAnalyzer.Myanmar());
    }
}

/// <summary>
/// Common machinery of the Asian tokenizers: the text is cut into spans of the target script (segmented with a dictionary) and
/// everything else (handed to the UAX #29 word tokenizer, so Latin words and numbers inside Asian text stay whole). Tokens keep the
/// offsets of the original text.
/// </summary>
public abstract class AsianTokenizer : Tokenizer
{
    private readonly StandardTokenizer _inner = new();
    private readonly List<Segment> _segments = [];
    private ReadOnlyMemory<char> _text;
    private int _scan;
    private bool _innerActive;
    private int _innerBase;
    private int _spanStart;
    private int _gap;
    private readonly List<(int Start, int Length, string? Term, bool Stacked)> _pending = [];
    private int _pendingIndex;

    /// <summary>Whether a code point belongs to the script this tokenizer segments.</summary>
    /// <param name="codePoint">The code point.</param>
    /// <returns><see langword="true"/> for the target script.</returns>
    protected abstract bool IsTarget(int codePoint);

    /// <summary>Segments one span of target text and into <paramref name="segments"/>.</summary>
    /// <param name="span">The span.</param>
    /// <param name="segments">The reusable list to segment into.</param>
    protected abstract void SegmentSpan(ReadOnlySpan<char> span, List<Segment> segments);

    /// <summary>Turns the segments of the current span into tokens; call <see cref="Emit"/> for each token and <see cref="Skip"/> for each dropped one.</summary>
    /// <param name="span">The span the segments index into.</param>
    /// <param name="segments">The segments from <see cref="SegmentSpan"/>.</param>
    protected abstract void Produce(ReadOnlySpan<char> span, IReadOnlyList<Segment> segments);

    /// <summary>Queues a token for the current span.</summary>
    /// <param name="start">Start within the span.</param>
    /// <param name="length">Length within the span.</param>
    /// <param name="term">The term text, or <see langword="null"/> to use the text of the span as written.</param>
    /// <param name="stacked">Whether it sits at the previous token's position.</param>
    protected void Emit(int start, int length, string? term, bool stacked) => _pending.Add((start, length, term, stacked));

    /// <summary>Records a dropped token (a stop part of speech) so the next token's position leaves a gap.</summary>
    protected void Skip() => _pending.Add((-1, 0, null, false));

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        _text = text;
        _scan = 0;
        _innerActive = false;
        _segments.Clear();
        _pending.Clear();
        _pendingIndex = 0;
        _gap = 0;
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        Token t = Token;
        while (true)
        {
            if (_innerActive)
            {
                if (_inner.MoveNext())
                {
                    Token s = _inner.Token;
                    t.SetTerm(s.Term);
                    t.StartOffset = s.StartOffset + _innerBase;
                    t.EndOffset = s.EndOffset + _innerBase;
                    t.Type = s.Type;
                    t.Marks = s.Marks;
                    t.PositionIncrement = s.PositionIncrement + _gap;
                    _gap = 0;
                    return true;
                }

                _innerActive = false;
            }

            while (_pendingIndex < _pending.Count)
            {
                (int start, int length, string? term, bool stacked) = _pending[_pendingIndex++];
                if (start < 0)
                {
                    _gap++;
                    continue;
                }

                ReadOnlySpan<char> span = _text.Span.Slice(_spanStart, _segmentsSpanLength);
                t.Clear();
                t.SetTerm(term is null ? span.Slice(start, length) : term.AsSpan());
                t.StartOffset = _spanStart + start;
                t.EndOffset = _spanStart + start + length;
                t.Type = TokenType.Ideographic;
                t.PositionIncrement = stacked ? 0 : 1 + _gap;
                if (!stacked)
                {
                    _gap = 0;
                }

                return true;
            }

            if (_scan >= _text.Length)
            {
                return false;
            }

            ReadOnlySpan<char> all = _text.Span;
            int a = _scan;
            bool target = IsTarget(CodePointAt(all, a));
            int b = a;
            while (b < all.Length)
            {
                int cp = CodePointAt(all, b);
                if (IsTarget(cp) != target)
                {
                    break;
                }

                b += cp > 0xFFFF ? 2 : 1;
            }

            _scan = b;
            if (target)
            {
                _spanStart = a;
                _segmentsSpanLength = b - a;
                _pending.Clear();
                _pendingIndex = 0;
                SegmentSpan(all.Slice(a, b - a), _segments);
                Produce(all.Slice(a, b - a), _segments);
            }
            else
            {
                _inner.Reset(_text.Slice(a, b - a));
                _innerBase = a;
                _innerActive = true;
            }
        }
    }

    private int _segmentsSpanLength;

    private static int CodePointAt(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];
        return char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? char.ConvertToUtf32(c, text[index + 1]) : c;
    }

    /// <summary>Han ideographs, including extensions and compatibility ideographs and the ideographic iteration marks.</summary>
    /// <param name="cp">The code point.</param>
    /// <returns><see langword="true"/> for Han.</returns>
    protected static bool IsHan(int cp)
        => (cp >= 0x4E00 && cp <= 0x9FFF) || (cp >= 0x3400 && cp <= 0x4DBF) || (cp >= 0x20000 && cp <= 0x323AF) || (cp >= 0xF900 && cp <= 0xFAFF)
            || (cp >= 0x2E80 && cp <= 0x2FDF) || cp is 0x3005 or 0x3006 or 0x3007 or 0x303B;

    /// <summary>Hiragana, Katakana (including the prolonged sound mark and half-width forms) and the kana voicing marks.</summary>
    /// <param name="cp">The code point.</param>
    /// <returns><see langword="true"/> for kana.</returns>
    protected static bool IsKana(int cp)
        => (cp >= 0x3041 && cp <= 0x309F) || (cp >= 0x30A1 && cp <= 0x30FA) || cp is 0x30FC or 0x30FD or 0x30FE or 0x30FF
            || (cp >= 0x31F0 && cp <= 0x31FF) || (cp >= 0xFF66 && cp <= 0xFF9F) || cp is 0x3099 or 0x309A;

    /// <summary>Hangul syllables and jamo.</summary>
    /// <param name="cp">The code point.</param>
    /// <returns><see langword="true"/> for Hangul.</returns>
    protected static bool IsHangul(int cp)
        => (cp >= 0xAC00 && cp <= 0xD7A3) || (cp >= 0x1100 && cp <= 0x11FF) || (cp >= 0x3131 && cp <= 0x318E) || (cp >= 0xA960 && cp <= 0xA97F)
            || (cp >= 0xD7B0 && cp <= 0xD7FF) || (cp >= 0xFFA0 && cp <= 0xFFDC);
}

/// <summary>
/// Tokenizer for Japanese: Han and kana spans are segmented by lattice search over a dictionary, with a length penalty that splits long
/// compounds (search mode). Particles, auxiliary verbs and symbols are dropped by default (leaving position gaps), and inflected
/// words can be replaced by their dictionary form.
/// </summary>
public sealed class JapaneseTokenizer : AsianTokenizer
{
    private readonly LatticeSegmenter _segmenter;
    private readonly bool _baseForm;
    private readonly bool _dropFunctionWords;

    /// <summary>Creates the tokenizer.</summary>
    /// <param name="dictionary">The dictionary; the packed one when <see langword="null"/>.</param>
    /// <param name="options">Length penalties; <see cref="LatticeOptions.Search"/> when <see langword="null"/>.</param>
    /// <param name="baseForm">Replace inflected words by their dictionary form (for example the stem of a verb ending). Default <see langword="true"/>.</param>
    /// <param name="dropFunctionWords">Drop particles, auxiliary verbs and symbols. Default <see langword="true"/>.</param>
    public JapaneseTokenizer(LatticeLexicon? dictionary = null, LatticeOptions? options = null, bool baseForm = true, bool dropFunctionWords = true)
    {
        _segmenter = new LatticeSegmenter(dictionary ?? AsianDictionaries.Japanese, options);
        _baseForm = baseForm;
        _dropFunctionWords = dropFunctionWords;
    }

    /// <inheritdoc/>
    protected override bool IsTarget(int codePoint) => IsHan(codePoint) || IsKana(codePoint);

    /// <inheritdoc/>
    protected override void SegmentSpan(ReadOnlySpan<char> span, List<Segment> segments) => _segmenter.Segment(span, segments);

    /// <inheritdoc/>
    protected override void Produce(ReadOnlySpan<char> span, IReadOnlyList<Segment> segments)
    {
        LatticeLexicon d = _segmenter.Dictionary;
        foreach (Segment s in segments)
        {
            string pos = d.PosName(s.Pos);
            if (_dropFunctionWords && (pos.StartsWith("助詞", StringComparison.Ordinal) || pos.StartsWith("助動詞", StringComparison.Ordinal) || pos.StartsWith("記号", StringComparison.Ordinal)))
            {
                Skip();
                continue;
            }

            string? term = null;
            if (_baseForm && s.Word >= 0)
            {
                string extra = d.Extra(s.Word);
                if (extra.Length > 0)
                {
                    term = extra;
                }
            }

            Emit(s.Start, s.Length, term, false);
        }
    }
}

/// <summary>
/// Tokenizer for Korean: Hangul and Hanja spans (between spaces) are segmented into morphemes by lattice search. Particles, endings and
/// symbols are dropped by default; an inflected form yields its content morphemes (its stem), and a compound noun yields the compound
/// followed by its parts at the same position.
/// </summary>
public sealed class KoreanTokenizer : AsianTokenizer
{
    private readonly LatticeSegmenter _segmenter;
    private readonly bool _dropFunctionWords;

    /// <summary>Creates the tokenizer.</summary>
    /// <param name="dictionary">The dictionary; the packed one when <see langword="null"/>.</param>
    /// <param name="dropFunctionWords">Drop particles, endings, suffixes and symbols. Default <see langword="true"/>.</param>
    public KoreanTokenizer(LatticeLexicon? dictionary = null, bool dropFunctionWords = true)
    {
        _segmenter = new LatticeSegmenter(dictionary ?? AsianDictionaries.Korean, LatticeOptions.Normal);
        _dropFunctionWords = dropFunctionWords;
    }

    /// <inheritdoc/>
    protected override bool IsTarget(int codePoint) => IsHangul(codePoint) || IsHan(codePoint);

    /// <inheritdoc/>
    protected override void SegmentSpan(ReadOnlySpan<char> span, List<Segment> segments) => _segmenter.Segment(span, segments);

    private static bool IsFunctionTag(string tag)
        => tag.Length > 0 && (tag[0] is 'E' or 'J' || tag is "SC" or "SE" or "SF" or "SP" or "SSO" or "SSC" or "SY" or "VCP" or "VCN" or "XPN" or "XSA" or "XSN" or "XSV");

    /// <inheritdoc/>
    protected override void Produce(ReadOnlySpan<char> span, IReadOnlyList<Segment> segments)
    {
        LatticeLexicon d = _segmenter.Dictionary;
        foreach (Segment s in segments)
        {
            string tag = d.PosName(s.Pos);
            string expression = s.Word >= 0 ? d.Extra(s.Word) : string.Empty;
            if (expression.Length > 0)
            {
                bool compound = tag.IndexOf('+') < 0;
                bool first = true;
                if (compound)
                {
                    Emit(s.Start, s.Length, null, false);
                    first = false;
                }

                foreach (string part in expression.Split('+'))
                {
                    int slash = part.IndexOf('/');
                    int slash2 = slash < 0 ? -1 : part.IndexOf('/', slash + 1);
                    if (slash <= 0 || slash2 < 0)
                    {
                        continue;
                    }

                    string surface = part[..slash];
                    string morphemeTag = part[(slash + 1)..slash2];
                    if (_dropFunctionWords && IsFunctionTag(morphemeTag))
                    {
                        if (!compound)
                        {
                            Skip();
                        }

                        continue;
                    }

                    Emit(s.Start, s.Length, surface, !first);
                    first = false;
                }

                continue;
            }

            if (_dropFunctionWords && IsFunctionTag(tag))
            {
                Skip();
                continue;
            }

            Emit(s.Start, s.Length, null, false);
        }
    }
}

/// <summary>Tokenizer for Chinese: Han spans are segmented by the most probable path through a frequency dictionary.</summary>
public sealed class ChineseTokenizer : AsianTokenizer
{
    private readonly UnigramSegmenter _segmenter;

    /// <summary>Creates the tokenizer.</summary>
    /// <param name="dictionary">The word list; the packed one when <see langword="null"/>.</param>
    /// <param name="searchMode">Stack the parts of long words as extra tokens. Default <see langword="true"/>.</param>
    public ChineseTokenizer(WordList? dictionary = null, bool searchMode = true)
    {
        _segmenter = new UnigramSegmenter(dictionary ?? AsianDictionaries.Chinese, searchMode);
    }

    /// <inheritdoc/>
    protected override bool IsTarget(int codePoint) => IsHan(codePoint);

    /// <inheritdoc/>
    protected override void SegmentSpan(ReadOnlySpan<char> span, List<Segment> segments) => _segmenter.Segment(span, segments);

    /// <inheritdoc/>
    protected override void Produce(ReadOnlySpan<char> span, IReadOnlyList<Segment> segments)
    {
        foreach (Segment s in segments)
        {
            Emit(s.Start, s.Length, null, s.Stacked);
        }
    }
}

/// <summary>Tokenizer for Thai, Lao, Khmer and Myanmar: spans of the script are broken into words by maximal matching over a word list.</summary>
public sealed class WordBreakTokenizer : AsianTokenizer
{
    private readonly MaximalMatchSegmenter _segmenter;
    private readonly int _from, _to, _from2, _to2;

    /// <summary>Creates the tokenizer.</summary>
    /// <param name="dictionary">The word list.</param>
    /// <param name="script">The script whose spans are broken: Thai, Lao, Khmer or Myanmar.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="script"/> is not one of the four.</exception>
    public WordBreakTokenizer(WordList dictionary, WordBreakScript script)
    {
        _segmenter = new MaximalMatchSegmenter(dictionary);
        (_from, _to, _from2, _to2) = script switch
        {
            WordBreakScript.Thai => (0x0E01, 0x0E7F, 0, -1),
            WordBreakScript.Lao => (0x0E81, 0x0EDF, 0, -1),
            WordBreakScript.Khmer => (0x1780, 0x17FF, 0x19E0, 0x19FF),
            WordBreakScript.Myanmar => (0x1000, 0x109F, 0xA9E0, 0xAA7F),
            _ => throw new ArgumentOutOfRangeException(nameof(script)),
        };
    }

    /// <inheritdoc/>
    protected override bool IsTarget(int codePoint) => (codePoint >= _from && codePoint <= _to) || (codePoint >= _from2 && codePoint <= _to2);

    /// <inheritdoc/>
    protected override void SegmentSpan(ReadOnlySpan<char> span, List<Segment> segments) => _segmenter.Segment(span, segments);

    /// <inheritdoc/>
    protected override void Produce(ReadOnlySpan<char> span, IReadOnlyList<Segment> segments)
    {
        foreach (Segment s in segments)
        {
            // Digits and signs inside a span (Thai digits, Khmer digits) are numbers, not words, but they stay as terms.
            Emit(s.Start, s.Length, null, false);
        }
    }
}

/// <summary>The script a <see cref="WordBreakTokenizer"/> breaks.</summary>
public enum WordBreakScript
{
    /// <summary>Thai (U+0E01 to U+0E7F).</summary>
    Thai,

    /// <summary>Lao (U+0E81 to U+0EDF).</summary>
    Lao,

    /// <summary>Khmer (U+1780 to U+17FF and the symbols block).</summary>
    Khmer,

    /// <summary>Myanmar (Burmese; U+1000 to U+109F and the extended blocks).</summary>
    Myanmar,
}

/// <summary>Japanese analyzer, registered as <c>ja</c> by <see cref="AsianAnalyzers.Register"/>: <see cref="JapaneseTokenizer"/>, NFKC, case folding.</summary>
public sealed class JapaneseAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "ja";

    private readonly LatticeLexicon? _dictionary;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="dictionary">The dictionary; the packed one when <see langword="null"/>.</param>
    public JapaneseAnalyzer(LatticeLexicon? dictionary = null) => _dictionary = dictionary;

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new CaseFoldFilter(new NormalizationFilter(new JapaneseTokenizer(_dictionary), compatibility: true));
}

/// <summary>Korean analyzer, registered as <c>ko</c>: <see cref="KoreanTokenizer"/>, NFKC, case folding.</summary>
public sealed class KoreanAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "ko";

    private readonly LatticeLexicon? _dictionary;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="dictionary">The dictionary; the packed one when <see langword="null"/>.</param>
    public KoreanAnalyzer(LatticeLexicon? dictionary = null) => _dictionary = dictionary;

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new CaseFoldFilter(new NormalizationFilter(new KoreanTokenizer(_dictionary), compatibility: true));
}

/// <summary>Chinese analyzer, registered as <c>zh</c>: <see cref="ChineseTokenizer"/>, NFKC, case folding.</summary>
public sealed class ChineseAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "zh";

    private readonly WordList? _dictionary;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="dictionary">The word list; the packed one when <see langword="null"/>.</param>
    public ChineseAnalyzer(WordList? dictionary = null) => _dictionary = dictionary;

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new CaseFoldFilter(new NormalizationFilter(new ChineseTokenizer(_dictionary), compatibility: true));
}

/// <summary>Analyzer for Thai (<c>th</c>), Lao (<c>lo</c>), Khmer (<c>km</c>) and Myanmar (<c>my</c>): <see cref="WordBreakTokenizer"/>, NFKC, case folding.</summary>
public sealed class WordBreakAnalyzer : Analyzer
{
    internal const string ThaiName = "th", LaoName = "lo", KhmerName = "km", MyanmarName = "my";

    private readonly WordList? _dictionary;
    private readonly WordBreakScript _script;

    /// <summary>Creates the analyzer.</summary>
    /// <param name="script">The script.</param>
    /// <param name="dictionary">The word list; the packed one for the script when <see langword="null"/>.</param>
    public WordBreakAnalyzer(WordBreakScript script, WordList? dictionary = null)
    {
        _script = script;
        _dictionary = dictionary;
        Name = script switch { WordBreakScript.Thai => ThaiName, WordBreakScript.Lao => LaoName, WordBreakScript.Khmer => KhmerName, _ => MyanmarName };
    }

    /// <summary>Thai analyzer (<c>th</c>).</summary>
    /// <returns>A new analyzer.</returns>
    public static WordBreakAnalyzer Thai() => new(WordBreakScript.Thai);

    /// <summary>Lao analyzer (<c>lo</c>).</summary>
    /// <returns>A new analyzer.</returns>
    public static WordBreakAnalyzer Lao() => new(WordBreakScript.Lao);

    /// <summary>Khmer analyzer (<c>km</c>).</summary>
    /// <returns>A new analyzer.</returns>
    public static WordBreakAnalyzer Khmer() => new(WordBreakScript.Khmer);

    /// <summary>Myanmar analyzer (<c>my</c>).</summary>
    /// <returns>A new analyzer.</returns>
    public static WordBreakAnalyzer Myanmar() => new(WordBreakScript.Myanmar);

    /// <inheritdoc/>
    public override string Name { get; }

    /// <inheritdoc/>
    protected override TokenSource CreateStream()
    {
        WordList d = _dictionary ?? _script switch
        {
            WordBreakScript.Thai => AsianDictionaries.Thai,
            WordBreakScript.Lao => AsianDictionaries.Lao,
            WordBreakScript.Khmer => AsianDictionaries.Khmer,
            _ => AsianDictionaries.Myanmar,
        };
        return new CaseFoldFilter(new NormalizationFilter(new WordBreakTokenizer(d, _script), compatibility: true));
    }
}
