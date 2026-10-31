using OneIndex.Analysis.Unicode;

namespace OneIndex.Analysis;

/// <summary>
/// Splits text into words at the word boundaries of Unicode Standard Annex #29 (Unicode 17.0.0) and keeps the segments that
/// contain a letter, a digit or an ideograph. Punctuation, white space and symbols are dropped. A token's type is
/// <see cref="TokenType.Number"/> when it has digits only, <see cref="TokenType.Ideographic"/> when it is Han, Hiragana,
/// Katakana or Hangul text, and <see cref="TokenType.Word"/> otherwise.
/// <para>
/// Words longer than the maximum token length are cut into pieces of that length (never inside a surrogate pair), so no single
/// input can make a term of unbounded size.
/// </para>
/// <para>
/// With <c>cjkBigrams</c> on, runs of Han, Hiragana, Katakana or Hangul characters of one script become overlapping two-character
/// terms (a lone character stays a one-character term), so Chinese, Japanese and Korean text can be searched without a
/// dictionary. A bigram's offsets cover both characters.
/// </para>
/// </summary>
public sealed class StandardTokenizer : Tokenizer
{
    /// <summary>The default maximum token length, in UTF-16 characters.</summary>
    public const int DefaultMaxTokenLength = 255;

    private readonly int _maxLength;
    private readonly bool _cjkBigrams;
    private ReadOnlyMemory<char> _text;
    private int _position;
    private int _pendingStart;
    private int _pendingEnd;
    private TokenType _pendingType;
    private int _runPosition; // start of the first character of the next bigram
    private int _runEnd;
    private int _runEmitted;

