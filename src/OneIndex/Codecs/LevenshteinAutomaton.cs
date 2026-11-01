using System.Text;

namespace OneIndex.Codecs;

/// <summary>
/// A deterministic recogniser over the code points of a term, which a term dictionary intersects with its automaton to list the
/// matching terms without scanning the others (Schulz and Mihov 2002, §1: the dictionary is walked together with the automaton
/// and a branch is abandoned when the automaton is dead). A state is a small set of integers the automaton defines; an empty set
/// is the dead state. An instance may keep scratch space, so use one per search and never share it between threads.
/// </summary>
public abstract class TermAutomaton
{
    /// <summary>The largest number of integers any state holds; callers size their buffers with it.</summary>
    public abstract int MaxStateLength { get; }

    /// <summary>Writes the start state.</summary>
    /// <param name="state">At least <see cref="MaxStateLength"/> long.</param>
    /// <returns>The number of integers written; 0 when nothing can match.</returns>
    public abstract int Start(Span<int> state);

    /// <summary>Computes the state after reading a code point.</summary>
    /// <param name="state">The current state.</param>
    /// <param name="codePoint">The code point read (a Unicode scalar value).</param>
    /// <param name="destination">Receives the next state; at least <see cref="MaxStateLength"/> long and not overlapping <paramref name="state"/>.</param>
    /// <returns>The number of integers written; 0 for the dead state.</returns>
    public abstract int Transition(ReadOnlySpan<int> state, int codePoint, Span<int> destination);

    /// <summary>Whether a term that ends in <paramref name="state"/> matches.</summary>
    /// <param name="state">The state after the whole term.</param>
    /// <returns><see langword="true"/> when the term matches.</returns>
    public abstract bool IsAccepting(ReadOnlySpan<int> state);

    /// <summary>
    /// Lets a term dictionary that can look up an arc by its label go straight to the arcs that keep the automaton alive. When every
    /// code point outside a small set kills the state, the first UTF-8 bytes of that set are returned in increasing order, and the
    /// dictionary skips every other arc without reading it. The default says nothing is known. The bytes may include ones that lead
    /// to a dead state once the code point is complete, never leave out one that does not.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="bytes">Receives the bytes in increasing order, without repeats; at least 8 long, and no state needs more.</param>
    /// <returns>How many bytes were written, or -1 when any arc may keep the state alive.</returns>
    internal virtual int TryGetViableBytes(ReadOnlySpan<int> state, Span<byte> bytes) => -1;
}

/// <summary>
/// The Levenshtein automaton of a word: accepts the terms within a number of edits of it, counting a substitution, an insertion
/// and a deletion of one code point as one edit each, and optionally the swap of two adjacent code points as one edit. The
/// non-deterministic automaton of Schulz and Mihov (2002, §2) has a state for every (position in the word, edits used) pair; a set
/// of them is a state of the deterministic automaton. What a code point does to such a set depends only on the set, shifted to
/// start at its smallest position, and on which of the next few positions of the word hold that code point (their §4: the
/// "characteristic vector" of the code point against a window of the word). So the transitions are computed once for every
/// shape and edit budget, shared by all automata, and a step costs a comparison of the code point with a handful of letters and
/// one table read. With swaps the distance is the "optimal string alignment" distance: a swapped pair is not edited again.
/// </summary>
/// <remarks>
/// The state is two integers: the number of a shape and the word position it starts at. They are meaningful only to the
/// automaton that wrote them.
/// </remarks>
public sealed class LevenshteinAutomaton : TermAutomaton
{
    private readonly int[] _word;
    private readonly LevenshteinTables _tables;
    private readonly int _window;
    private readonly ulong[]? _asciiPositions; // for words of up to 64 code points: bit i of entry c is set when the word holds c at position i
    private readonly bool _hasNonAscii;
    private readonly long[] _viableKeys = new long[64]; // a small direct-mapped cache of TryGetViableBytes answers, keyed by (shape, origin)
    private readonly ulong[] _viableBytes = new ulong[64];
    private readonly sbyte[] _viableCounts = new sbyte[64];

