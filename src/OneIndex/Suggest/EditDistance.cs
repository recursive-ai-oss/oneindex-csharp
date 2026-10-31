using System.Buffers;

namespace OneIndex.Suggest;

/// <summary>
/// The edit distance used to rank spelling candidates: the optimal string alignment distance over Unicode code points
/// (insertion, deletion, substitution and the swap of two adjacent code points each cost 1; a swapped pair is not edited
/// again). It is the distance <see cref="OneIndex.Codecs.LevenshteinAutomaton"/> accepts with swaps enabled, so a term the
/// automaton returns has exactly the distance this reports (Damerau 1964 counted swaps as an edit; Wagner and Fischer 1974 gave the
/// row-by-row computation used here).
/// </summary>
public static class EditDistance
{
    private const int StackCodePoints = 64;

    /// <summary>Computes the distance, giving up once it passes a bound.</summary>
    /// <param name="a">The first string; any text (a lone surrogate counts as one code point).</param>
    /// <param name="b">The second string.</param>
    /// <param name="max">The largest distance that matters; 0 or more.</param>
    /// <returns>The distance when it is at most <paramref name="max"/>, otherwise <c>max + 1</c>.</returns>
    public static int OptimalStringAlignment(ReadOnlySpan<char> a, ReadOnlySpan<char> b, int max)
    {
        // Strings up to StackCodePoints code points use fixed stack buffers (3 rows of 65 ints, 2 x 64 code points: ~1.3 KB);
        // longer ones borrow from the shared pool.
        int[]? rented = null;
        try
        {
            int need = a.Length + b.Length + 3 * (b.Length + 1);
            Span<int> all = need <= 2 * StackCodePoints + 3 * (StackCodePoints + 1)
                ? stackalloc int[2 * StackCodePoints + 3 * (StackCodePoints + 1)]
                : (rented = ArrayPool<int>.Shared.Rent(need));
            Span<int> ca = all[..a.Length];
            Span<int> cb = all.Slice(a.Length, b.Length);
            int n = Decode(a, ca);
            int m = Decode(b, cb);
            ca = ca[..n];
            cb = cb[..m];
            if (Math.Abs(n - m) > max)
            {
                return max + 1;
            }

            Span<int> rows = all[(a.Length + b.Length)..];
            Span<int> prev2 = rows[..(m + 1)];
            Span<int> prev = rows.Slice(m + 1, m + 1);
            Span<int> cur = rows.Slice(2 * (m + 1), m + 1);
            for (int j = 0; j <= m; j++)
            {
                prev[j] = j;
            }

            for (int i = 1; i <= n; i++)
            {
                cur[0] = i;
                int rowMin = cur[0];
                for (int j = 1; j <= m; j++)
                {
                    int cost = ca[i - 1] == cb[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    if (i > 1 && j > 1 && ca[i - 1] == cb[j - 2] && ca[i - 2] == cb[j - 1])
                    {
                        v = Math.Min(v, prev2[j - 2] + 1);
                    }

                    cur[j] = v;
                    rowMin = Math.Min(rowMin, v);
                }

                if (rowMin > max)
                {
                    return max + 1;
                }

                Span<int> t = prev2;
                prev2 = prev;
                prev = cur;
                cur = t;
            }

            return Math.Min(prev[m], max + 1);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    // Writes the code points of text into destination (at least text.Length long); returns how many.
    private static int Decode(ReadOnlySpan<char> text, Span<int> destination)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                destination[n++] = char.ConvertToUtf32(c, text[i + 1]);
                i++;
            }
            else
            {
                destination[n++] = char.IsSurrogate(c) ? 0xFFFD : c; // a lone surrogate reads as U+FFFD, as in the automaton
            }
        }

        return n;
    }

    /// <summary>Counts the code points of a string the way the automaton does (a lone surrogate is one).</summary>
    /// <param name="text">The text.</param>
    /// <returns>The count.</returns>
    public static int CodePointCount(ReadOnlySpan<char> text)
    {
        int n = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                i++;
            }

            n++;
        }

        return n;
    }
}
