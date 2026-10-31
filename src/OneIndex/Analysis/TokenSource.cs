namespace OneIndex.Analysis;

/// <summary>
/// A pull stream of tokens over one text. Call <see cref="Reset"/> with the text, then <see cref="MoveNext"/> until it returns
/// <see langword="false"/>, reading <see cref="Token"/> after each success. The same <see cref="Token"/> object is returned for
/// every token, so copy what you need before advancing. A stream is not thread-safe and is reused for many texts.
/// </summary>
public abstract class TokenSource
{
    /// <summary>The current token; its contents are valid after <see cref="MoveNext"/> returned <see langword="true"/>.</summary>
    public abstract Token Token { get; }

    /// <summary>Starts a new text, discarding any state of the previous one.</summary>
    /// <param name="text">The text to tokenize; it must stay unchanged while the stream is read.</param>
    public abstract void Reset(ReadOnlyMemory<char> text);

    /// <summary>Advances to the next token.</summary>
    /// <returns><see langword="true"/> when <see cref="Token"/> holds a new token; <see langword="false"/> at the end of the text.</returns>
    public abstract bool MoveNext();
}

/// <summary>The first link of a chain: it splits text into tokens and owns the <see cref="Token"/> the chain shares.</summary>
public abstract class Tokenizer : TokenSource
{
    private readonly Token _token = new();

    /// <inheritdoc/>
    public sealed override Token Token => _token;
}

/// <summary>
/// A later link of a chain: it reads tokens from <see cref="Input"/>, edits, drops or adds tokens, and passes them on. A filter
/// that drops a token adds its increment to the next token's, so positions stay honest.
/// </summary>
/// <param name="input">The stream to read from.</param>
public abstract class TokenFilter(TokenSource input) : TokenSource
{
    /// <summary>The upstream stream.</summary>
    protected TokenSource Input { get; } = input ?? throw new ArgumentNullException(nameof(input));

    /// <inheritdoc/>
    public sealed override Token Token => Input.Token;

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text) => Input.Reset(text);
}

/// <summary>
/// Turns text into terms: a tokenizer followed by filters, built once and reused. An analyzer instance is not thread-safe; each
/// thread asks <see cref="AnalyzerRegistry"/> for its own. The <see cref="Name"/> is what a schema stores to find the analyzer
/// again, so it never changes for a given behaviour.
/// </summary>
public abstract class Analyzer
{
    private TokenSource? _stream;

    /// <summary>The stable name stored in schemas.</summary>
    public abstract string Name { get; }

    /// <summary>Tokenizes <paramref name="text"/>.</summary>
    /// <param name="text">The text; it must stay unchanged while the stream is read.</param>
    /// <returns>The analyzer's reusable stream, already started on the text. The previous stream contents are gone.</returns>
    public TokenSource GetTokens(ReadOnlyMemory<char> text)
    {
        _stream ??= CreateStream();
        _stream.Reset(text);
        return _stream;
    }

    /// <summary>Builds the chain; called once per analyzer instance.</summary>
    /// <returns>The last link of the chain.</returns>
    protected abstract TokenSource CreateStream();
}

/// <summary>An analyzer assembled from a tokenizer factory and filter factories.</summary>
/// <param name="name">The stable name.</param>
/// <param name="tokenizer">Creates the tokenizer.</param>
/// <param name="filters">Each wraps the stream so far, in order.</param>
public sealed class CustomAnalyzer(string name, Func<Tokenizer> tokenizer, params Func<TokenSource, TokenSource>[] filters) : Analyzer
{
    private readonly Func<Tokenizer> _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
    private readonly Func<TokenSource, TokenSource>[] _filters = filters ?? throw new ArgumentNullException(nameof(filters));

    /// <inheritdoc/>
    public override string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    /// <inheritdoc/>
    protected override TokenSource CreateStream()
    {
        TokenSource stream = _tokenizer();
        foreach (Func<TokenSource, TokenSource> filter in _filters)
        {
            stream = filter(stream);
        }

        return stream;
    }
}

/// <summary>Helpers that copy tokens out of a stream; allocating, for tests and tools.</summary>
public static class AnalyzerExtensions
{
    /// <summary>Runs <paramref name="analyzer"/> over <paramref name="text"/> and copies every token.</summary>
    /// <param name="analyzer">The analyzer.</param>
    /// <param name="text">The text.</param>
    /// <returns>The tokens in order, with absolute positions.</returns>
    public static IReadOnlyList<AnalyzedToken> Analyze(this Analyzer analyzer, string text)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(text);
        var result = new List<AnalyzedToken>();
        TokenSource stream = analyzer.GetTokens(text.AsMemory());
        int position = -1;
        while (stream.MoveNext())
        {
            Token t = stream.Token;
            position += t.PositionIncrement;
            result.Add(new AnalyzedToken(t.Term.ToString(), position, t.StartOffset, t.EndOffset, t.Type, t.Marks));
        }

        return result;
    }
}