    /// <summary>Builds the automaton.</summary>
    /// <param name="word">The word to match against.</param>
    /// <param name="maxEdits">The most edits allowed, from 0 to 3.</param>
    /// <param name="transpositions">Whether swapping two adjacent code points costs one edit instead of two.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxEdits"/> is outside 0 to 3.</exception>
    public LevenshteinAutomaton(string word, int maxEdits, bool transpositions = true)
    {
        ArgumentNullException.ThrowIfNull(word);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEdits);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxEdits, 3);
        var cps = new List<int>(word.Length);
        foreach (Rune r in word.EnumerateRunes())
        {
            cps.Add(r.Value);
        }

        _word = [.. cps];
        _tables = LevenshteinTables.Get(maxEdits, transpositions);
        _window = _tables.Window;
        foreach (int cp in _word)
        {
            _hasNonAscii |= cp >= 128;
        }

        if (_word.Length <= 64)
        {
            _asciiPositions = new ulong[128];
            for (int i = 0; i < _word.Length; i++)
            {
                if (_word[i] < 128)
                {
                    _asciiPositions[_word[i]] |= 1UL << i;
                }
            }
        }
    }

    /// <summary>The number of code points in the word.</summary>
    public int WordLength => _word.Length;

    /// <summary>A state is a shape number and a word position, two integers.</summary>
    public override int MaxStateLength => 2;

    /// <inheritdoc/>
    public override int Start(Span<int> state)
    {
        state[0] = _tables.StartShape(Math.Min(_word.Length, _window));
        state[1] = 0;
        return 2;
    }

    /// <inheritdoc/>
    public override int Transition(ReadOnlySpan<int> state, int codePoint, Span<int> destination)
    {
        if (state.Length != 2)
        {
            return 0;
        }

        int origin = state[1];
        int next = _tables.Next(state[0], Key(origin, codePoint));
        if (next < 0)
        {
            return 0;
        }

        destination[0] = next >> 8;
        destination[1] = origin + (next & 0xFF);
        return 2;
    }

    /// <inheritdoc/>
    public override bool IsAccepting(ReadOnlySpan<int> state)
    {
        if (state.Length != 2)
        {
            return false;
        }

        int distance = _word.Length - state[1];
        return (uint)distance < 32 && ((_tables.AcceptMask(state[0]) >> distance) & 1) != 0;
    }

    // The table key of a code point at a position: bit j of the vector says the word holds the code point at origin+j (j below
    // the window), and the count says how many of the window's positions lie inside the word.
    private int Key(int origin, int codePoint)
    {
        int available = Math.Min(_word.Length - origin, _window);
        int vector = 0;
        if (codePoint < 128 && _asciiPositions is not null)
        {
            vector = (int)((_asciiPositions[codePoint] >> origin) & ((1UL << available) - 1));
        }
        else if (codePoint >= 128 && !_hasNonAscii)
        {
            // Nothing in the word is outside ASCII, so this code point is nowhere in it.
        }
        else
        {
            for (int j = 0; j < available; j++)
            {
                if (_word[origin + j] == codePoint)
                {
                    vector |= 1 << j;
                }
            }
        }

        return (vector << 4) | available;
    }

    // A state is narrow when a code point that is not in the window kills it: with no edit left, only the word's own letters go on.
    internal override int TryGetViableBytes(ReadOnlySpan<int> state, Span<byte> bytes)
    {
        if (state.Length != 2)
        {
            return -1;
        }

        int shape = state[0];
        int origin = state[1];
        long id = (((long)origin << 32) | (uint)shape) + 1; // never 0, so an empty slot (0) is not a hit
        int slot = (int)((uint)((shape * 31) + (origin * 17)) % 64);
        if (_viableKeys[slot] == id)
        {
            int cached = _viableCounts[slot];
            ulong packed = _viableBytes[slot];
            for (int k = 0; k < cached; k++)
            {
                bytes[k] = (byte)(packed >> (8 * k));
            }

            return cached;
        }

        int count = ComputeViable(shape, origin, bytes);
        ulong store = 0;
        for (int k = 0; k < count; k++)
        {
            store |= (ulong)bytes[k] << (8 * k);
        }

        _viableKeys[slot] = id;
        _viableBytes[slot] = store;
        _viableCounts[slot] = (sbyte)count;
        return count;
    }

    private int ComputeViable(int shape, int origin, Span<byte> bytes)
    {
        int available = Math.Min(_word.Length - origin, _window);
        if (_tables.Next(shape, available) >= 0)
        {
            // The key of an empty vector is a code point that is nowhere in the window.
            return -1;
        }

        int count = 0;
        for (int j = 0; j < available; j++)
        {
            int cp = _word[origin + j];
            if (_tables.Next(shape, Key(origin, cp)) < 0)
            {
                continue;
            }

            byte lead = (byte)(cp < 0x80 ? cp : cp < 0x800 ? 0xC0 | (cp >> 6) : cp < 0x10000 ? 0xE0 | (cp >> 12) : 0xF0 | (cp >> 18));
            int at = 0;
            while (at < count && bytes[at] < lead)
            {
                at++;
            }

            if (at < count && bytes[at] == lead)
            {
                continue;
            }

            for (int m = count; m > at; m--)
            {
                bytes[m] = bytes[m - 1];
            }

            bytes[at] = lead;
            count++;
        }

        return count;
    }
}

