namespace OneIndex.Analysis.Unicode;

/// <summary>
/// Full case folding from CaseFolding.txt (Unicode 17.0.0, statuses C and F): "ß" folds to "ss", "ſ" to "s", final sigma to
/// sigma. Folding does not depend on a culture and is the same on every platform. The Turkic mappings (status T) are not used.
/// </summary>
internal static class CaseFolder
{
    /// <summary>The longest folding of one UTF-16 character, in characters.</summary>
    internal const int MaxExpansion = 3;

    /// <summary>Folds <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="source">The text.</param>
    /// <param name="destination">Room for at least <c>source.Length * MaxExpansion</c> characters.</param>
    /// <returns>The folded length.</returns>
    internal static int Fold(ReadOnlySpan<char> source, Span<char> destination)
    {
        int w = 0;
        for (int i = 0; i < source.Length;)
        {
            char c = source[i];
            if (c < 0x80)
            {
                destination[w++] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
                i++;
                continue;
            }

            int cp = UnicodeProperties.ReadForward(source, i, out int len);
            int at = UnicodeProperties.Find(UnicodeTables.CaseFoldKeys, cp);
            if (at < 0)
            {
                source.Slice(i, len).CopyTo(destination[w..]);
                w += len;
            }
            else
            {
                int info = UnicodeTables.CaseFoldInfos[at];
                UnicodeTables.CaseFoldPool.AsSpan(info >> 5, info & 31).CopyTo(destination[w..]);
                w += info & 31;
            }

            i += len;
        }

        return w;
    }

    /// <summary>Folds ASCII upper case in place; returns <see langword="false"/> and changes nothing at the first non-ASCII character.</summary>
    internal static bool TryFoldAsciiInPlace(Span<char> text)
    {
        if (!System.Text.Ascii.IsValid(text))
        {
            return false;
        }

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is >= 'A' and <= 'Z')
            {
                text[i] = (char)(c + 32);
            }
        }

        return true;
    }
}
