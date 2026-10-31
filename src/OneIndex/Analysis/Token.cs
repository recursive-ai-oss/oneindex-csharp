namespace OneIndex.Analysis;

/// <summary>A coarse classification a tokenizer gives a token, which filters may use (for example to leave numbers unstemmed).</summary>
public enum TokenType : byte
{
    /// <summary>Anything not covered below.</summary>
    Other = 0,

    /// <summary>A word of letters, possibly with digits.</summary>
    Word,

    /// <summary>A number.</summary>
    Number,

    /// <summary>An ideographic character or run (CJK).</summary>
    Ideographic,

    /// <summary>A token added by a filter at the position of another (a synonym, a stemmed twin).</summary>
    Synonym,
}

/// <summary>Per-token marks that filters set and read.</summary>
[Flags]
public enum TokenMarks : byte
{
    /// <summary>No marks.</summary>
    None = 0,

    /// <summary>The term must not be stemmed or otherwise rewritten.</summary>
    Keyword = 1,
}

/// <summary>
/// The one mutable record that moves along an analysis chain. A tokenizer fills it; each filter reads and edits it in place, so a
/// chain allocates nothing per token once its buffer has grown to the longest term.
/// <para>
/// The term is UTF-16 text, because .NET strings are UTF-16: offsets index the original string directly (highlighting, snippets),
/// case and accent folding edit characters in place, and the one conversion to UTF-8 happens once, where a term enters the
/// term dictionary.
/// </para>
/// <para>
/// Positions: the first token's position is its <see cref="PositionIncrement"/> minus 1, so an increment of 1 gives position 0;
/// each later token's position is the previous one's plus its own increment. An increment of 0 stacks the token on the previous
/// position (a synonym); an increment above 1 leaves a gap (a removed stop word).
/// </para>
/// </summary>
public sealed class Token
{
    private char[] _buffer = new char[32];
    private int _length;

    /// <summary>The term text. Valid until the stream is advanced or the term is changed.</summary>
    public ReadOnlySpan<char> Term => _buffer.AsSpan(0, _length);

    /// <summary>How far this token's position is from the previous token's. Default 1.</summary>
    public int PositionIncrement { get; set; } = 1;

    /// <summary>The index in the original text of the term's first character.</summary>
    public int StartOffset { get; set; }

    /// <summary>The index in the original text just past the term's last character; at least <see cref="StartOffset"/>.</summary>
    public int EndOffset { get; set; }

    /// <summary>The token's classification.</summary>
    public TokenType Type { get; set; }

    /// <summary>The token's marks.</summary>
    public TokenMarks Marks { get; set; }

    /// <summary>Replaces the term.</summary>
    /// <param name="term">The new text; it may overlap the current term.</param>
    public void SetTerm(ReadOnlySpan<char> term)
    {
        if (term.Length > _buffer.Length)
        {
            Grow(term.Length, preserve: false);
        }

        term.CopyTo(_buffer);
        _length = term.Length;
    }

    /// <summary>Sets the term to <paramref name="length"/> characters and returns them for the caller to fill or edit in place.</summary>
    /// <param name="length">The new term length; characters beyond the old length are unspecified.</param>
    /// <returns>The term's characters.</returns>
    public Span<char> ResizeTerm(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (length > _buffer.Length)
        {
            Grow(length, preserve: true);
        }

        _length = length;
        return _buffer.AsSpan(0, length);
    }

    /// <summary>Returns the token to its starting state: empty term, increment 1, offsets 0, no type or marks.</summary>
    public void Clear()
    {
        _length = 0;
        PositionIncrement = 1;
        StartOffset = 0;
        EndOffset = 0;
        Type = TokenType.Other;
        Marks = TokenMarks.None;
    }

    private void Grow(int needed, bool preserve)
    {
        var bigger = new char[Math.Max(needed, _buffer.Length * 2)];
        if (preserve)
        {
            _buffer.AsSpan(0, _length).CopyTo(bigger);
        }

        _buffer = bigger;
    }
}

/// <summary>A token copied out of a stream, for tests, debugging and tools (the stream itself allocates nothing).</summary>
/// <param name="Term">The term.</param>
/// <param name="Position">The absolute position (see <see cref="Token"/>).</param>
/// <param name="StartOffset">Start offset in the original text.</param>
/// <param name="EndOffset">End offset in the original text.</param>
/// <param name="Type">The classification.</param>
/// <param name="Marks">The marks.</param>
public readonly record struct AnalyzedToken(string Term, int Position, int StartOffset, int EndOffset, TokenType Type, TokenMarks Marks);
