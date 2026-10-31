namespace OneIndex.Analysis.Snowball;

/// <summary>
/// Reduces a word to its stem. The stemmers implement the Snowball algorithms (see docs/analysis.md for the version of each
/// script). An instance keeps a working buffer between calls and is not thread-safe; the returned span is valid until the next call.
/// </summary>
public abstract class Stemmer
{
    /// <summary>The language name the stemmer is registered under, in English and lower case (for example <c>english</c>).</summary>
    public abstract string Language { get; }

    /// <summary>Stems a word that is already lower-cased (the algorithms expect lower-case input).</summary>
    /// <param name="word">The word.</param>
    /// <returns>The stem. Words the algorithm leaves alone come back unchanged. Valid until the next call on this instance.</returns>
    public abstract ReadOnlySpan<char> Stem(ReadOnlySpan<char> word);
}

/// <summary>A set of characters, as the Snowball <c>groupings</c> declaration defines.</summary>
internal sealed class Grouping
{
    private readonly char _min;
    private readonly char _max;
    private readonly ulong[] _bits;

    internal Grouping(string members)
    {
        _min = char.MaxValue;
        _max = char.MinValue;
        foreach (char c in members)
        {
            _min = c < _min ? c : _min;
            _max = c > _max ? c : _max;
        }

        _bits = new ulong[(((_max - _min) + 64) / 64)];
        foreach (char c in members)
        {
            int i = c - _min;
            _bits[i >> 6] |= 1UL << (i & 63);
        }
    }

    internal bool Contains(char c)
    {
        int i = c - _min;
        return (uint)i <= (uint)(_max - _min) && (_bits[i >> 6] & (1UL << (i & 63))) != 0;
    }
}

/// <summary>A Snowball string variable.</summary>
internal sealed class StringVariable
{
    internal char[] Buffer = new char[16];
    internal int Length;

    internal ReadOnlySpan<char> Value => Buffer.AsSpan(0, Length);

    internal void Set(ReadOnlySpan<char> value)
    {
        if (value.Length > Buffer.Length)
        {
            Buffer = new char[Math.Max(value.Length, Buffer.Length * 2)];
        }

        value.CopyTo(Buffer);
        Length = value.Length;
    }
}

/// <summary>One string of an <c>among</c> list: the text, the number of its command group, and the routine that must succeed (0 for none).</summary>
internal readonly struct AmongEntry(string text, int group, int routine)
{
    internal readonly string Text = text;
    internal readonly int Group = group;
    internal readonly int Routine = routine;
}

/// <summary>
/// The Snowball string machine, written from the language manual: a text buffer, a cursor <c>C</c> and two limits (<c>L</c> to the
/// right, <c>Lb</c> to the left), slice ends <c>Bra</c> and <c>Ket</c>, and the primitives the generated stemmers are built from.
/// Characters are UTF-16 code units, which is exact for every script the algorithms handle (all in the Basic Multilingual Plane).
/// <para>
/// Forward routines move the cursor right towards <c>L</c>; backward routines (declared in <c>backwardmode</c>) move it left
/// towards <c>Lb</c>. The generated code saves and restores the cursor itself; in backward code it saves the distance from the end of
/// the text, which survives edits made to the left.
/// </para>
/// </summary>
internal abstract class SnowballProgram : Stemmer
{
    internal char[] B = new char[64];
    internal int Len;
    internal int C;
    internal int L;
    internal int Lb;
    internal int Bra;
    internal int Ket;

    /// <summary>The generated <c>stem</c> routine.</summary>
    protected abstract bool Run();

    /// <summary>Evaluates the routine that an <c>among</c> entry names. The generated override switches on <paramref name="routine"/>.</summary>
    protected virtual bool AmongRoutine(int routine) => true;

    /// <inheritdoc/>
    public sealed override ReadOnlySpan<char> Stem(ReadOnlySpan<char> word)
    {
        if (word.Length > B.Length)
        {
            B = new char[Math.Max(word.Length, B.Length * 2)];
        }

        word.CopyTo(B);
        Len = L = word.Length;
        C = 0;
        Lb = 0;
        Bra = 0;
        Ket = 0;
        _ = Run();
        return B.AsSpan(0, Len);
    }

    // --- Matching ---------------------------------------------------------------------------------------------------------

    /// <summary>A literal string, going forward.</summary>
    protected bool EqS(ReadOnlySpan<char> s)
    {
        if (L - C < s.Length || !B.AsSpan(C, s.Length).SequenceEqual(s))
        {
            return false;
        }

        C += s.Length;
        return true;
    }

    /// <summary>A literal string, going backward (the text is still written forwards).</summary>
    protected bool EqSB(ReadOnlySpan<char> s)
    {
        if (C - Lb < s.Length || !B.AsSpan(C - s.Length, s.Length).SequenceEqual(s))
        {
            return false;
        }

        C -= s.Length;
        return true;
    }

