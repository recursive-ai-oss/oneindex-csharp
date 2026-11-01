using System.Buffers;
using System.Globalization;
using System.Text;

namespace OneIndex.Models.Tokenization;

/// <summary>
/// The BERT tokenizer: basic tokenization followed by greedy longest-match-first WordPiece (Devlin et al. 2019, section 3.1;
/// Wu et al. 2016 for the WordPiece idea; the BERT README for the cleaning, CJK, accent and punctuation rules).
/// </summary>
/// <remarks>
/// <para>Steps, in order, for each stretch of text between special tokens:</para>
/// <list type="number">
/// <item>Cleaning: control characters, U+0000 and U+FFFD are dropped; every whitespace character becomes a word break.</item>
/// <item>CJK ideographs become words of their own.</item>
/// <item>Each word is decomposed (NFD), nonspacing marks are dropped (accent stripping) and it is lower-cased, per the options.</item>
/// <item>Each word is split around punctuation: every punctuation character is a word of its own.</item>
/// <item>Each word is cut into the longest vocabulary pieces from the left; later pieces carry the continuation prefix. A word
/// that cannot be cut completely, or is longer than <see cref="WordPieceOptions.MaxInputCharsPerWord"/>, is one unknown token.</item>
/// </list>
/// <para>The details the written descriptions leave open (which characters count as control or punctuation, how case mapping treats a
/// few characters, whether special-token text in the input is recognized) were settled by comparing token ids with the Hugging Face
/// <c>tokenizers</c> package on every Unicode scalar value, run as a black box; <c>docs/models.md</c> lists the settled behaviour.</para>
/// <para>Thread-safe. Memory used per call is bounded by the longest word of the input, not by its total length, and encoding stops
/// as soon as the output is full.</para>
/// </remarks>
public sealed class WordPieceTokenizer
{
    private readonly WordPieceVocabulary _vocabulary;
    private readonly WordPieceOptions _options;
    private readonly bool _stripAccents;
    private readonly ICanonicalDecomposer _decomposer;
    private readonly (string Text, int Id)[] _specials;

    /// <summary>Creates a tokenizer over a vocabulary.</summary>
    /// <param name="vocabulary">The vocabulary; it must contain the unknown token and, for <see cref="Encode(ReadOnlySpan{char}, Span{int}, bool)"/> with
    /// special tokens, the class and separator tokens.</param>
    /// <param name="options">Settings; the BERT uncased defaults when <see langword="null"/>.</param>
    /// <param name="decomposer">Canonical decomposition used for accent stripping. When <see langword="null"/> the runtime's own
    /// normalization is used, which only works where the runtime has Unicode normalization data (not with invariant globalization
    /// on Linux, where it silently leaves text unchanged); pass an implementation backed by your own tables there.</param>
    /// <exception cref="ArgumentException">The vocabulary lacks the unknown, class or separator token.</exception>
    public WordPieceTokenizer(WordPieceVocabulary vocabulary, WordPieceOptions? options = null, ICanonicalDecomposer? decomposer = null)
    {
        ArgumentNullException.ThrowIfNull(vocabulary);
        _vocabulary = vocabulary;
        _options = options ?? new WordPieceOptions();
        _decomposer = decomposer ?? RuntimeDecomposer.Instance;
        _stripAccents = _options.StripAccents ?? _options.LowerCase;
        UnknownId = Require(_options.UnknownToken);
        ClassId = Require(_options.ClassToken);
        SeparatorId = Require(_options.SeparatorToken);
        PadId = Require(_options.PadToken);
        var specials = new List<(string, int)>();
        foreach (string name in new[] { _options.PadToken, _options.UnknownToken, _options.ClassToken, _options.SeparatorToken, _options.MaskToken })
        {
            if (vocabulary.TryGetId(name, out int id))
            {
                specials.Add((name, id));
            }
        }

        _specials = [.. specials];
    }

    /// <summary>The id of the unknown token.</summary>
    public int UnknownId { get; }

    /// <summary>The id of the sequence-start token.</summary>
    public int ClassId { get; }

    /// <summary>The id of the sequence-end token.</summary>
    public int SeparatorId { get; }

    /// <summary>The id of the padding token.</summary>
    public int PadId { get; }

    /// <summary>The vocabulary this tokenizer matches against.</summary>
    public WordPieceVocabulary Vocabulary => _vocabulary;

    private int Require(string token) =>
        _vocabulary.TryGetId(token, out int id) ? id : throw new ArgumentException($"The vocabulary has no {token} token.");

