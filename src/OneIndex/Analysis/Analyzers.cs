using OneIndex.Analysis.Snowball;

namespace OneIndex.Analysis;

/// <summary>
/// The default analyzer, registered as <c>standard</c>: Unicode word boundaries (UAX #29), then full case folding. No stop words,
/// no stemming, no accent folding, so terms are the words of the text, lower-cased. Tokens longer than 255 characters are split.
/// </summary>
public sealed class StandardAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "standard";

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new CaseFoldFilter(new StandardTokenizer());
}

/// <summary>
/// For Chinese, Japanese and Korean text without a dictionary, registered as <c>cjk</c>: width and compatibility forms are
/// normalized (NFKC), Han, Hiragana, Katakana and Hangul runs become overlapping bigrams, other words stay whole, and everything is
/// case-folded. Dictionary segmentation is a separate package.
/// </summary>
public sealed class CjkAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "cjk";

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream()
        => new CaseFoldFilter(new NormalizationFilter(new StandardTokenizer(cjkBigrams: true), compatibility: true));
}

/// <summary>
/// An analyzer for one language, registered under its ISO 639-1 code (<c>en</c>, <c>de</c>, <c>fr</c> ...) or the code plus
/// <c>-both</c>. The chain is: UAX #29 words, case folding, then (where the language has them) apostrophe normalization, elision,
/// the stop words Snowball publishes, the Snowball stemmer, and accent folding. Accent folding runs after stemming because the
/// stemming algorithms read accented letters; it applies to the Latin-script languages whose accents readers drop when typing
/// (English folds to plain ASCII).
/// <para>
/// With <c>-both</c> every stemmed word is indexed twice at one position: as written, and as its stem (marked
/// <see cref="TokenMarks.Stemmed"/>), so exact matches can rank above stem matches. Without it, only the stem is produced.
/// </para>
/// </summary>
public sealed class LanguageAnalyzer : Analyzer
{
    private static readonly (string Code, string Language)[] Table =
    [
        ("ar", "arabic"), ("ca", "catalan"), ("cs", "czech"), ("da", "danish"), ("de", "german"), ("el", "greek"), ("en", "english"),
        ("eo", "esperanto"), ("es", "spanish"), ("et", "estonian"), ("eu", "basque"), ("fa", "persian"), ("fi", "finnish"),
        ("fr", "french"), ("ga", "irish"), ("hi", "hindi"), ("hu", "hungarian"), ("hy", "armenian"), ("id", "indonesian"),
        ("it", "italian"), ("lt", "lithuanian"), ("ne", "nepali"), ("nl", "dutch"), ("no", "norwegian"), ("pl", "polish"),
        ("pt", "portuguese"), ("ro", "romanian"), ("ru", "russian"), ("sr", "serbian"), ("st", "sesotho"), ("sv", "swedish"),
        ("ta", "tamil"), ("tr", "turkish"), ("yi", "yiddish"),
    ];

    private readonly string _code;
    private readonly string _language;
    private readonly bool _both;
    private readonly bool _decompound;
    private readonly StopWordSettings? _stopSettings;

    /// <summary>Creates an analyzer.</summary>
    /// <param name="languageCode">An ISO 639-1 code from <see cref="LanguageCodes"/>.</param>
    /// <param name="keepUnstemmed">Whether to index the unstemmed word beside its stem (the <c>-both</c> form).</param>
    /// <param name="stopWords">A per-index stop word configuration that replaces or extends the language's list; <see langword="null"/> (default) keeps the language's own list. The analyzer's <see cref="Name"/> does not change: the settings are stored with the index, not in the name.</param>
    /// <param name="decompound">Whether to also index the parts of compound words (the <c>-compound</c> form); needs a language in <see cref="CompoundVocabulary.PackedLanguages"/>.</param>
    /// <exception cref="ArgumentException">The code is not supported, or <paramref name="decompound"/> is set for a language without a packed word list.</exception>
    public LanguageAnalyzer(string languageCode, bool keepUnstemmed = false, StopWordSettings? stopWords = null, bool decompound = false)
    {
        ArgumentNullException.ThrowIfNull(languageCode);
        string? language = null;
        foreach ((string code, string name) in Table)
        {
            if (code == languageCode)
            {
                language = name;
            }
        }

        _language = language ?? throw new ArgumentException($"No language analyzer for '{languageCode}'.", nameof(languageCode));
        _code = languageCode;
        _both = keepUnstemmed;
        _stopSettings = stopWords;
        if (decompound && !CompoundVocabulary.PackedLanguages.Contains(languageCode))
        {
            throw new ArgumentException($"There is no decompounding word list for '{languageCode}'.", nameof(decompound));
        }

        _decompound = decompound;
    }

    /// <summary>The ISO 639-1 codes that have an analyzer, in alphabetical order.</summary>
    public static IReadOnlyList<string> LanguageCodes { get; } = Table.Select(t => t.Code).ToArray();

    /// <inheritdoc/>
    public override string Name => _code + (_decompound ? "-compound" : string.Empty) + (_both ? "-both" : string.Empty);

    /// <inheritdoc/>
    protected override TokenSource CreateStream()
    {
        TokenSource stream = new StandardTokenizer();
        if (_code == "tr")
        {
            stream = new TurkishCaseFilter(stream);
        }

        stream = new CaseFoldFilter(stream);
        stream = new ApostropheFilter(stream);
        if (_decompound)
        {
            // Compounds are split before stop words and stemming, so each part is stemmed like any other word.
            stream = new DecompoundFilter(stream, CompoundSplitter.ForLanguage(_code));
        }

        StopWords? elision = _code switch
        {
            "fr" => ElisionFilter.French,
            "it" => ElisionFilter.Italian,
            "ca" => ElisionFilter.Catalan,
            _ => null,
        };
        if (elision is not null)
        {
            stream = new ElisionFilter(stream, elision);
        }

        if (_stopSettings is not null)
        {
            stream = _stopSettings.CreateFilter(stream, _code);
        }
        else if (StopWords.ForLanguage(_code) is { } stopWords)
        {
            stream = new StopFilter(stream, stopWords);
        }

        _ = Stemmers.TryCreate(_language, out Stemmer? stemmer);
        stream = new StemFilter(stream, stemmer!, _both ? StemOutput.Both : StemOutput.Replace);
        return _code switch
        {
            "en" => new AsciiFoldFilter(stream),
            "fr" or "es" or "it" or "pt" or "ca" or "nl" or "ro" => new AccentFoldFilter(stream),
            _ => stream,
        };
    }
}
