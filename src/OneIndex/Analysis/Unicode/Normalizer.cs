using System.Buffers;

namespace OneIndex.Analysis.Unicode;

/// <summary>The four Unicode normalization forms (UAX #15).</summary>
internal enum UnicodeForm
{
    /// <summary>Canonical decomposition, then canonical composition.</summary>
    Nfc,

    /// <summary>Canonical decomposition.</summary>
    Nfd,

    /// <summary>Compatibility decomposition, then canonical composition.</summary>
    Nfkc,

    /// <summary>Compatibility decomposition.</summary>
    Nfkd,
}

/// <summary>
/// Unicode normalization (UAX #15) from tables generated out of the UCD, so results are the same on every platform.
/// The BCL's <c>string.Normalize</c> is not used: it needs ICU, and without ICU (invariant globalization mode, which is how
/// browser WebAssembly builds usually run) it silently returns non-ASCII text unchanged, which would change the terms an index
/// stores depending on where it was written.
/// <para>
/// An instance keeps its working buffer between calls, so normalizing many short terms allocates only when a longer term than
/// any before arrives. It is not thread-safe.
/// </para>
/// </summary>
internal sealed class Normalizer
{
    private const int HangulSBase = 0xAC00;
    private const int HangulLBase = 0x1100;
    private const int HangulVBase = 0x1161;
    private const int HangulTBase = 0x11A7;
    private const int HangulLCount = 19;
    private const int HangulVCount = 21;
    private const int HangulTCount = 28;
    private const int HangulNCount = HangulVCount * HangulTCount;
    private const int HangulSCount = HangulLCount * HangulNCount;

    private int[] _cps = new int[64];
    private int[] _spare = new int[64];
    private int _count;

    /// <summary>Normalizes <paramref name="source"/> into <paramref name="destination"/>.</summary>
    /// <param name="source">The text.</param>
    /// <param name="form">The form.</param>
    /// <param name="destination">Receives the result.</param>
    /// <param name="written">The result length when it fits.</param>
    /// <returns><see langword="false"/> when <paramref name="destination"/> is too small (try again with more room).</returns>
    internal bool TryNormalize(ReadOnlySpan<char> source, UnicodeForm form, Span<char> destination, out int written)
    {
        bool compat = form is UnicodeForm.Nfkc or UnicodeForm.Nfkd;
        Decompose(source, compat);
        Reorder();
        if (form is UnicodeForm.Nfc or UnicodeForm.Nfkc)
        {
            Compose();
        }

        return TryEncode(destination, out written);
    }

    /// <summary>Normalizes to a new string; for tests and tools.</summary>
    internal static string Normalize(string text, UnicodeForm form)
    {
        var n = new Normalizer();
        Span<char> buffer = new char[(text.Length * 18) + 16];
        return n.TryNormalize(text, form, buffer, out int written) ? new string(buffer[..written]) : throw new InvalidOperationException("buffer too small");
    }

    /// <summary>
    /// Removes the combining marks that carry accents. Text is decomposed, marks in the combining diacritical blocks are dropped, and the
    /// rest is composed again, so a precomposed "é" becomes "e" while Hangul and other scripts come back unchanged.
    /// </summary>
    internal bool TryFoldAccents(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        Decompose(source, compat: false);
        Reorder();
        DropMarks();
        Compose();
        return TryEncode(destination, out written);
    }

    /// <summary>
    /// Folds text towards ASCII: compatibility decomposition (ligatures, full-width forms, superscripts), accents dropped, then a
    /// short table for Latin letters that have no decomposition (ø, đ, ł, ß, æ ...). Characters with no ASCII form stay as they are.
    /// </summary>
    internal bool TryFoldToAscii(ReadOnlySpan<char> source, Span<char> destination, out int written)
    {
        Decompose(source, compat: true);
        Reorder();
        DropMarks();
        ApplyAsciiTable();
        Compose();
        return TryEncode(destination, out written);
    }

