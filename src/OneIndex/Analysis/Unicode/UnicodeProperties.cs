namespace OneIndex.Analysis.Unicode;

/// <summary>
/// Property lookups over the generated tables, and code point reading from UTF-16 that never fails: a lone surrogate is read as
/// itself and has no properties.
/// </summary>
internal static class UnicodeProperties
{
    /// <summary>Set in a word-table entry for letters, decimal digits and letter numbers.</summary>
    internal const byte TokenCharBit = 0x20;

    /// <summary>Set in a word-table entry for Extended_Pictographic.</summary>
    internal const byte ExtendedPictographicBit = 0x80;

    internal const byte WordBreakMask = 0x1F;

    /// <summary>The word-table entry of a code point: Word_Break in the low five bits plus the two flags.</summary>
    internal static byte WordEntry(int codePoint)
        => (uint)codePoint > 0x10FFFF ? (byte)0 : UnicodeTables.WordStage2[(UnicodeTables.WordStage1[codePoint >> UnicodeTables.BlockShift] << UnicodeTables.BlockShift) | (codePoint & ((1 << UnicodeTables.BlockShift) - 1))];

    /// <summary>The Sentence_Break value of a code point.</summary>
    internal static SentenceBreak SentenceBreakOf(int codePoint)
        => (uint)codePoint > 0x10FFFF ? SentenceBreak.Other : (SentenceBreak)UnicodeTables.SentenceStage2[(UnicodeTables.SentenceStage1[codePoint >> UnicodeTables.BlockShift] << UnicodeTables.BlockShift) | (codePoint & ((1 << UnicodeTables.BlockShift) - 1))];

    /// <summary>The script class used for CJK bigrams: 0 other, 1 Han, 2 Hiragana, 3 Katakana, 4 Hangul.</summary>
    internal static int CjkScriptOf(int codePoint)
        => (uint)codePoint > 0x10FFFF ? 0 : UnicodeTables.ScriptStage2[(UnicodeTables.ScriptStage1[codePoint >> UnicodeTables.BlockShift] << UnicodeTables.BlockShift) | (codePoint & ((1 << UnicodeTables.BlockShift) - 1))];

    /// <summary>The canonical combining class of a code point (0 for starters).</summary>
    internal static int CombiningClass(int codePoint)
        => (uint)codePoint > 0x10FFFF ? 0 : UnicodeTables.CccStage2[(UnicodeTables.CccStage1[codePoint >> UnicodeTables.BlockShift] << UnicodeTables.BlockShift) | (codePoint & ((1 << UnicodeTables.BlockShift) - 1))];

    /// <summary>Binary search in a sorted table.</summary>
    /// <returns>The index of <paramref name="key"/>, or -1.</returns>
    internal static int Find(ReadOnlySpan<int> sorted, int key)
    {
        int lo = 0;
        int hi = sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            int v = sorted[mid];
            if (v == key)
            {
                return mid;
            }

            if (v < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    /// <summary>Binary search in a sorted table of 64-bit keys.</summary>
    /// <returns>The index of <paramref name="key"/>, or -1.</returns>
    internal static int Find(ReadOnlySpan<long> sorted, long key)
    {
        int lo = 0;
        int hi = sorted.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            long v = sorted[mid];
            if (v == key)
            {
                return mid;
            }

            if (v < key)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    /// <summary>Reads the code point starting at <paramref name="index"/>; a lone surrogate is returned as its own value.</summary>
    internal static int ReadForward(ReadOnlySpan<char> text, int index, out int length)
    {
        char c = text[index];
        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
        {
            length = 2;
            return char.ConvertToUtf32(c, text[index + 1]);
        }

        length = 1;
        return c;
    }

    /// <summary>Reads the code point that ends just before <paramref name="index"/>.</summary>
    internal static int ReadBackward(ReadOnlySpan<char> text, int index, out int length)
    {
        char c = text[index - 1];
        if (char.IsLowSurrogate(c) && index >= 2 && char.IsHighSurrogate(text[index - 2]))
        {
            length = 2;
            return char.ConvertToUtf32(text[index - 2], c);
        }

        length = 1;
        return c;
    }
}
