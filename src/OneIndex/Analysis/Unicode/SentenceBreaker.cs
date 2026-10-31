namespace OneIndex.Analysis.Unicode;

/// <summary>
/// Sentence boundaries per Unicode Standard Annex #29, section 5.1.1 (Unicode 17.0.0), over UTF-16 text.
/// <para>
/// The segmenter keeps a small state machine for the pattern <c>SATerm Close* Sp* ParaSep?</c> that rules SB8a to SB11 describe,
/// so text is read once. Rule SB8 needs to look ahead for a lower-case letter; that look-ahead stops at the next sentence
/// terminator, so the total work stays linear.
/// </para>
/// </summary>
internal static class SentenceBreaker
{
    /// <summary>Finds the end of the sentence that starts at <paramref name="start"/>.</summary>
    /// <param name="text">The text.</param>
    /// <param name="start">A boundary: 0 or a value this method returned earlier.</param>
    /// <returns>The index just past the sentence; <c>text.Length</c> for the last one.</returns>
    internal static int Next(ReadOnlySpan<char> text, int start)
    {
        int n = text.Length;
        if (start >= n)
        {
            return n;
        }

        int i = start;
        int cp = UnicodeProperties.ReadForward(text, i, out int len);
        SentenceBreak cur = UnicodeProperties.SentenceBreakOf(cp);
        i += len;

        // SB3 (CR × LF) and SB4 (break after a paragraph separator).
        if (cur == SentenceBreak.CR)
        {
            return i < n && text[i] == '\n' ? i + 1 : i;
        }

        if (cur is SentenceBreak.LF or SentenceBreak.Sep)
        {
            return i;
        }

        // phase: 0 outside the terminator pattern; 1 right after SATerm; 2 after SATerm Close+; 3 after SATerm Close* Sp+.
        int phase = 0;
        bool termIsATerm = false;
        bool termFollowsLetter = false; // SB7: the character before the ATerm is Upper or Lower
        SentenceBreak lastSignificant = cur;
        if (cur is SentenceBreak.ATerm or SentenceBreak.STerm)
        {
            phase = 1;
            termIsATerm = cur == SentenceBreak.ATerm;
        }

        while (i < n)
        {
            int next = UnicodeProperties.ReadForward(text, i, out int nextLength);
            SentenceBreak np = UnicodeProperties.SentenceBreakOf(next);

            // SB5: Extend and Format characters belong to the character before them.
            if (np is SentenceBreak.Extend or SentenceBreak.Format)
            {
                i += nextLength;
                continue;
            }

            bool paraSep = np is SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep;
            if (phase != 0)
            {
                bool noBreak = false;
                if (phase == 1 && termIsATerm && np == SentenceBreak.Numeric)
                {
                    noBreak = true; // SB6
                }
                else if (phase == 1 && termIsATerm && np == SentenceBreak.Upper && termFollowsLetter)
                {
                    noBreak = true; // SB7
                }
                else if (paraSep || np == SentenceBreak.Sp || (np == SentenceBreak.Close && phase <= 2))
                {
                    // SB9 and SB10. Rule SB8 would also forbid the break here, so the look-ahead is not needed.
                    noBreak = true;
                }

                if (!noBreak && termIsATerm)
                {
                    // SB8: ATerm Close* Sp* × (¬(OLetter | Upper | Lower | ParaSep | SATerm))* Lower
                    int afterLower = FindLowerAfterNonLetters(text, i);
                    if (afterLower >= 0)
                    {
                        i = afterLower;
                        lastSignificant = SentenceBreak.Lower;
                        phase = 0;
                        continue;
                    }
                }

                if (!noBreak && np is SentenceBreak.SContinue or SentenceBreak.ATerm or SentenceBreak.STerm)
                {
                    noBreak = true; // SB8a
                }

                if (!noBreak)
                {
                    return i; // SB11
                }
            }

            i += nextLength;
            if (paraSep)
            {
                // SB3 and SB4: the separator ends the sentence, a CR takes a following LF with it.
                return np == SentenceBreak.CR && i < n && text[i] == '\n' ? i + 1 : i;
            }

            if (np is SentenceBreak.ATerm or SentenceBreak.STerm)
            {
                phase = 1;
                termIsATerm = np == SentenceBreak.ATerm;
                termFollowsLetter = lastSignificant is SentenceBreak.Upper or SentenceBreak.Lower;
            }
            else if (phase != 0 && np == SentenceBreak.Close && phase <= 2)
            {
                phase = 2;
            }
            else if (phase != 0 && np == SentenceBreak.Sp)
            {
                phase = 3;
            }
            else
            {
                phase = 0;
            }

            lastSignificant = np;
        }

        return n;
    }

    // Skips characters outside OLetter, Upper, Lower, ParaSep and SATerm; returns the index past the Lower that ends the run, or -1.
    private static int FindLowerAfterNonLetters(ReadOnlySpan<char> text, int index)
    {
        while (index < text.Length)
        {
            int cp = UnicodeProperties.ReadForward(text, index, out int len);
            SentenceBreak p = UnicodeProperties.SentenceBreakOf(cp);
            if (p == SentenceBreak.Lower)
            {
                return index + len;
            }

            if (p is SentenceBreak.OLetter or SentenceBreak.Upper or SentenceBreak.CR or SentenceBreak.LF or SentenceBreak.Sep or SentenceBreak.ATerm or SentenceBreak.STerm)
            {
                return -1;
            }

            index += len;
        }

        return -1;
    }
}
