using OneIndex.Analysis.Snowball;
using OneIndex.Analysis.Unicode;

namespace OneIndex.Analysis;

/// <summary>
/// Applies full Unicode case folding (CaseFolding.txt statuses C and F, Unicode 17.0.0) to each term: "Straße" becomes "strasse".
/// Folding does not depend on the culture or the platform. A term can grow to three times its length.
/// </summary>
/// <param name="input">The stream to read from.</param>
public sealed class CaseFoldFilter(TokenSource input) : TokenFilter(input)
{
    private char[] _scratch = new char[64];

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Token t = Token;
        if (CaseFolder.TryFoldAsciiInPlace(t.ResizeTerm(t.Term.Length)))
        {
            return true;
        }

        ReadOnlySpan<char> term = t.Term;
        int needed = term.Length * CaseFolder.MaxExpansion;
        if (_scratch.Length < needed)
        {
            _scratch = new char[Math.Max(needed, _scratch.Length * 2)];
        }

        int n = CaseFolder.Fold(term, _scratch);
        t.SetTerm(_scratch.AsSpan(0, n));
        return true;
    }
}

/// <summary>
/// Normalizes each term to Unicode NFC, or to NFKC when <c>compatibility</c> is set (NFKC also maps ligatures, full-width and
/// half-width forms, superscripts and similar variants to their plain forms). Normalization uses OneIndex's own tables
/// (Unicode 17.0.0), not the platform's, so it works the same in the browser and with invariant globalization.
/// </summary>
/// <param name="input">The stream to read from.</param>
/// <param name="compatibility"><see langword="true"/> for NFKC, <see langword="false"/> (the default) for NFC.</param>
public sealed class NormalizationFilter(TokenSource input, bool compatibility = false) : TokenFilter(input)
{
    private readonly Normalizer _normalizer = new();
    private readonly UnicodeForm _form = compatibility ? UnicodeForm.Nfkc : UnicodeForm.Nfc;
    private char[] _scratch = new char[64];

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Token t = Token;
        if (System.Text.Ascii.IsValid(t.Term))
        {
            return true; // ASCII is already in every normalization form
        }

        int written;
        while (!_normalizer.TryNormalize(t.Term, _form, _scratch, out written))
        {
            _scratch = new char[_scratch.Length * 2];
        }

        t.SetTerm(_scratch.AsSpan(0, written));
        return true;
    }
}

/// <summary>
/// Removes accents: "café" becomes "cafe", "Ελλάδα" becomes "Ελλαδα". Text is decomposed, the combining diacritical marks are dropped
/// and the rest is composed again, so letters that merely look accented to a reader of another language (Hangul, kana) are untouched.
/// </summary>
/// <param name="input">The stream to read from.</param>
public sealed class AccentFoldFilter(TokenSource input) : TokenFilter(input)
{
    private readonly Normalizer _normalizer = new();
    private char[] _scratch = new char[64];

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Token t = Token;
        if (System.Text.Ascii.IsValid(t.Term))
        {
            return true;
        }

        int written;
        while (!_normalizer.TryFoldAccents(t.Term, _scratch, out written))
        {
            _scratch = new char[_scratch.Length * 2];
        }

        t.SetTerm(_scratch.AsSpan(0, written));
        return true;
    }
}

/// <summary>
/// Folds each term towards plain ASCII: accents are dropped, compatibility forms (ligatures "ﬁ", full-width letters) are
/// expanded, and Latin letters with no decomposition are spelled out ("ß" to "ss", "æ" to "ae", "ø" to "o", "ł" to "l").
/// Characters with no ASCII form (Greek, Cyrillic, CJK) are left as they are.
/// </summary>
/// <param name="input">The stream to read from.</param>
public sealed class AsciiFoldFilter(TokenSource input) : TokenFilter(input)
{
    private readonly Normalizer _normalizer = new();
    private char[] _scratch = new char[64];

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Token t = Token;
        if (System.Text.Ascii.IsValid(t.Term))
        {
            return true;
        }

        int written;
        while (!_normalizer.TryFoldToAscii(t.Term, _scratch, out written))
        {
            _scratch = new char[_scratch.Length * 2];
        }

        t.SetTerm(_scratch.AsSpan(0, written));
        return true;
    }
}

/// <summary>
/// Turns typographic apostrophes (U+2018, U+2019, U+02BC, U+FF07) into the ASCII apostrophe, so "don’t" and "don't" are one term and
/// the stop word lists, which use the ASCII form, match.
/// </summary>
/// <param name="input">The stream to read from.</param>
public sealed class ApostropheFilter(TokenSource input) : TokenFilter(input)
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
            if (term[i] is '\u2018' or '\u2019' or '\u02BC' or '\uFF07')
            {
                term[i] = '\'';
            }
        }

        return true;
    }
}

