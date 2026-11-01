using System.Buffers;
using System.Text;
using OneIndex.Codecs;

namespace OneIndex.Search;

/// <summary>How an indexed word matched a query word under typo tolerance and as-you-type matching.</summary>
/// <param name="Typos">The number of typos, 0 to 2 for an accepted match. A typo on the first character counts as two.</param>
/// <param name="IsPrefixExtension">Whether the match is the query word (with its typos) being the beginning of a longer indexed word.</param>
public readonly record struct TypoMatch(int Typos, bool IsPrefixExtension)
{
    /// <summary>Whether the indexed word is the query word itself.</summary>
    public bool IsExact => Typos == 0 && !IsPrefixExtension;

    /// <summary>
    /// The score factor for typos: <c>1 / (1 + typos)</c>, so a word with no typos keeps its full score, one typo halves it and two
    /// typos leave a third.
    /// </summary>
    public float TypoFactor => 1f / (1 + Typos);

    /// <summary>The score factor for exactness: 1 for a whole-word match, <see cref="TypoRanking.PrefixExactness"/> for a longer word that only begins like the query word.</summary>
    public float ExactnessFactor => IsPrefixExtension ? TypoRanking.PrefixExactness : 1f;

    /// <summary>The product of <see cref="TypoFactor"/> and <see cref="ExactnessFactor"/>: the boost an expanded word's score gets.</summary>
    public float Factor => TypoFactor * ExactnessFactor;
}

/// <summary>The cost model of typo tolerance and the reference computation of it (see <c>docs/typo-tolerance.md</c>).</summary>
public static class TypoRanking
{
    /// <summary>The exactness factor of a match that only begins like the query word. Default 0.8.</summary>
    public const float PrefixExactness = 0.8f;

