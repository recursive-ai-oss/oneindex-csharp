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

    /// <summary>The value of <see cref="StartStateId"/> for an automaton that does not intern its states.</summary>
    public const int NoStateIds = -2;

    /// <summary>The id of the dead state of an interning automaton.</summary>
    public const int DeadStateId = -1;

    /// <summary>
    /// For an automaton that interns its states (each distinct state gets a small integer id and its transitions are remembered, so a
    /// walk pays a table lookup per step instead of recomputing the state set), the id of the start state, or <see cref="DeadStateId"/>
    /// when nothing can match. The default is <see cref="NoStateIds"/>, and the walk then uses <see cref="Start"/> and <see cref="Transition"/>.
    /// </summary>
    public virtual int StartStateId => NoStateIds;

    /// <summary>The state id after reading a code point, for an interning automaton.</summary>
    /// <param name="stateId">A live state id.</param>
    /// <param name="codePoint">The code point read (a Unicode scalar value).</param>
    /// <returns>The next state id, or <see cref="DeadStateId"/>.</returns>
    /// <exception cref="NotSupportedException">The automaton does not intern its states.</exception>
    public virtual int NextStateId(int stateId, int codePoint) => throw new NotSupportedException();

    /// <summary>Whether a term that ends in the state with the given id matches, for an interning automaton.</summary>
    /// <param name="stateId">A live state id.</param>
    /// <returns><see langword="true"/> when the term matches.</returns>
    /// <exception cref="NotSupportedException">The automaton does not intern its states.</exception>
    public virtual bool IsAcceptingId(int stateId) => throw new NotSupportedException();
}

/// <summary>
/// The Levenshtein automaton of a word: accepts the terms within a number of edits of it, counting a substitution, an insertion
/// and a deletion of one code point as one edit each, and optionally the swap of two adjacent code points as one edit. A state
/// is the set of positions of the non-deterministic automaton of Schulz and Mihov (2002, §2) that the input so far can be in,
/// as (word position, edits used) pairs; the subset construction is done lazily, one step at a time, rather than from the
/// precomputed parametric tables of their §4. With swaps the distance is the "optimal string alignment" distance: a swapped pair
/// is not edited again.
/// </summary>
public sealed class LevenshteinAutomaton : TermAutomaton
{
    // A state integer is (position << 3) | (edits << 1) | pending. A pending state has consumed the first code point of a swap
    // (the code point at position+1) and waits for the one at position; its edit is already counted.
    private readonly int[] _word;
    private readonly int _maxEdits;
    private readonly bool _transpositions;

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
        _maxEdits = maxEdits;
        _transpositions = transpositions;
    }

    /// <summary>The number of code points in the word.</summary>
    public int WordLength => _word.Length;

    /// <inheritdoc/>
    public override int MaxStateLength => 2 * (_word.Length + 1);

    /// <inheritdoc/>
    public override int Start(Span<int> state)
    {
        int n = 0;
        AddClosure(state, ref n, 0, 0);
        return n;
    }

    /// <inheritdoc/>
    public override int Transition(ReadOnlySpan<int> state, int codePoint, Span<int> destination)
    {
        int n = 0;
        int len = _word.Length;
        foreach (int s in state)
        {
            int i = s >> 3;
            int e = (s >> 1) & 3;
            if ((s & 1) != 0)
            {
                // Second code point of a swap: it must be the word's code point at i.
                if (_word[i] == codePoint)
                {
                    AddClosure(destination, ref n, i + 2, e);
                }

                continue;
            }

            if (i < len && _word[i] == codePoint)
            {
                AddClosure(destination, ref n, i + 1, e);
            }

            if (e < _maxEdits)
            {
                if (i < len)
                {
                    AddClosure(destination, ref n, i + 1, e + 1); // substitution
                }

                AddClosure(destination, ref n, i, e + 1); // insertion of the input code point
                if (_transpositions && i + 1 < len && _word[i + 1] == codePoint)
                {
                    Add(destination, ref n, i, e + 1, pending: true);
                }
            }
        }

        return n;
    }

    /// <inheritdoc/>
    // Interned states. A state set is identified by its sorted integers; the table of an id remembers the next id for every ASCII
    // code point once computed, which is almost every step of a walk over Latin-script terms.
    private const int Unknown = -3;
    private readonly List<int[]> _sets = [];
    private readonly List<int[]> _asciiNext = [];
    private readonly List<bool> _accepting = [];
    private readonly Dictionary<int[], int> _ids = new(IntSetComparer.Instance);
    private int _startId = Unknown;

    /// <inheritdoc/>
    public override int StartStateId
    {
        get
        {
            if (_startId == Unknown)
            {
                var buffer = new int[MaxStateLength];
                _startId = Intern(buffer, Start(buffer));
            }

            return _startId;
        }
    }

    /// <inheritdoc/>
    public override int NextStateId(int stateId, int codePoint)
    {
        int[] table = _asciiNext[stateId];
        if ((uint)codePoint < 128 && table[codePoint] != Unknown)
        {
            return table[codePoint];
        }

        var buffer = new int[MaxStateLength];
        int next = Intern(buffer, Transition(_sets[stateId], codePoint, buffer));
        if ((uint)codePoint < 128)
        {
            table[codePoint] = next;
        }

        return next;
    }

    /// <inheritdoc/>
    public override bool IsAcceptingId(int stateId) => _accepting[stateId];

    private int Intern(int[] buffer, int length)
    {
        if (length == 0)
        {
            return DeadStateId;
        }

        int[] set = buffer.AsSpan(0, length).ToArray();
        Array.Sort(set);
        if (_ids.TryGetValue(set, out int id))
        {
            return id;
        }

        id = _sets.Count;
        _sets.Add(set);
        var table = new int[128];
        Array.Fill(table, Unknown);
        _asciiNext.Add(table);
        _accepting.Add(IsAccepting(set));
        _ids.Add(set, id);
        return id;
    }

    private sealed class IntSetComparer : IEqualityComparer<int[]>
    {
        public static readonly IntSetComparer Instance = new();

        public bool Equals(int[]? x, int[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(int[] obj)
        {
            var h = default(HashCode);
            foreach (int v in obj)
            {
                h.Add(v);
            }

            return h.ToHashCode();
        }
    }

    /// <inheritdoc/>
    public override bool IsAccepting(ReadOnlySpan<int> state)
    {
        foreach (int s in state)
        {
            if ((s & 1) == 0 && (s >> 3) == _word.Length)
            {
                return true;
            }
        }

        return false;
    }

    // Adds (i, e) and everything reachable by deleting word code points without reading input (the automaton's epsilon moves).
    private void AddClosure(Span<int> set, ref int n, int i, int e)
    {
        while (true)
        {
            Add(set, ref n, i, e, pending: false);
            if (i >= _word.Length || e >= _maxEdits)
            {
                return;
            }

            i++;
            e++;
        }
    }

    // Keeps the cheapest edit count per (position, pending): a state with more edits at the same position can never do better.
    private static void Add(Span<int> set, ref int n, int i, int e, bool pending)
    {
        int p = pending ? 1 : 0;
        for (int k = 0; k < n; k++)
        {
            int s = set[k];
            if ((s >> 3) == i && (s & 1) == p)
            {
                if (((s >> 1) & 3) > e)
                {
                    set[k] = (i << 3) | (e << 1) | p;
                }

                return;
            }
        }

        set[n++] = (i << 3) | (e << 1) | p;
    }
}
