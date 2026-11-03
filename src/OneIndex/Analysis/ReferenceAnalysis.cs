namespace OneIndex.Analysis;

/// <summary>Splits text at white space (<see cref="char.IsWhiteSpace(char)"/>). A reference tokenizer: Unicode word boundaries come later.</summary>
public sealed class WhitespaceTokenizer : Tokenizer
{
    private ReadOnlyMemory<char> _text;
    private int _position;

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        _text = text;
        _position = 0;
        Token.Clear();
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        ReadOnlySpan<char> text = _text.Span;
        int i = _position;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        if (i >= text.Length)
        {
            _position = text.Length;
            return false;
        }

        int start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        _position = i;
        Token t = Token;
        t.Clear();
        t.SetTerm(text[start..i]);
        t.StartOffset = start;
        t.EndOffset = i;
        t.Type = TokenType.Word;
        return true;
    }
}

/// <summary>Emits the whole text as one token, for identifiers and other exact-match fields. Empty text gives no token.</summary>
public sealed class KeywordTokenizer : Tokenizer
{
    private ReadOnlyMemory<char> _text;
    private bool _done;

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        _text = text;
        _done = false;
        Token.Clear();
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (_done || _text.IsEmpty)
        {
            return false;
        }

        _done = true;
        Token t = Token;
        t.Clear();
        t.SetTerm(_text.Span);
        t.EndOffset = _text.Length;
        t.Type = TokenType.Other;
        return true;
    }
}

/// <summary>Lower-cases each term with the invariant culture, character by character. A reference filter: full case folding comes later.</summary>
/// <param name="input">The stream to read from.</param>
public sealed class LowerCaseFilter(TokenSource input) : TokenFilter(input)
{
    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Span<char> term = Token.ResizeTerm(Token.Term.Length);
        for (int i = 0; i < term.Length; i++)
        {
            term[i] = char.ToLowerInvariant(term[i]);
        }

        return true;
    }
}

/// <summary>The reference analyzer: split at white space, lower-case. Registered as <c>whitespace-lowercase</c>.</summary>
public sealed class WhitespaceLowercaseAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "whitespace-lowercase";

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new LowerCaseFilter(new WhitespaceTokenizer());
}

/// <summary>Treats the whole text as one term. Registered as <c>keyword</c>.</summary>
public sealed class KeywordAnalyzer : Analyzer
{
    /// <summary>The registered name.</summary>
    public const string RegisteredName = "keyword";

    /// <inheritdoc/>
    public override string Name => RegisteredName;

    /// <inheritdoc/>
    protected override TokenSource CreateStream() => new KeywordTokenizer();
}

/// <summary>
/// The analyzers this build knows, by the names schemas store. The list is a plain <c>switch</c> and a fixed table, not discovered by
/// reflection, so trimming and AOT keep exactly what is named here. The names are: <c>standard</c> (the default), <c>cjk</c>,
/// <c>keyword</c>, <c>whitespace-lowercase</c>, one per language code (<see cref="LanguageAnalyzer.LanguageCodes"/>), the
/// language code plus <c>-both</c>, and for the languages with a packed word list (<see cref="CompoundVocabulary.PackedLanguages"/>) the
/// code plus <c>-compound</c> (compounds also indexed as their parts), with or without <c>-both</c>.
/// </summary>
public static class AnalyzerRegistry
{
    /// <summary>Creates a new analyzer instance by name.</summary>
    /// <param name="name">The analyzer name.</param>
    /// <param name="analyzer">The new instance when found.</param>
    /// <returns><see langword="true"/> when the name is registered.</returns>
    public static bool TryCreate(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Analyzer? analyzer)
    {
        Func<Analyzer>? registered = null;
        if (Volatile.Read(ref s_registered).TryGetValue(name, out registered))
        {
            analyzer = registered();
            return true;
        }

        analyzer = name switch
        {
            StandardAnalyzer.RegisteredName => new StandardAnalyzer(),
            CjkAnalyzer.RegisteredName => new CjkAnalyzer(),
            WhitespaceLowercaseAnalyzer.RegisteredName => new WhitespaceLowercaseAnalyzer(),
            KeywordAnalyzer.RegisteredName => new KeywordAnalyzer(),
            _ => TryCreateLanguage(name),
        };
        return analyzer is not null;
    }

    private static IReadOnlyDictionary<string, Func<Analyzer>> s_registered = new Dictionary<string, Func<Analyzer>>(StringComparer.Ordinal);

    /// <summary>
    /// Makes an analyzer from another assembly (for example the Asian-language package) known by name, so a schema that stores that name
    /// opens. Registration is explicit, one call at start-up, never discovered by reflection. Registering the same name again replaces
    /// the factory; built-in names cannot be replaced (they are looked up after registered names only when not built in).
    /// </summary>
    /// <param name="name">The analyzer name the schema stores; it must not be empty.</param>
    /// <param name="factory">Creates a new analyzer instance on each call; each thread gets its own.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or is a built-in analyzer name.</exception>
    public static void Register(string name, Func<Analyzer> factory)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(factory);
        if (name is StandardAnalyzer.RegisteredName or CjkAnalyzer.RegisteredName or WhitespaceLowercaseAnalyzer.RegisteredName or KeywordAnalyzer.RegisteredName
            || TryCreateLanguage(name) is not null)
        {
            throw new ArgumentException($"'{name}' is a built-in analyzer name.", nameof(name));
        }

        lock (s_registerLock)
        {
            var copy = new Dictionary<string, Func<Analyzer>>(s_registered, StringComparer.Ordinal) { [name] = factory };
            Volatile.Write(ref s_registered, copy);
        }
    }

    private static readonly object s_registerLock = new();

    /// <summary>Creates a new analyzer instance by name.</summary>
    /// <param name="name">The analyzer name.</param>
    /// <returns>A new instance; each thread should have its own.</returns>
    /// <exception cref="ArgumentException">No analyzer has that name.</exception>
    public static Analyzer Create(string name)
        => TryCreate(name, out Analyzer? analyzer) ? analyzer : throw new ArgumentException($"No analyzer named '{name}' is registered.", nameof(name));

    private static LanguageAnalyzer? TryCreateLanguage(string name)
    {
        bool both = name.EndsWith("-both", StringComparison.Ordinal);
        string code = both ? name[..^5] : name;
        bool compound = code.EndsWith("-compound", StringComparison.Ordinal);
        code = compound ? code[..^9] : code;
        foreach (string known in LanguageAnalyzer.LanguageCodes)
        {
            if (known == code)
            {
                return compound && !CompoundVocabulary.PackedLanguages.Contains(code) ? null : new LanguageAnalyzer(code, both, null, compound);
            }
        }

        return null;
    }
}