    protected bool InGrouping(Grouping g)
    {
        if (C >= L || !g.Contains(B[C]))
        {
            return false;
        }

        C++;
        return true;
    }

    protected bool InGroupingB(Grouping g)
    {
        if (C <= Lb || !g.Contains(B[C - 1]))
        {
            return false;
        }

        C--;
        return true;
    }

    /// <summary><c>non G</c>: any character that is not in the grouping.</summary>
    protected bool OutGrouping(Grouping g)
    {
        if (C >= L || g.Contains(B[C]))
        {
            return false;
        }

        C++;
        return true;
    }

    protected bool OutGroupingB(Grouping g)
    {
        if (C <= Lb || g.Contains(B[C - 1]))
        {
            return false;
        }

        C--;
        return true;
    }

    protected bool Hop(int n)
    {
        if (n < 0 || L - C < n)
        {
            return false;
        }

        C += n;
        return true;
    }

    protected bool HopB(int n)
    {
        if (n < 0 || C - Lb < n)
        {
            return false;
        }

        C -= n;
        return true;
    }

    /// <summary>
    /// The longest entry that matches at the cursor (and whose routine succeeds), going forward. The entries are sorted by
    /// decreasing length. On a match the cursor moves past the text.
    /// </summary>
    /// <returns>The entry's group number (1-based), or 0 when nothing matches.</returns>
    protected int FindAmong(AmongEntry[] entries)
    {
        foreach (AmongEntry e in entries)
        {
            int n = e.Text.Length;
            if (L - C < n || !B.AsSpan(C, n).SequenceEqual(e.Text))
            {
                continue;
            }

            if (e.Routine != 0)
            {
                int saved = C;
                C += n;
                bool ok = AmongRoutine(e.Routine);
                C = saved;
                if (!ok)
                {
                    continue;
                }
            }

            C += n;
            return e.Group;
        }

        return 0;
    }

    /// <summary>Like <see cref="FindAmong"/>, going backward: the entry text ends at the cursor.</summary>
    protected int FindAmongB(AmongEntry[] entries)
    {
        foreach (AmongEntry e in entries)
        {
            int n = e.Text.Length;
            if (C - Lb < n || !B.AsSpan(C - n, n).SequenceEqual(e.Text))
            {
                continue;
            }

            if (e.Routine != 0)
            {
                int saved = Len - C;
                C -= n;
                bool ok = AmongRoutine(e.Routine);
                C = Len - saved;
                if (!ok)
                {
                    continue;
                }
            }

            C -= n;
            return e.Group;
        }

        return 0;
    }

    // --- Editing ----------------------------------------------------------------------------------------------------------

    /// <summary><c>&lt;- s</c>: replaces the slice. Fails when the slice ends are not set.</summary>
    protected bool SliceFrom(ReadOnlySpan<char> s)
    {
        if (Bra < 0 || Bra > Ket || Ket > Len)
        {
            return false;
        }

        int adjust = s.Length - (Ket - Bra);
        if (adjust != 0)
        {
            if (Len + adjust > B.Length)
            {
                Array.Resize(ref B, Math.Max(Len + adjust, B.Length * 2));
            }

            B.AsSpan(Ket, Len - Ket).CopyTo(B.AsSpan(Ket + adjust));
        }

        s.CopyTo(B.AsSpan(Bra));
        Len += adjust;
        if (Ket <= L)
        {
            L += adjust;
        }

        // The cursor follows the text it was after; one inside the replaced text moves to its start.
        if (C >= Ket)
        {
            C += adjust;
        }
        else if (C > Bra)
        {
            C = Bra;
        }

        Ket += adjust;
        return true;
    }

    protected bool SliceDel() => SliceFrom([]);

    /// <summary><c>-&gt; v</c>: copies the slice to a string variable.</summary>
    protected bool SliceTo(StringVariable v)
    {
        if (Bra < 0 || Bra > Ket || Ket > Len)
        {
            return false;
        }

        v.Set(B.AsSpan(Bra, Ket - Bra));
        return true;
    }

    /// <summary>
    /// <c>insert</c> and <c>attach</c>: inserts text at the cursor. Going forward, <c>insert</c> leaves the cursor after the new text
    /// and <c>attach</c> before it; going backward it is the other way round, so in both directions <c>insert</c> leaves the cursor on
    /// the side it is travelling towards.
    /// </summary>
    protected void InsertS(ReadOnlySpan<char> s, bool attach, bool backward)
    {
        int n = s.Length;
        if (Len + n > B.Length)
        {
            Array.Resize(ref B, Math.Max(Len + n, B.Length * 2));
        }

        B.AsSpan(C, Len - C).CopyTo(B.AsSpan(C + n));
        s.CopyTo(B.AsSpan(C));
        Len += n;
        if (C <= L)
        {
            L += n;
        }

        if (backward == attach)
        {
            C += n;
        }
    }
}