/// <summary>
/// The shapes and transitions of the Levenshtein automata for one edit budget, shared by every automaton with that budget (and so
/// by every thread). A shape is a set of (offset, edits, pending swap) triples relative to the smallest position in it. A
/// transition for a shape and a key (see <see cref="LevenshteinAutomaton"/>) is computed the first time it is needed, under a
/// lock, and published with a volatile write; readers take no lock. The computation is deterministic, so two threads that race
/// would store the same number.
/// </summary>
internal sealed class LevenshteinTables
{
    private const int Unknown = -2;
    private static readonly LevenshteinTables?[] Cache = new LevenshteinTables?[8];
    private readonly int _maxEdits;
    private readonly bool _transpositions;
    private readonly Lock _lock = new();
    private readonly Dictionary<int[], int> _numbers = new(SetComparer.Instance);
    private readonly int[] _starts;
    private readonly int[] _scratch; // used under the lock only; a set holds at most two entries (plain and pending) per offset, and offsets stay within the window plus one
    private Shape[] _shapes = new Shape[16];
    private int _shapeCount;

    private LevenshteinTables(int maxEdits, bool transpositions)
    {
        _maxEdits = maxEdits;
        _transpositions = transpositions;
        // A pair swap reads the letter after the one a position holds, and positions run up to twice the budget ahead of the smallest.
        Window = (2 * maxEdits) + 2;
        _scratch = new int[2 * (Window + 2)];
        _starts = new int[Window + 1];
        Array.Fill(_starts, Unknown);
    }

    /// <summary>The number of word positions a key describes.</summary>
    public int Window { get; }

    public static LevenshteinTables Get(int maxEdits, bool transpositions)
    {
        int index = (maxEdits * 2) + (transpositions ? 1 : 0);
        LevenshteinTables? tables = Volatile.Read(ref Cache[index]);
        if (tables is null)
        {
            Interlocked.CompareExchange(ref Cache[index], new LevenshteinTables(maxEdits, transpositions), null);
            tables = Volatile.Read(ref Cache[index])!;
        }

        return tables;
    }

    /// <summary>The shape of the start state for a word with <paramref name="available"/> positions inside the window (the word's length, up to the window).</summary>
    public int StartShape(int available)
    {
        int v = Volatile.Read(ref _starts[available]);
        if (v != Unknown)
        {
            return v;
        }

        lock (_lock)
        {
            int n = 0;
            Closure(_scratch, ref n, 0, 0, available);
            v = Intern(_scratch.AsSpan(0, n));
            Volatile.Write(ref _starts[available], v);
            return v;
        }
    }

    /// <summary>Bit d of the result is set when a term that ends d positions before the end of the word is accepted in this shape.</summary>
    public int AcceptMask(int shape) => Volatile.Read(ref _shapes)[shape].Accept;

    /// <summary>The next shape and the shift of the origin as <c>(shape &lt;&lt; 8) | shift</c>, or -1 for the dead state.</summary>
    public int Next(int shape, int key)
    {
        int[] table = Volatile.Read(ref _shapes)[shape].Table;
        int v = Volatile.Read(ref table[key]);
        return v != Unknown ? v : Compute(shape, key);
    }