    /// <summary>
    /// The typos between a query word and an indexed word: the optimal-string-alignment distance in code points (insertion, deletion,
    /// substitution, and the swap of two adjacent characters each cost one), plus one when the first characters differ, because a typo
    /// on the first character counts as two.
    /// </summary>
    /// <param name="query">The query word.</param>
    /// <param name="word">The indexed word.</param>
    /// <returns>The number of typos (can exceed 2; callers compare with their budget).</returns>
    public static int Typos(string query, string word)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(word);
        int[] q = CodePoints(query);
        int[] w = CodePoints(word);
        return Typos(q, w, w.Length);
    }

    /// <summary>
    /// The smallest number of typos over the non-empty beginnings of an indexed word: how well the query word matches as the start of
    /// the word (prefix Levenshtein).
    /// </summary>
    /// <param name="query">The query word.</param>
    /// <param name="word">The indexed word.</param>
    /// <returns>The number of typos, and the length in code points of the beginning that achieved it (the shortest one on ties).</returns>
    public static (int Typos, int Length) PrefixTypos(string query, string word)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(word);
        int[] q = CodePoints(query);
        int[] w = CodePoints(word);
        return PrefixTypos(q, w);
    }

    /// <summary>Classifies an indexed word against a query word.</summary>
    /// <param name="query">The query word.</param>
    /// <param name="word">The indexed word.</param>
    /// <param name="budget">The most typos allowed, 0 to 2.</param>
    /// <param name="prefix">Whether the indexed word may also match by beginning like the query word.</param>
    /// <returns>The match, or <see langword="null"/> when the word does not match.</returns>
    public static TypoMatch? Match(string query, string word, int budget, bool prefix)
        => Match(CodePoints(query), CodePoints(word), budget, prefix);

    /// <summary>
    /// <see cref="Match(string, string, int, bool)"/> on code points, in one pass over the alignment table and without allocating for
    /// words up to 255 code points. The last row of the table holds the distance from the query word to every beginning of the indexed
    /// word, so the whole word and the best beginning come from the same table. The best beginning is a strict improvement over the whole
    /// word exactly when the match is a prefix extension.
    /// </summary>
    /// <param name="q">The query word as code points.</param>
    /// <param name="w">The indexed word as code points.</param>
    /// <param name="budget">The most typos allowed, 0 to 2.</param>
    /// <param name="prefix">Whether the indexed word may also match by beginning like the query word.</param>
    /// <returns>The match, or <see langword="null"/>.</returns>
    internal static TypoMatch? Match(ReadOnlySpan<int> q, ReadOnlySpan<int> w, int budget, bool prefix)
    {
        int m = q.Length;
        int k = w.Length;
        int[]? rented = null;
        // Three rows of k + 1 integers; fixed small buffer on the stack, rented for longer words.
        Span<int> rows = 3 * (k + 1) <= 768 ? stackalloc int[768] : (rented = ArrayPool<int>.Shared.Rent(3 * (k + 1)));
        try
        {
            Span<int> prev2 = rows.Slice(0, k + 1);
            Span<int> prev = rows.Slice(k + 1, k + 1);
            Span<int> cur = rows.Slice(2 * (k + 1), k + 1);
            for (int j = 0; j <= k; j++)
            {
                prev[j] = j;
            }

            for (int i = 1; i <= m; i++)
            {
                cur[0] = i;
                for (int j = 1; j <= k; j++)
                {
                    int cost = q[i - 1] == w[j - 1] ? 0 : 1;
                    int v = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + cost);
                    if (i > 1 && j > 1 && q[i - 1] == w[j - 2] && q[i - 2] == w[j - 1])
                    {
                        v = Math.Min(v, prev2[j - 2] + 1);
                    }

                    cur[j] = v;
                }

                Span<int> spare = prev2;
                prev2 = prev;
                prev = cur;
                cur = spare;
            }

            // prev is now the row of the whole query word: prev[n] is its distance to the first n code points of the word.
            int firstPenalty = m > 0 && k > 0 && q[0] != w[0] ? 1 : 0; // a typo on the first character counts as two
            int whole = prev[k] + (k > 0 ? firstPenalty : 0);
            if (!prefix)
            {
                return whole <= budget ? new TypoMatch(whole, false) : null;
            }

            int best = int.MaxValue;
            for (int n = 1; n <= k; n++)
            {
                best = Math.Min(best, prev[n] + firstPenalty);
            }

            if (best == int.MaxValue || whole <= best)
            {
                return whole <= budget ? new TypoMatch(whole, false) : null;
            }

            return best <= budget ? new TypoMatch(best, true) : null;
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>The definition-by-definition match the fast one is tested against.</summary>
    internal static TypoMatch? MatchByDefinition(string query, string word, int budget, bool prefix)
    {
        int[] q = CodePoints(query);
        int[] w = CodePoints(word);
        int whole = Typos(q, w, w.Length);
        if (!prefix)
        {
            return whole <= budget ? new TypoMatch(whole, false) : null;
        }

        (int pre, int length) = PrefixTypos(q, w);
        if (whole <= pre)
        {
            return whole <= budget ? new TypoMatch(whole, false) : null;
        }

        return pre <= budget ? new TypoMatch(pre, length < w.Length) : null;
    }

    internal static int[] CodePoints(string s)
    {
        var list = new List<int>(s.Length);
        foreach (Rune r in s.EnumerateRunes())
        {
            list.Add(r.Value);
        }

        return [.. list];
    }

    private static int Typos(int[] q, int[] w, int wordLength)
    {
        int d = Osa(q, q.Length, w, wordLength);
        return d + (q.Length > 0 && wordLength > 0 && q[0] != w[0] ? 1 : 0);
    }

    private static (int, int) PrefixTypos(int[] q, int[] w)
    {
        int best = int.MaxValue;
        int bestLength = 0;
        for (int n = 1; n <= w.Length; n++)
        {
            int t = Typos(q, w, n);
            if (t < best)
            {
                best = t;
                bestLength = n;
            }
        }

        return best == int.MaxValue ? (q.Length + 1, 0) : (best, bestLength);
    }

    // Optimal string alignment distance (Damerau 1964 restricted form: a swapped pair is not edited again), the distance the
    // Levenshtein automaton implements. Rows are indexed by the first string, columns by the second.
    private static int Osa(int[] a, int n, int[] b, int m)
    {
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= m; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int v = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    v = Math.Min(v, d[i - 2, j - 2] + 1);
                }

                d[i, j] = v;
            }
        }

        return d[n, m];
    }
}

/// <summary>
/// A Levenshtein automaton that accepts every word with a non-empty beginning within the edit budget of the query word: once a
/// beginning is close enough, the rest of the word is free (prefix Levenshtein, which as-you-type matching with typos needs).
/// </summary>
internal sealed class PrefixLevenshteinAutomaton : TermAutomaton
{
    // States of the inner automaton are not negative, so this value marks "already matched, accept anything that follows".
    private const int Matched = int.MinValue;
    private readonly LevenshteinAutomaton _inner;

