namespace OneIndex.Analysis.Unicode;

/// <summary>
/// Word boundaries per Unicode Standard Annex #29, section 4.1.1 (Unicode 17.0.0), over UTF-16 text.
/// <para>
/// The segmenter walks forward once and keeps a few facts about the segment it is building (the last two significant classes
/// and the length of a regional-indicator run), so finding every boundary in a text takes time linear in its length, even for
/// hostile input such as a million combining marks or flag symbols. Rule WB4 ("ignore Extend, Format and ZWJ") is applied by
/// treating such a character as part of the one before it.
/// </para>
/// </summary>
internal static class WordBreaker
{
    private const WordBreak None = (WordBreak)0xFF;

    /// <summary>Finds the end of the word segment that starts at <paramref name="start"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">A boundary: 0 or a value this method returned earlier.</param>
    /// <returns>The index just past the segment; <c>text.Length</c> for the last one; <paramref name="start"/> itself only when it is at the end.</returns>
    internal static int Next(ReadOnlySpan<char> text, int start)
    {
        int n = text.Length;
        if (start >= n)
        {
            return n;
        }

        int i = start;
        int cp = UnicodeProperties.ReadForward(text, i, out int len);
        WordBreak cur = (WordBreak)(UnicodeProperties.WordEntry(cp) & UnicodeProperties.WordBreakMask);
        i += len;

        // WB3 (CR × LF), WB3a (break after Newline, CR, LF).
        if (cur == WordBreak.CR)
        {
            return i < n && text[i] == '\n' ? i + 1 : i;
        }

        if (cur is WordBreak.LF or WordBreak.Newline)
        {
            return i;
        }

        WordBreak prev = cur;
        WordBreak beforePrev = None;
        WordBreak lastRaw = cur;
        int regionalIndicators = cur == WordBreak.RegionalIndicator ? 1 : 0;

        while (i < n)
        {
            int next = UnicodeProperties.ReadForward(text, i, out int nextLength);
            byte entry = UnicodeProperties.WordEntry(next);
            WordBreak np = (WordBreak)(entry & UnicodeProperties.WordBreakMask);

            // WB3b: break before Newline, CR, LF.
            if (np is WordBreak.CR or WordBreak.LF or WordBreak.Newline)
            {
                return i;
            }

            bool ignorable = np is WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ;
            bool joins =
                // WB3c: no break within an emoji ZWJ sequence.
                (lastRaw == WordBreak.ZWJ && (entry & UnicodeProperties.ExtendedPictographicBit) != 0) ||
                // WB3d: keep horizontal white space together.
                (lastRaw == WordBreak.WSegSpace && np == WordBreak.WSegSpace) ||
                // WB4: a following Extend, Format or ZWJ belongs to the character before it.
                ignorable ||
                NoBreakBetween(text, i + nextLength, prev, beforePrev, np, regionalIndicators);

            if (!joins)
            {
                return i;
            }

            if (!ignorable)
            {
                beforePrev = prev;
                prev = np;
                regionalIndicators = np == WordBreak.RegionalIndicator ? regionalIndicators + 1 : 0;
            }

            lastRaw = np;
            i += nextLength;
        }

        return n;
    }