    private int Compute(int shape, int key)
    {
        lock (_lock)
        {
            Shape s = _shapes[shape];
            int available = key & 15;
            int vector = key >> 4;
            int[] next = _scratch;
            int n = 0;
            foreach (int element in s.Elements)
            {
                int o = element >> 3;
                int e = (element >> 1) & 3;
                if ((element & 1) != 0)
                {
                    // Second code point of a swap: it must be the word's code point here.
                    if (Has(vector, o))
                    {
                        Closure(next, ref n, o + 2, e, available);
                    }

                    continue;
                }

                if (Has(vector, o))
                {
                    Closure(next, ref n, o + 1, e, available);
                }

                if (e < _maxEdits)
                {
                    if (o < available)
                    {
                        Closure(next, ref n, o + 1, e + 1, available); // substitution
                    }

                    Closure(next, ref n, o, e + 1, available); // insertion of the input code point
                    if (_transpositions && o + 1 < available && Has(vector, o + 1))
                    {
                        Add(next, ref n, o, e + 1, pending: true);
                    }
                }
            }

            int result;
            if (n == 0)
            {
                result = -1;
            }
            else
            {
                int shift = int.MaxValue;
                for (int k = 0; k < n; k++)
                {
                    shift = Math.Min(shift, next[k] >> 3);
                }

                for (int k = 0; k < n; k++)
                {
                    next[k] -= shift << 3;
                }

                result = (Intern(next.AsSpan(0, n)) << 8) | shift;
            }

            Volatile.Write(ref s.Table[key], result);
            return result;
        }
    }

    private static bool Has(int vector, int offset) => offset < 31 && ((vector >> offset) & 1) != 0;

    // Adds (o, e) and everything reachable by deleting word letters without reading input (the automaton's epsilon moves), as long as
    // the position is inside the word.
    private void Closure(int[] set, ref int n, int o, int e, int available)
    {
        while (true)
        {
            Add(set, ref n, o, e, pending: false);
            if (o >= available || e >= _maxEdits)
            {
                return;
            }

            o++;
            e++;
        }
    }

    // Keeps the cheapest edit count per (offset, pending): a triple with more edits at the same place can never do better.
    private static void Add(int[] set, ref int n, int o, int e, bool pending)
    {
        int p = pending ? 1 : 0;
        for (int k = 0; k < n; k++)
        {
            int s = set[k];
            if ((s >> 3) == o && (s & 1) == p)
            {
                if (((s >> 1) & 3) > e)
                {
                    set[k] = (o << 3) | (e << 1) | p;
                }

                return;
            }
        }

        set[n++] = (o << 3) | (e << 1) | p;
    }

    // Called with the lock held.
    private int Intern(Span<int> set)
    {
        set.Sort();
        int[] key = set.ToArray();
        if (_numbers.TryGetValue(key, out int existing))
        {
            return existing;
        }

        int accept = 0;
        foreach (int s in key)
        {
            if ((s & 1) == 0)
            {
                accept |= 1 << (s >> 3);
            }
        }

        var table = new int[1 << (Window + 4)];
        Array.Fill(table, Unknown);
        int id = _shapeCount;
        Shape[] shapes = _shapes;
        if (id == shapes.Length)
        {
            shapes = new Shape[id * 2];
            Array.Copy(_shapes, shapes, id);
        }

        shapes[id] = new Shape(key, accept, table);
        Volatile.Write(ref _shapes, shapes); // published before any table entry names the new shape
        _shapeCount++;
        _numbers.Add(key, id);
        return id;
    }

    private sealed class Shape(int[] elements, int accept, int[] table)
    {
        public readonly int[] Elements = elements;
        public readonly int Accept = accept;
        public readonly int[] Table = table;
    }

    private sealed class SetComparer : IEqualityComparer<int[]>
    {
        public static readonly SetComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x is not null && y is not null && x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            uint h = 2166136261;
            foreach (int v in obj)
            {
                h = (h ^ (uint)v) * 16777619;
            }

            return (int)h;
        }
    }
}