    public PrefixLevenshteinAutomaton(string word, int maxEdits, bool transpositions = true)
    {
        _inner = new LevenshteinAutomaton(word, maxEdits, transpositions);
    }

    public override int MaxStateLength => _inner.MaxStateLength;

    public override int Start(Span<int> state) => _inner.Start(state);

    public override int Transition(ReadOnlySpan<int> state, int codePoint, Span<int> destination)
    {
        if (state.Length == 1 && state[0] == Matched)
        {
            destination[0] = Matched;
            return 1;
        }

        int n = _inner.Transition(state, codePoint, destination);
        if (n > 0 && _inner.IsAccepting(destination[..n]))
        {
            destination[0] = Matched;
            return 1;
        }

        return n;
    }

    public override bool IsAccepting(ReadOnlySpan<int> state) => (state.Length == 1 && state[0] == Matched) || _inner.IsAccepting(state);

    internal override int TryGetViableBytes(ReadOnlySpan<int> state, Span<byte> bytes)
        => state.Length == 1 && state[0] == Matched ? -1 : _inner.TryGetViableBytes(state, bytes);
}

/// <summary>Finds the indexed words that can match a query word, with the automata of the term dictionary.</summary>
internal static class TypoCandidates
{
    /// <summary>
    /// Plans the search for a superset of the words whose <see cref="TypoRanking.Match(string, string, int, bool)"/> is within the budget;
    /// the caller applies the exact cost. A typo on the first character costs two, so with one typo the first character must agree, and
    /// with two the words that differ in the first character may have only one edit: two passes, the second without a fixed first
    /// character. The automata are built once and serve every segment and field.
    /// </summary>
    public sealed class Plan
    {
        private readonly byte[] _exact;
        private readonly int _budget;
        private readonly bool _prefix;
        private readonly int _firstLength;
        private readonly TermAutomaton? _first;
        private readonly TermAutomaton? _second;

        public Plan(string word, int budget, bool prefix)
        {
            _exact = word.Length == 0 ? [] : Encoding.UTF8.GetBytes(word);
            _budget = budget;
            _prefix = prefix;
            if (word.Length == 0 || budget == 0)
            {
                return;
            }

            _firstLength = Rune.GetRuneAt(word, 0).Utf8SequenceLength;
            _first = prefix ? new PrefixLevenshteinAutomaton(word, budget) : new LevenshteinAutomaton(word, budget);
            if (budget == 2)
            {
                _second = prefix ? new PrefixLevenshteinAutomaton(word, 1) : new LevenshteinAutomaton(word, 1);
            }
        }

        public void Collect(ITextFieldReader reader, TermSink sink)
        {
            if (_exact.Length == 0)
            {
                return;
            }

            if (_budget == 0)
            {
                if (_prefix)
                {
                    Drain(reader.GetTerms(_exact), sink);
                }
                else if (reader.TryGetTerm(_exact, out TermInfo info))
                {
                    sink(_exact, info);
                }

                return;
            }

            Drain(reader.Intersect(_first!, _exact.AsSpan(0, _firstLength)), sink);
            if (_second is not null)
            {
                Drain(reader.Intersect(_second), sink);
            }
        }
    }

    private static void Drain(ITermEnumerator terms, TermSink sink)
    {
        using (terms)
        {
            while (terms.MoveNext())
            {
                sink(terms.Term, terms.Info);
            }
        }
    }
}

/// <summary>Wraps the weight of one expanded word so that an explanation names the typo and exactness factors behind its boost.</summary>
internal sealed class TypoMatchWeight(Weight inner, string queryWord, string word, TypoMatch match) : Weight
{
    public override Scorer? GetScorer(LeafContext leaf) => inner.GetScorer(leaf);

    public override Explanation? Explain(LeafContext leaf, int docId)
    {
        Explanation? e = inner.Explain(leaf, docId);
        if (e is null)
        {
            return null;
        }

        string how = match.IsExact ? "whole word" : match.IsPrefixExtension ? "beginning of a longer word" : "whole word with typos";
        string text = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"query word '{queryWord}' matched '{word}' ({how}, {match.Typos} typos): typo factor {match.TypoFactor:G9} = 1 / (1 + typos), exactness factor {match.ExactnessFactor:G9}, boost {match.Factor:G9}");
        return new Explanation(e.Value, text, e);
    }
}