    // Rules WB5 to WB16 for the pair (prev, np). `after` is the index just past np, where the lookahead rules continue.
    private static bool NoBreakBetween(ReadOnlySpan<char> text, int after, WordBreak prev, WordBreak beforePrev, WordBreak np, int regionalIndicators)
    {
        bool prevLetter = prev is WordBreak.ALetter or WordBreak.HebrewLetter;
        bool npLetter = np is WordBreak.ALetter or WordBreak.HebrewLetter;

        // WB5: AHLetter × AHLetter.
        if (prevLetter && npLetter)
        {
            return true;
        }

        // WB8, WB9, WB10: digits join digits and letters.
        if ((prev == WordBreak.Numeric && (np == WordBreak.Numeric || npLetter)) || (prevLetter && np == WordBreak.Numeric))
        {
            return true;
        }

        // WB13: Katakana × Katakana.
        if (prev == WordBreak.Katakana && np == WordBreak.Katakana)
        {
            return true;
        }

        // WB13a, WB13b: ExtendNumLet (underscore and friends) joins letters, digits and Katakana on both sides.
        if (np == WordBreak.ExtendNumLet && (prevLetter || prev is WordBreak.Numeric or WordBreak.Katakana or WordBreak.ExtendNumLet))
        {
            return true;
        }

        if (prev == WordBreak.ExtendNumLet && (npLetter || np is WordBreak.Numeric or WordBreak.Katakana))
        {
            return true;
        }

        // WB15, WB16: regional indicators pair up; a break falls after every second one.
        if (prev == WordBreak.RegionalIndicator && np == WordBreak.RegionalIndicator)
        {
            return (regionalIndicators & 1) == 1;
        }

        // WB7a: Hebrew_Letter × Single_Quote.
        if (prev == WordBreak.HebrewLetter && np == WordBreak.SingleQuote)
        {
            return true;
        }

        // The remaining rules look at three characters: one before, one after, or both.
        bool npMidLetterQ = np is WordBreak.MidLetter or WordBreak.MidNumLet or WordBreak.SingleQuote;
        bool prevMidLetterQ = prev is WordBreak.MidLetter or WordBreak.MidNumLet or WordBreak.SingleQuote;
        bool npMidNumQ = np is WordBreak.MidNum or WordBreak.MidNumLet or WordBreak.SingleQuote;
        bool prevMidNumQ = prev is WordBreak.MidNum or WordBreak.MidNumLet or WordBreak.SingleQuote;
        bool beforeLetter = beforePrev is WordBreak.ALetter or WordBreak.HebrewLetter;

        // WB7: AHLetter (MidLetter | MidNumLetQ) × AHLetter.
        if (beforeLetter && prevMidLetterQ && npLetter)
        {
            return true;
        }

        // WB7c: Hebrew_Letter Double_Quote × Hebrew_Letter.
        if (beforePrev == WordBreak.HebrewLetter && prev == WordBreak.DoubleQuote && np == WordBreak.HebrewLetter)
        {
            return true;
        }

        // WB11: Numeric (MidNum | MidNumLetQ) × Numeric.
        if (beforePrev == WordBreak.Numeric && prevMidNumQ && np == WordBreak.Numeric)
        {
            return true;
        }

        // WB6: AHLetter × (MidLetter | MidNumLetQ) AHLetter.
        if (prevLetter && npMidLetterQ)
        {
            WordBreak ahead = ClassAfter(text, after);
            if (ahead is WordBreak.ALetter or WordBreak.HebrewLetter)
            {
                return true;
            }
        }

        // WB7b: Hebrew_Letter × Double_Quote Hebrew_Letter.
        if (prev == WordBreak.HebrewLetter && np == WordBreak.DoubleQuote && ClassAfter(text, after) == WordBreak.HebrewLetter)
        {
            return true;
        }

        // WB12: Numeric × (MidNum | MidNumLetQ) Numeric.
        if (prev == WordBreak.Numeric && npMidNumQ && ClassAfter(text, after) == WordBreak.Numeric)
        {
            return true;
        }

        return false; // WB999
    }

    // The class of the first character at or after `index` that rule WB4 does not ignore.
    private static WordBreak ClassAfter(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length)
        {
            int cp = UnicodeProperties.ReadForward(text, index, out int len);
            WordBreak p = (WordBreak)(UnicodeProperties.WordEntry(cp) & UnicodeProperties.WordBreakMask);
            if (p is not (WordBreak.Extend or WordBreak.Format or WordBreak.ZWJ))
            {
                return p;
            }

            index += len;
        }

        return None;
    }
}