    // Marks that carry accents: the combining diacritical blocks.
    private static bool IsAccentMark(int cp)
        => (cp >= 0x0300 && cp <= 0x036F) || (cp >= 0x1AB0 && cp <= 0x1AFF) || (cp >= 0x1DC0 && cp <= 0x1DFF) ||
           (cp >= 0x20D0 && cp <= 0x20FF) || (cp >= 0xFE20 && cp <= 0xFE2F);

    private void DropMarks()
    {
        int w = 0;
        for (int r = 0; r < _count; r++)
        {
            if (!IsAccentMark(_cps[r]))
            {
                _cps[w++] = _cps[r];
            }
        }

        _count = w;
    }

    private void Append(int cp)
    {
        if (_count == _cps.Length)
        {
            Array.Resize(ref _cps, _cps.Length * 2);
        }

        _cps[_count++] = cp;
    }

    private void Decompose(ReadOnlySpan<char> source, bool compat)
    {
        _count = 0;
        ReadOnlySpan<int> keys = compat ? UnicodeTables.CompatibleKeys : UnicodeTables.CanonicalKeys;
        ReadOnlySpan<int> infos = compat ? UnicodeTables.CompatibleInfos : UnicodeTables.CanonicalInfos;
        ReadOnlySpan<char> pool = compat ? UnicodeTables.CompatiblePool : UnicodeTables.CanonicalPool;
        for (int i = 0; i < source.Length;)
        {
            int cp = UnicodeProperties.ReadForward(source, i, out int len);
            i += len;
            if (cp < 0x80)
            {
                Append(cp);
                continue;
            }

            int s = cp - HangulSBase;
            if ((uint)s < HangulSCount)
            {
                Append(HangulLBase + (s / HangulNCount));
                Append(HangulVBase + (s % HangulNCount / HangulTCount));
                int t = s % HangulTCount;
                if (t != 0)
                {
                    Append(HangulTBase + t);
                }

                continue;
            }

            int at = UnicodeProperties.Find(keys, cp);
            if (at < 0)
            {
                Append(cp);
                continue;
            }

            int info = infos[at];
            ReadOnlySpan<char> expansion = pool.Slice(info >> 5, info & 31);
            for (int k = 0; k < expansion.Length;)
            {
                Append(UnicodeProperties.ReadForward(expansion, k, out int l));
                k += l;
            }
        }
    }

    // Canonical ordering (UAX #15 D109): within each run of non-starters, a stable sort by combining class.
    private void Reorder()
    {
        int i = 0;
        while (i < _count)
        {
            if (UnicodeProperties.CombiningClass(_cps[i]) == 0)
            {
                i++;
                continue;
            }

            int start = i;
            while (i < _count && UnicodeProperties.CombiningClass(_cps[i]) != 0)
            {
                i++;
            }

            SortRun(start, i);
        }
    }

    private void SortRun(int start, int end)
    {
        int length = end - start;
        if (length <= 1)
        {
            return;
        }

        if (length <= 32)
        {
            // Insertion sort is stable and quick for the handful of marks real text carries.
            for (int a = start + 1; a < end; a++)
            {
                int cp = _cps[a];
                int cc = UnicodeProperties.CombiningClass(cp);
                int b = a - 1;
                while (b >= start && UnicodeProperties.CombiningClass(_cps[b]) > cc)
                {
                    _cps[b + 1] = _cps[b];
                    b--;
                }

                _cps[b + 1] = cp;
            }

            return;
        }

        // A long run (hostile input): sort by (class, position) so the cost stays n log n.
        long[] keys = ArrayPool<long>.Shared.Rent(length);
        int[] values = ArrayPool<int>.Shared.Rent(length);
        try
        {
            for (int k = 0; k < length; k++)
            {
                int cp = _cps[start + k];
                keys[k] = ((long)UnicodeProperties.CombiningClass(cp) << 32) | (uint)k;
                values[k] = cp;
            }

            Array.Sort(keys, values, 0, length);
            Array.Copy(values, 0, _cps, start, length);
        }
        finally
        {
            ArrayPool<long>.Shared.Return(keys);
            ArrayPool<int>.Shared.Return(values);
        }
    }