/// <summary>
/// Maps Turkish capital letters before case folding: "I" to dotless "ı" and "İ" to "i", which the generic fold would turn into
/// "i" and "i̇".
/// </summary>
/// <param name="input">The stream to read from.</param>
public sealed class TurkishCaseFilter(TokenSource input) : TokenFilter(input)
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
            if (term[i] == 'I')
            {
                term[i] = '\u0131';
            }
            else if (term[i] == '\u0130')
            {
                term[i] = 'i';
            }
        }

        return true;
    }
}

/// <summary>
/// A sorted set of words, searched without allocating. Words are compared exactly (ordinal); fold the case of the stream first.
/// </summary>
public sealed class StopWords
{
    private readonly string[] _sorted;

    /// <summary>Creates a set.</summary>
    /// <param name="words">The words; duplicates are fine.</param>
    public StopWords(IEnumerable<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);
        string[] sorted = [.. words.Distinct(StringComparer.Ordinal)];
        Array.Sort(sorted, StringComparer.Ordinal);
        _sorted = sorted;
    }

    private StopWords(string[] alreadySorted) => _sorted = alreadySorted;

    /// <summary>The number of words.</summary>
    public int Count => _sorted.Length;

    /// <summary>The words in ordinal order.</summary>
    public IReadOnlyList<string> Words => _sorted;

    /// <summary>A set with no words.</summary>
    public static StopWords Empty { get; } = new([]);

    /// <summary>Returns a set holding these words and <paramref name="more"/>.</summary>
    /// <param name="more">The words to add; duplicates are fine.</param>
    /// <returns>A new set; this one is unchanged.</returns>
    public StopWords With(IEnumerable<string> more)
    {
        ArgumentNullException.ThrowIfNull(more);
        return new StopWords(_sorted.Concat(more));
    }

    /// <summary>Returns the stop word list the Snowball project publishes for a language.</summary>
    /// <param name="language">An ISO 639-1 code as the language analyzers use: da, nl, en, fi, fr, de, hu, id, ga, it, no, pt, ru, st, es, sv.</param>
    /// <returns>The list, or <see langword="null"/> when none is published for the language.</returns>
    public static StopWords? ForLanguage(string language)
    {
        string[]? list = StopWordData.For(language);
        return list is null ? null : new StopWords(list);
    }

    /// <summary>Tests membership.</summary>
    /// <param name="word">The word.</param>
    /// <returns><see langword="true"/> when the set holds exactly this word.</returns>
    public bool Contains(ReadOnlySpan<char> word)
    {
        int lo = 0;
        int hi = _sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int c = word.CompareTo(_sorted[mid], StringComparison.Ordinal);
            if (c == 0)
            {
                return true;
            }

            if (c < 0)
            {
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return false;
    }
}

/// <summary>
/// Drops terms found in a stop word set. By default the position increment of a dropped term is added to the next term, so a
/// phrase across a removed word keeps its gap; with <c>preserveGaps</c> false the next term follows directly, as if the word had
/// never been in the text (stop words are then ignored inside phrases too).
/// </summary>
/// <param name="input">The stream to read from.</param>
/// <param name="stopWords">The words to drop.</param>
/// <param name="preserveGaps">Whether a dropped word leaves a gap in the positions. Default true.</param>
public sealed class StopFilter(TokenSource input, StopWords stopWords, bool preserveGaps = true) : TokenFilter(input)
{
    private readonly StopWords _stopWords = stopWords ?? throw new ArgumentNullException(nameof(stopWords));

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        int skipped = 0;
        while (Input.MoveNext())
        {
            if (_stopWords.Contains(Token.Term))
            {
                if (preserveGaps)
                {
                    skipped += Token.PositionIncrement;
                }

                continue;
            }

            Token.PositionIncrement += skipped;
            return true;
        }

        return false;
    }
}

/// <summary>Drops terms shorter than a minimum or longer than a maximum number of characters, keeping position gaps.</summary>
/// <param name="input">The stream to read from.</param>
/// <param name="minLength">The shortest term to keep. Default 1.</param>
/// <param name="maxLength">The longest term to keep. Default <see cref="int.MaxValue"/>.</param>
public sealed class LengthFilter(TokenSource input, int minLength = 1, int maxLength = int.MaxValue) : TokenFilter(input)
{
    /// <inheritdoc/>
    public override bool MoveNext()
    {
        int skipped = 0;
        while (Input.MoveNext())
        {
            int n = Token.Term.Length;
            if (n < minLength || n > maxLength)
            {
                skipped += Token.PositionIncrement;
                continue;
            }

            Token.PositionIncrement += skipped;
            return true;
        }

        return false;
    }
}