    /// <summary>Encodes a text into token ids, with the class token first and the separator last when
    /// <paramref name="addSpecialTokens"/> is set, truncating the content so that the whole fits in <paramref name="ids"/>.</summary>
    /// <param name="text">Any text, including the empty string and text with unpaired surrogates (treated as U+FFFD, which is dropped).</param>
    /// <param name="ids">Receives the ids; its length is the maximum sequence length.</param>
    /// <param name="addSpecialTokens">Wrap the content in the class and separator tokens.</param>
    /// <returns>The number of ids written.</returns>
    /// <exception cref="ArgumentException"><paramref name="addSpecialTokens"/> is set and <paramref name="ids"/> is shorter than 2.</exception>
    public int Encode(ReadOnlySpan<char> text, Span<int> ids, bool addSpecialTokens = true)
    {
        if (addSpecialTokens && ids.Length < 2)
        {
            throw new ArgumentException("The output needs room for the class and separator tokens.", nameof(ids));
        }

        int offset = addSpecialTokens ? 1 : 0;
        int limit = ids.Length - (addSpecialTokens ? 2 : 0);
        var state = new State(limit);
        try
        {
            EncodeText(text, state);
            int content = Math.Min(state.Total, limit);
            state.Ids.AsSpan(0, content).CopyTo(ids[offset..]);
            if (addSpecialTokens)
            {
                ids[0] = ClassId;
                ids[1 + content] = SeparatorId;
                return content + 2;
            }

            return content;
        }
        finally
        {
            state.Release();
        }
    }

    /// <summary>Encodes a text into a new array, with special tokens, truncated to <paramref name="maxLength"/> ids in all.</summary>
    /// <param name="text">The text.</param>
    /// <param name="maxLength">The most ids returned, special tokens included; at least 2. 512 by default.</param>
    /// <returns>The ids.</returns>
    public int[] Encode(string text, int maxLength = 512)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        int[] rented = ArrayPool<int>.Shared.Rent(maxLength);
        try
        {
            int n = Encode(text, rented.AsSpan(0, maxLength));
            return rented.AsSpan(0, n).ToArray();
        }
        finally
        {
            ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>Encodes texts with special tokens and pads them to the longest (after truncation).</summary>
    /// <param name="texts">The texts.</param>
    /// <param name="maxLength">The most ids per row, special tokens included; at least 2.</param>
    /// <returns>The padded batch.</returns>
    public TokenizedBatch EncodeBatch(IReadOnlyList<string> texts, int maxLength = 512)
    {
        ArgumentNullException.ThrowIfNull(texts);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        var rows = new int[texts.Count][];
        int longest = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = Encode(texts[i], maxLength);
            longest = Math.Max(longest, rows[i].Length);
        }

        var ids = new int[checked(rows.Length * longest)];
        var mask = new int[ids.Length];
        var lengths = new int[rows.Length];
        for (int i = 0; i < rows.Length; i++)
        {
            Span<int> row = ids.AsSpan(i * longest, longest);
            rows[i].CopyTo(row);
            row[rows[i].Length..].Fill(PadId);
            mask.AsSpan(i * longest, rows[i].Length).Fill(1);
            lengths[i] = rows[i].Length;
        }

        return new TokenizedBatch(rows.Length, longest, ids, mask, lengths);
    }

    // Splits the text around special tokens, which are recognized before any normalization (they are never lower-cased or split),
    // and encodes the stretches between them.
    private void EncodeText(ReadOnlySpan<char> text, State state)
    {
        if (!_options.SplitOnSpecialTokens || _specials.Length == 0)
        {
            EncodeSegment(text, state);
            return;
        }

        while (!state.Full)
        {
            int from = 0;
            int found = -1;
            int foundId = 0;
            int foundLength = 0;
            while (from < text.Length)
            {
                int open = text[from..].IndexOf('[');
                if (open < 0)
                {
                    break;
                }

                open += from;
                foreach ((string special, int id) in _specials)
                {
                    if (text[open..].StartsWith(special, StringComparison.Ordinal))
                    {
                        found = open;
                        foundId = id;
                        foundLength = special.Length;
                        break;
                    }
                }

                if (found >= 0)
                {
                    break;
                }

                from = open + 1;
            }

            if (found < 0)
            {
                EncodeSegment(text, state);
                return;
            }

            EncodeSegment(text[..found], state);
            state.Add(foundId);
            text = text[(found + foundLength)..];
        }
    }

    // Basic tokenization of one stretch of text: clean, split off CJK, then normalize and word-piece each whitespace-delimited word.
    private void EncodeSegment(ReadOnlySpan<char> text, State state)
    {
        int wordLength = 0;
        while (!text.IsEmpty && !state.Full)
        {
            Rune.DecodeFromUtf16(text, out Rune rune, out int consumed);
            ReadOnlySpan<char> source = text[..consumed];
            text = text[consumed..];
            if (IsDropped(rune))
            {
                continue;
            }

            if (IsWhiteSpace(rune))
            {
                FlushWord(state, ref wordLength);
                continue;
            }

            if (_options.TokenizeChineseChars && IsChineseChar(rune.Value))
            {
                FlushWord(state, ref wordLength);
                source.CopyTo(state.Word(consumed));
                wordLength = consumed;
                FlushWord(state, ref wordLength);
                continue;
            }

            Span<char> destination = state.WordAppend(wordLength, consumed);
            source.CopyTo(destination);
            wordLength += consumed;
        }

        FlushWord(state, ref wordLength);
    }