    // Canonical composition (UAX #15 D117): a character combines with the last starter unless something of the same or higher
    // class stands between them.
    private void Compose()
    {
        int starter = -1;
        int lastClass = 0;
        int write = 0;
        for (int read = 0; read < _count; read++)
        {
            int cp = _cps[read];
            int cc = UnicodeProperties.CombiningClass(cp);
            if (starter >= 0 && (lastClass < cc || lastClass == 0))
            {
                int composite = ComposePair(_cps[starter], cp);
                if (composite >= 0)
                {
                    _cps[starter] = composite;
                    continue;
                }
            }

            if (cc == 0)
            {
                starter = write;
            }

            lastClass = cc;
            _cps[write++] = cp;
        }

        _count = write;
    }

    private static int ComposePair(int first, int second)
    {
        int l = first - HangulLBase;
        if ((uint)l < HangulLCount)
        {
            int v = second - HangulVBase;
            if ((uint)v < HangulVCount)
            {
                return HangulSBase + (((l * HangulVCount) + v) * HangulTCount);
            }
        }

        int s = first - HangulSBase;
        if ((uint)s < HangulSCount && s % HangulTCount == 0)
        {
            int t = second - HangulTBase;
            if (t > 0 && t < HangulTCount)
            {
                return first + t;
            }
        }

        int at = UnicodeProperties.Find(UnicodeTables.ComposeKeys, ((long)first << 21) | (uint)second);
        return at >= 0 ? UnicodeTables.ComposeValues[at] : -1;
    }

    private bool TryEncode(Span<char> destination, out int written)
    {
        int w = 0;
        for (int i = 0; i < _count; i++)
        {
            int cp = _cps[i];
            if (cp < 0x10000)
            {
                if (w >= destination.Length)
                {
                    written = 0;
                    return false;
                }

                destination[w++] = (char)cp;
            }
            else
            {
                if (w + 1 >= destination.Length)
                {
                    written = 0;
                    return false;
                }

                destination[w++] = (char)(0xD7C0 + (cp >> 10));
                destination[w++] = (char)(0xDC00 + (cp & 0x3FF));
            }
        }

        written = w;
        return true;
    }

    // Latin letters without a decomposition, folded to their plain ASCII spelling. Both cases are listed because the filter may
    // run before or after case folding.
    private void ApplyAsciiTable()
    {
        if (_spare.Length < _count * 2)
        {
            _spare = new int[Math.Max(_count * 2, _cps.Length)];
        }

        int[] output = _spare;
        int w = 0;
        for (int r = 0; r < _count; r++)
        {
            int cp = _cps[r];
            string? replacement = cp < 0x80 ? null : AsciiReplacement(cp);
            if (replacement is null)
            {
                output[w++] = cp;
            }
            else
            {
                foreach (char c in replacement)
                {
                    output[w++] = c;
                }
            }
        }

        _spare = _cps;
        _cps = output;
        _count = w;
    }

    private static string? AsciiReplacement(int cp) => cp switch
    {
        0x00C6 => "AE", 0x00E6 => "ae", 0x0152 => "OE", 0x0153 => "oe", 0x00DF => "ss", 0x1E9E => "SS",
        0x00D8 => "O", 0x00F8 => "o", 0x0110 => "D", 0x0111 => "d", 0x00D0 => "D", 0x00F0 => "d",
        0x0141 => "L", 0x0142 => "l", 0x0126 => "H", 0x0127 => "h", 0x0131 => "i", 0x0166 => "T", 0x0167 => "t",
        0x00DE => "TH", 0x00FE => "th", 0x0138 => "q", 0x014A => "N", 0x014B => "n", 0x0180 => "b", 0x0197 => "I",
        0x0268 => "i", 0x019A => "l", 0x0243 => "B", 0x0244 => "U", 0x0246 => "E", 0x0247 => "e", 0x0248 => "J", 0x0249 => "j",
        0x024C => "R", 0x024D => "r", 0x024E => "Y", 0x024F => "y", 0x1D6C => "b", 0x1D6D => "d", 0x1D6E => "f", 0x1D80 => "b",
        0x2018 or 0x2019 or 0x201B => "'", 0x201C or 0x201D => "\"", 0x2010 or 0x2011 or 0x2012 or 0x2013 or 0x2014 => "-",
        _ => null,
    };
}