    /// <summary>Creates a tokenizer.</summary>
    /// <param name="maxTokenLength">The longest term to emit, in UTF-16 characters; at least 2. Default 255.</param>
    /// <param name="cjkBigrams">Whether to turn CJK runs into bigrams. Default off.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxTokenLength"/> is below 2.</exception>
    public StandardTokenizer(int maxTokenLength = DefaultMaxTokenLength, bool cjkBigrams = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTokenLength, 2);
        _maxLength = maxTokenLength;
        _cjkBigrams = cjkBigrams;
    }

    /// <inheritdoc/>
    public override void Reset(ReadOnlyMemory<char> text)
    {
        _text = text;
        _position = 0;
        _pendingStart = _pendingEnd = 0;
        _runPosition = _runEnd = _runEmitted = 0;
        Token.Clear();
    }

    /// <inheritdoc/>
    public override bool MoveNext()
    {
        ReadOnlySpan<char> text = _text.Span;
        while (true)
        {
            if (_pendingStart < _pendingEnd)
            {
                EmitPiece(text, _pendingStart, _pendingEnd, _pendingType);
                return true;
            }

            if (_runPosition < _runEnd && EmitBigram(text))
            {
                return true;
            }

            if (_position >= text.Length)
            {
                return false;
            }

            int start = _position;
            if (_cjkBigrams && CjkScriptAt(text, start) != 0)
            {
                _runPosition = start;
                _runEnd = FindRunEnd(text, start);
                _runEmitted = 0;
                _position = _runEnd;
                continue;
            }

            int end = WordBreaker.Next(text, start);
            if (_cjkBigrams)
            {
                end = CutAtCjk(text, start, end);
            }

            _position = end;
            if (!Classify(text, start, end, out TokenType type))
            {
                continue;
            }

            EmitPiece(text, start, end, type);
            return true;
        }
    }

    // Emits text[start..end), or its first _maxLength characters with the rest left pending.
    private void EmitPiece(ReadOnlySpan<char> text, int start, int end, TokenType type)
    {
        int pieceEnd = end;
        if (end - start > _maxLength)
        {
            pieceEnd = start + _maxLength;
            if (char.IsLowSurrogate(text[pieceEnd]) && char.IsHighSurrogate(text[pieceEnd - 1]))
            {
                pieceEnd--;
            }

            _pendingStart = pieceEnd;
            _pendingEnd = end;
            _pendingType = type;
        }
        else
        {
            _pendingStart = _pendingEnd = 0;
        }

        Set(text, start, pieceEnd, type);
    }

    private void Set(ReadOnlySpan<char> text, int start, int end, TokenType type)
    {
        Token t = Token;
        t.Clear();
        t.SetTerm(text[start..end]);
        t.StartOffset = start;
        t.EndOffset = end;
        t.Type = type;
    }

    // Scans a word segment: does it hold a letter, digit or ideograph, and what kind of token is it?
    private static bool Classify(ReadOnlySpan<char> text, int start, int end, out TokenType type)
    {
        bool hasToken = false;
        bool hasLetter = false;
        bool cjk = false;
        for (int i = start; i < end;)
        {
            int cp = UnicodeProperties.ReadForward(text, i, out int len);
            i += len;
            byte entry = UnicodeProperties.WordEntry(cp);
            if ((entry & UnicodeProperties.TokenCharBit) == 0)
            {
                continue;
            }

            hasToken = true;
            if ((WordBreak)(entry & UnicodeProperties.WordBreakMask) != WordBreak.Numeric)
            {
                hasLetter = true;
            }

            if (cp >= 0x1100 && UnicodeProperties.CjkScriptOf(cp) != 0)
            {
                cjk = true;
            }
        }

        type = cjk ? TokenType.Ideographic : hasLetter ? TokenType.Word : TokenType.Number;
        return hasToken;
    }

    private static int CjkScriptAt(ReadOnlySpan<char> text, int index)
    {
        if (text[index] < 0x1100)
        {
            return 0;
        }

        return UnicodeProperties.CjkScriptOf(UnicodeProperties.ReadForward(text, index, out _));
    }

    // A segment that mixes CJK and other letters (Hangul is an ordinary letter to UAX #29) is cut where the CJK run begins.
    private static int CutAtCjk(ReadOnlySpan<char> text, int start, int end)
    {
        for (int i = start; i < end;)
        {
            int cp = UnicodeProperties.ReadForward(text, i, out int len);
            if (i > start && cp >= 0x1100 && UnicodeProperties.CjkScriptOf(cp) != 0)
            {
                return i;
            }

            i += len;
        }

        return end;
    }

    // The run of characters of one CJK script starting at `start`; combining marks and format characters stay with their base.
    private static int FindRunEnd(ReadOnlySpan<char> text, int start)
    {
        int script = CjkScriptAt(text, start);
        int i = start;
        while (i < text.Length)
        {
            int cp = UnicodeProperties.ReadForward(text, i, out int len);
            WordBreak wb = (WordBreak)(UnicodeProperties.WordEntry(cp) & UnicodeProperties.WordBreakMask);
            bool attached = i > start && wb is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;
            if (!attached && UnicodeProperties.CjkScriptOf(cp) != script)
            {
                break;
            }

            i += len;
        }

        return i;
    }

    // The end of the character at `index` together with the marks that follow it, within the current run.
    private int UnitEnd(ReadOnlySpan<char> text, int index)
    {
        UnicodeProperties.ReadForward(text, index, out int len);
        int i = index + len;
        while (i < _runEnd)
        {
            int cp = UnicodeProperties.ReadForward(text, i, out int l);
            if ((WordBreak)(UnicodeProperties.WordEntry(cp) & UnicodeProperties.WordBreakMask) is not (WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ))
            {
                break;
            }

            i += l;
        }

        return i;
    }

    private bool EmitBigram(ReadOnlySpan<char> text)
    {
        int firstEnd = UnitEnd(text, _runPosition);
        if (firstEnd >= _runEnd)
        {
            // Last character of the run: it stands alone only when it is the whole run.
            int start = _runPosition;
            bool alone = _runEmitted == 0;
            _runPosition = _runEnd;
            if (!alone)
            {
                return false;
            }

            Set(text, start, firstEnd, TokenType.Ideographic);
            return true;
        }

        int secondEnd = UnitEnd(text, firstEnd);
        Set(text, _runPosition, secondEnd, TokenType.Ideographic);
        _runPosition = firstEnd;
        _runEmitted++;
        return true;
    }
}