    private void FlushWord(State state, ref int wordLength)
    {
        if (wordLength == 0 || state.Full)
        {
            wordLength = 0;
            return;
        }

        ReadOnlySpan<char> raw = state.WordBuffer.AsSpan(0, wordLength);
        wordLength = 0;
        ReadOnlySpan<char> normalized = Normalize(raw, state);

        // Split around punctuation: each punctuation character is a word, the runs between them are words.
        int start = 0;
        int position = 0;
        while (position < normalized.Length && !state.Full)
        {
            Rune.DecodeFromUtf16(normalized[position..], out Rune rune, out int consumed);
            if (IsPunctuation(rune))
            {
                if (position > start)
                {
                    WordPiece(normalized[start..position], state);
                }

                WordPiece(normalized.Slice(position, consumed), state);
                start = position + consumed;
            }

            position += consumed;
        }

        if (start < normalized.Length && !state.Full)
        {
            WordPiece(normalized[start..], state);
        }
    }

    // Accent stripping (NFD, then drop nonspacing marks; Unicode Standard section 3.7 and UAX #15) and lower-casing, in that order.
    private ReadOnlySpan<char> Normalize(ReadOnlySpan<char> word, State state)
    {
        if (Ascii.IsValid(word))
        {
            if (!_options.LowerCase)
            {
                return word;
            }

            Span<char> lowered = state.Scratch(0, word.Length);
            for (int i = 0; i < word.Length; i++)
            {
                char c = word[i];
                lowered[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
            }

            return lowered;
        }

        ReadOnlySpan<char> current = word;
        if (_stripAccents)
        {
            int size = word.Length * 2 + 16;
            while (true)
            {
                Span<char> decomposed = state.Scratch(1, size);
                if (_decomposer.TryDecompose(current, decomposed, out int written))
                {
                    int kept = 0;
                    int p = 0;
                    ReadOnlySpan<char> d = decomposed[..written];
                    Span<char> filtered = state.Scratch(2, written);
                    while (p < d.Length)
                    {
                        Rune.DecodeFromUtf16(d[p..], out Rune rune, out int consumed);
                        if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.NonSpacingMark)
                        {
                            d.Slice(p, consumed).CopyTo(filtered[kept..]);
                            kept += consumed;
                        }

                        p += consumed;
                    }

                    current = filtered[..kept];
                    break;
                }

                size = checked(size * 2);
            }
        }

        if (!_options.LowerCase)
        {
            return current;
        }

        // Case mapping is one-to-one except U+0130, which lower-cases to "i" and a combining dot (Unicode SpecialCasing.txt).
        Span<char> output = state.Scratch(0, current.Length * 2);
        int length = 0;
        int index = 0;
        while (index < current.Length)
        {
            Rune.DecodeFromUtf16(current[index..], out Rune rune, out int consumed);
            index += consumed;
            if (rune.Value == 0x130)
            {
                output[length++] = 'i';
                output[length++] = '\u0307';
                continue;
            }

            length += Rune.ToLowerInvariant(rune).EncodeToUtf16(output[length..]);
        }

        return output[..length];
    }

    // Wu et al. 2016 / BERT: greedy longest-match-first. If no piece matches at some position the whole word is unknown.
    private void WordPiece(ReadOnlySpan<char> word, State state)
    {
        if (state.Full)
        {
            return;
        }

        if (CountScalars(word) > _options.MaxInputCharsPerWord)
        {
            state.Add(UnknownId);
            return;
        }

        int before = state.Total;
        int start = 0;
        while (start < word.Length)
        {
            int end = word.Length;
            int id = -1;
            while (end > start)
            {
                ReadOnlySpan<char> piece = word[start..end];
                bool hit = start == 0 ? _vocabulary.TryGetInitial(piece, out id) : _vocabulary.TryGetContinuation(piece, out id);
                if (hit)
                {
                    break;
                }

                id = -1;
                end--;
                if (end > start && char.IsLowSurrogate(word[end]) && char.IsHighSurrogate(word[end - 1]))
                {
                    end--;
                }
            }

            if (id < 0)
            {
                state.Rewind(before);
                state.Add(UnknownId);
                return;
            }

            state.Add(id);
            start = end;
        }
    }

    private static int CountScalars(ReadOnlySpan<char> text)
    {
        int count = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (!char.IsLowSurrogate(text[i]) || i == 0 || !char.IsHighSurrogate(text[i - 1]))
            {
                count++;
            }
        }