/// <summary>
/// Removes a leading elided article and its apostrophe: "l'avion" becomes "avion", "dell'arte" becomes "arte". Only the listed
/// articles are removed, and only when something is left.
/// </summary>
public sealed class ElisionFilter : TokenFilter
{
    private readonly StopWords _articles;

    /// <summary>Creates the filter.</summary>
    /// <param name="input">The stream to read from.</param>
    /// <param name="articles">The articles (without the apostrophe), in lower case.</param>
    public ElisionFilter(TokenSource input, StopWords articles)
        : base(input)
    {
        _articles = articles ?? throw new ArgumentNullException(nameof(articles));
    }

    /// <summary>The articles French elides before a vowel.</summary>
    public static StopWords French { get; } = new(["l", "m", "t", "qu", "n", "s", "j", "d", "c", "jusqu", "quoiqu", "lorsqu", "puisqu"]);

    /// <summary>The articles and prepositions Italian elides.</summary>
    public static StopWords Italian { get; } = new(["c", "l", "all", "dall", "dell", "nell", "sull", "coll", "pell", "gl", "agl", "dagl", "degl", "negl", "sugl", "un", "m", "t", "s", "v", "d"]);

    /// <summary>The articles Catalan elides.</summary>
    public static StopWords Catalan { get; } = new(["d", "l", "m", "n", "s", "t"]);

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        if (!Input.MoveNext())
        {
            return false;
        }

        Token t = Token;
        ReadOnlySpan<char> term = t.Term;
        int quote = term.IndexOf('\'');
        if (quote > 0 && quote < 8 && quote + 1 < term.Length && _articles.Contains(term[..quote]))
        {
            t.SetTerm(term[(quote + 1)..]);
            t.StartOffset += quote + 1;
        }

        return true;
    }
}

/// <summary>What a <see cref="StemFilter"/> does with the stem.</summary>
public enum StemOutput
{
    /// <summary>The stem replaces the word.</summary>
    Replace,

    /// <summary>
    /// The word stays, and its stem follows as a second term at the same position (increment 0), type <see cref="TokenType.Synonym"/>
    /// and marked <see cref="TokenMarks.Stemmed"/>. Nothing is added when the stem equals the word.
    /// </summary>
    Both,
}

/// <summary>
/// Stems words with a Snowball stemmer. Terms marked <see cref="TokenMarks.Keyword"/>, numbers and ideographic terms pass through
/// unchanged, and a stem is never empty (a word the algorithm would delete entirely is kept). The input must be lower-case, as the
/// algorithms expect.
/// </summary>
public sealed class StemFilter : TokenFilter
{
    private readonly Stemmer _stemmer;
    private readonly StemOutput _output;
    private char[] _pending = new char[32];
    private int _pendingLength;
    private bool _hasPending;

    /// <summary>Creates the filter.</summary>
    /// <param name="input">The stream to read from.</param>
    /// <param name="stemmer">The stemmer. It is used by this filter only.</param>
    /// <param name="output">Replace the word, or add the stem beside it.</param>
    public StemFilter(TokenSource input, Stemmer stemmer, StemOutput output = StemOutput.Replace)
        : base(input)
    {
        _stemmer = stemmer ?? throw new ArgumentNullException(nameof(stemmer));
        _output = output;
    }

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        base.Reset(text);
        _hasPending = false;
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        Token t = Token;
        if (_hasPending)
        {
            _hasPending = false;
            t.SetTerm(_pending.AsSpan(0, _pendingLength));
            t.PositionIncrement = 0;
            t.Type = TokenType.Synonym;
            t.Marks |= TokenMarks.Stemmed;
            return true;
        }

        if (!Input.MoveNext())
        {
            return false;
        }

        if (t.Type != TokenType.Word || (t.Marks & TokenMarks.Keyword) != 0)
        {
            return true;
        }

        ReadOnlySpan<char> stem = _stemmer.Stem(t.Term);
        if (stem.IsEmpty || stem.SequenceEqual(t.Term))
        {
            return true;
        }

        if (_output == StemOutput.Replace)
        {
            t.SetTerm(stem);
            return true;
        }

        if (_pending.Length < stem.Length)
        {
            _pending = new char[Math.Max(stem.Length, _pending.Length * 2)];
        }

        stem.CopyTo(_pending);
        _pendingLength = stem.Length;
        _hasPending = true;
        return true;
    }
}