        return count;
    }

    // Characters the cleaning step removes: U+0000, U+FFFD (also what an unpaired surrogate decodes to) and every control, format,
    // private-use or unassigned character except tab, line feed and carriage return. So vertical tab, form feed and U+0085 are
    // removed, not word breaks (settled by comparing with the reference tokenizer on every scalar value).
    private static bool IsDropped(Rune rune)
    {
        if (rune.Value == 0 || rune.Value == 0xFFFD)
        {
            return true;
        }

        // Tab, line feed and carriage return are control characters that count as whitespace instead.
        if (rune.Value is 9 or 10 or 13)
        {
            return false;
        }

        UnicodeCategory category = Rune.GetUnicodeCategory(rune);
        return category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.PrivateUse or UnicodeCategory.Surrogate;
    }

    // The Unicode White_Space property.
    private static bool IsWhiteSpace(Rune rune) => Rune.IsWhiteSpace(rune);

    // BERT README: all ASCII non-alphanumerics count as punctuation (so "$" and "^" are split), plus the Unicode P* categories.
    private static bool IsPunctuation(Rune rune)
    {
        int v = rune.Value;
        if (v < 128)
        {
            return v is >= 33 and <= 47 or >= 58 and <= 64 or >= 91 and <= 96 or >= 123 and <= 126;
        }

        return Rune.GetUnicodeCategory(rune) is UnicodeCategory.ConnectorPunctuation or UnicodeCategory.DashPunctuation
            or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
            or UnicodeCategory.FinalQuotePunctuation or UnicodeCategory.OtherPunctuation;
    }

    // BERT README: the CJK Unified Ideographs blocks and the compatibility ideographs (not Hiragana, Katakana or Hangul). The
    // reference tokenizer leaves U+2B820 to U+2B91F (the start of Extension E) out although the README lists the whole block;
    // that was settled by observing its output, and the ids it gives for those characters are the ones the models were used with.
    private static bool IsChineseChar(int v) =>
        v is >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF or >= 0x20000 and <= 0x2A6DF or >= 0x2A700 and <= 0x2B73F
            or >= 0x2B740 and <= 0x2B81F or >= 0x2B920 and <= 0x2CEAF or >= 0xF900 and <= 0xFAFF or >= 0x2F800 and <= 0x2FA1F;

    // The per-call output cursor and the pooled scratch buffers.
    private sealed class State
    {
        private readonly int[] _ids;
        private readonly int _limit;
        private char[]? _word;
        private char[]?[] _scratch;

        public State(int limit)
        {
            _ids = ArrayPool<int>.Shared.Rent(Math.Max(limit, 1));
            _limit = limit;
            Total = 0;
            _word = null;
            _scratch = new char[]?[3];
        }

        // Ids produced so far, which may exceed the limit while a word is decided (only the first limit are kept).
        public int Total { get; private set; }

        public int[] Ids => _ids;

        public bool Full => Total >= _limit;

        public char[] WordBuffer => _word ??= ArrayPool<char>.Shared.Rent(256);

        public void Add(int id)
        {
            if (Total < _limit)
            {
                _ids[Total] = id;
            }

            Total++;
        }

        public void Rewind(int total) => Total = total;

        public Span<char> Word(int length)
        {
            EnsureWord(0, length);
            return _word.AsSpan(0, length);
        }

        public Span<char> WordAppend(int used, int extra)
        {
            EnsureWord(used, extra);
            return _word.AsSpan(used, extra);
        }

        private void EnsureWord(int used, int extra)
        {
            int need = checked(used + extra);
            if (_word is null)
            {
                _word = ArrayPool<char>.Shared.Rent(Math.Max(256, need));
            }
            else if (_word.Length < need)
            {
                char[] bigger = ArrayPool<char>.Shared.Rent(Math.Max(need, _word.Length * 2));
                _word.AsSpan(0, used).CopyTo(bigger);
                ArrayPool<char>.Shared.Return(_word);
                _word = bigger;
            }
        }

        public Span<char> Scratch(int slot, int length)
        {
            char[]? buffer = _scratch[slot];
            if (buffer is null || buffer.Length < length)
            {
                if (buffer is not null)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                }

                buffer = ArrayPool<char>.Shared.Rent(Math.Max(length, 64));
                _scratch[slot] = buffer;
            }

            return buffer.AsSpan(0, length);
        }

        public void Release()
        {
            ArrayPool<int>.Shared.Return(_ids);
            if (_word is not null)
            {
                ArrayPool<char>.Shared.Return(_word);
                _word = null;
            }

            for (int i = 0; i < _scratch.Length; i++)
            {
                if (_scratch[i] is { } buffer)
                {
                    ArrayPool<char>.Shared.Return(buffer);
                    _scratch[i] = null;
                }
            }
        }
    }
}
