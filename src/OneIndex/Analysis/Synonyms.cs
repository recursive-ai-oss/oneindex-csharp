namespace OneIndex.Analysis;

/// <summary>
/// Bounds on synonym handling. They keep a synonym table from turning one query into an unbounded expansion; the defaults are the
/// limits Meilisearch documents for its synonyms setting (a query phrase of 1 to 3 words is looked up; a term has at most 50
/// synonyms of at most 100 words together), plus a cap on the number of readings of one query.
/// </summary>
public sealed class SynonymLimits
{
    /// <summary>The longest run of consecutive query words that is looked up. Default 3; a longer run is never a rule's key.</summary>
    public int MaxQueryWords { get; init; } = 3;

    /// <summary>The most synonyms kept for one key; later ones are dropped, in the order they were added. Default 50.</summary>
    public int MaxSynonymsPerTerm { get; init; } = 50;

    /// <summary>The most words in all the synonyms of one key together; a synonym that would pass it and the ones after it are dropped. Default 100.</summary>
    public int MaxWordsPerTerm { get; init; } = 100;

    /// <summary>
    /// The most readings (paths through the token graph) one query may have, original reading included. Synonym edges are
    /// added left to right, shorter spans first, and one that would pass the cap is left out. Default 64.
    /// </summary>
    public int MaxPaths { get; init; } = 64;

    /// <summary>The shared default limits.</summary>
    public static SynonymLimits Default { get; } = new();
}

/// <summary>
/// Collects synonym rules as written by a person and builds a <see cref="SynonymMap"/>. Rules are not transitive: with
/// <c>a =&gt; b</c> and <c>b =&gt; c</c>, a query for <c>a</c> reads as <c>a</c> or <c>b</c>, never <c>c</c>.
/// </summary>
public sealed class SynonymRules
{
    private readonly List<(string From, string[] To)> _rules = [];

    /// <summary>The number of rules added.</summary>
    public int Count => _rules.Count;

    /// <summary>Adds a one-way rule: a query for <paramref name="from"/> also finds each of <paramref name="to"/>, not the other way round.</summary>
    /// <param name="from">The query words that trigger the rule; may be several words.</param>
    /// <param name="to">The alternatives; each may be several words, which then match as a phrase.</param>
    /// <returns>This instance.</returns>
    public SynonymRules AddOneWay(string from, params string[] to)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        _rules.Add((from, [.. to]));
        return this;
    }

    /// <summary>Adds a multi-way rule: each member reads as every other member (<c>shoe &lt;=&gt; boot &lt;=&gt; slipper</c>).</summary>
    /// <param name="members">At least two phrases.</param>
    /// <returns>This instance.</returns>
    public SynonymRules AddGroup(params string[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        for (int i = 0; i < members.Length; i++)
        {
            var others = new List<string>(members.Length - 1);
            for (int j = 0; j < members.Length; j++)
            {
                if (j != i)
                {
                    others.Add(members[j]);
                }
            }

            _rules.Add((members[i], [.. others]));
        }

        return this;
    }

    /// <summary>Adds rules in the shape of Meilisearch's synonyms setting: each key is a one-way rule to its list; a mutual association lists every member under every member.</summary>
    /// <param name="settings">The settings, in the order to apply them (use an ordered dictionary or a list of pairs for a deterministic order).</param>
    /// <returns>This instance.</returns>
    public SynonymRules AddAll(IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        foreach (KeyValuePair<string, IReadOnlyList<string>> kv in settings)
        {
            AddOneWay(kv.Key, [.. kv.Value]);
        }

        return this;
    }

    /// <summary>
    /// Analyzes every key and synonym with <paramref name="analyzer"/> (so case, accents, stemming and stop words are treated as in
    /// the index), merges rules with the same key in the order added, applies the limits and builds the map.
    /// </summary>
    /// <param name="analyzer">The analyzer of the field the synonyms apply to. Phrases that analyze to nothing (all stop words) are dropped; a synonym equal to its key is dropped.</param>
    /// <param name="limits">The limits; <see cref="SynonymLimits.Default"/> when <see langword="null"/>.</param>
    /// <returns>The map.</returns>
    public SynonymMap Build(Analyzer analyzer, SynonymLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        limits ??= SynonymLimits.Default;
        var entries = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        var words = new Dictionary<string, int>(StringComparer.Ordinal);
        var closed = new HashSet<string>(StringComparer.Ordinal);
        int dropped = 0;
        foreach ((string from, string[] to) in _rules)
        {
            string[] keyTerms = SynonymMap.AnalyzeToTerms(analyzer, from);
            if (keyTerms.Length == 0 || keyTerms.Length > limits.MaxQueryWords)
            {
                continue;
            }

            string key = SynonymMap.JoinKey(keyTerms);
            if (!entries.TryGetValue(key, out List<string[]>? list))
            {
                entries[key] = list = [];
                words[key] = 0;
            }

            foreach (string alt in to)
            {
                string[] altTerms = SynonymMap.AnalyzeToTerms(analyzer, alt);
                if (altTerms.Length == 0 || SynonymMap.JoinKey(altTerms) == key || ContainsSequence(list, altTerms))
                {
                    continue;
                }

                if (closed.Contains(key) || list.Count >= limits.MaxSynonymsPerTerm || words[key] + altTerms.Length > limits.MaxWordsPerTerm)
                {
                    closed.Add(key); // everything after the first overflow is dropped
                    dropped++;
                    continue;
                }

                list.Add(altTerms);
                words[key] += altTerms.Length;
            }
        }

        var map = new Dictionary<string, string[][]>(entries.Count, StringComparer.Ordinal);
        foreach (KeyValuePair<string, List<string[]>> kv in entries)
        {
            if (kv.Value.Count > 0)
            {
                map[kv.Key] = [.. kv.Value];
            }
        }

        return new SynonymMap(map, limits, dropped);
    }

    private static bool ContainsSequence(List<string[]> list, string[] terms)
    {
        foreach (string[] t in list)
        {
            if (t.AsSpan().SequenceEqual(terms))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// An immutable synonym table over analyzed terms: for a run of 1 to <see cref="SynonymLimits.MaxQueryWords"/> analyzed query
/// words, the analyzed alternatives (each one or more words) it also reads as. Lookups never change the table, so one map is
/// shared by many threads; the map is applied at query time only, so changing synonyms never needs a reindex.
/// </summary>
public sealed class SynonymMap
{
    private const char KeySeparator = '\u001F';
    private readonly Dictionary<string, string[][]> _map;

    internal SynonymMap(Dictionary<string, string[][]> map, SynonymLimits limits, int dropped)
    {
        _map = map;
        Limits = limits;
        DroppedSynonyms = dropped;
    }

    /// <summary>A map with no rules.</summary>
    public static SynonymMap Empty { get; } = new([], SynonymLimits.Default, 0);

    /// <summary>The limits the map was built with.</summary>
    public SynonymLimits Limits { get; }

    /// <summary>How many synonyms the per-key limits left out.</summary>
    public int DroppedSynonyms { get; }

    /// <summary>The number of keys with at least one synonym.</summary>
    public int KeyCount => _map.Count;

    /// <summary>Looks up the alternatives of a run of analyzed words.</summary>
    /// <param name="terms">One to <see cref="SynonymLimits.MaxQueryWords"/> analyzed terms.</param>
    /// <returns>The alternatives in the order the rules gave them; empty when there are none.</returns>
    public IReadOnlyList<IReadOnlyList<string>> GetSynonyms(params string[] terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return TryGet(terms, out string[][]? alts) ? alts : [];
    }

    internal bool TryGet(string[] terms, out string[][] alternatives)
    {
        if (terms.Length == 0 || terms.Length > Limits.MaxQueryWords || _map.Count == 0)
        {
            alternatives = [];
            return false;
        }

        return _map.TryGetValue(JoinKey(terms), out alternatives!);
    }

    internal static string JoinKey(string[] terms) => terms.Length == 1 ? terms[0] : string.Join(KeySeparator, terms);

    // One term per position; terms stacked at one position (a stemmed twin) are alternatives of that word, and a rule is
    // written with the first of them, the form the analyzer lists first.
    internal static string[] AnalyzeToTerms(Analyzer analyzer, string text)
    {
        var terms = new List<string>();
        int last = int.MinValue;
        foreach (AnalyzedToken t in analyzer.Analyze(text))
        {
            if (t.Position != last)
            {
                terms.Add(t.Term);
                last = t.Position;
            }
        }

        return [.. terms];
    }
}

/// <summary>One word of a query after analysis: the terms the analyzer produced at that position and what lies before it.</summary>
/// <param name="Terms">The analyzed terms at the position: one normally, more when the analyzer stacks forms (an unstemmed word and its stem). The first is the primary form.</param>
/// <param name="GapBefore">The positions removed between the previous word and this one (stop words kept as gaps). 0 normally.</param>
/// <param name="StartOffset">The index in the query text of the word's first character.</param>
/// <param name="EndOffset">The index just past its last character.</param>
public sealed record QuerySlot(IReadOnlyList<string> Terms, int GapBefore, int StartOffset, int EndOffset);

/// <summary>
/// One way across part of a query: the original word (or an analyzer's stacked form of it), or a synonym that replaces a run of
/// words.
/// </summary>
/// <param name="From">The node (word boundary) where the edge starts; word <c>i</c> lies between nodes <c>i</c> and <c>i+1</c>.</param>
/// <param name="To">The node where it ends; more than <c>From + 1</c> only for a synonym of several query words.</param>
/// <param name="Terms">The terms in order; a synonym of several words is a phrase.</param>
/// <param name="IsSynonym"><see langword="true"/> for a synonym edge.</param>
/// <param name="GapBefore">The positions removed before the first word the edge covers.</param>
public sealed record TokenGraphEdge(int From, int To, IReadOnlyList<string> Terms, bool IsSynonym, int GapBefore);

/// <summary>
/// A query as a token graph (a lattice): nodes are the boundaries between query words, edges are the ways to read the words
/// between two nodes. The original words are the edges from node <c>i</c> to <c>i+1</c>; every synonym rule that matches a run of
/// words adds an edge from the run's first to its last node carrying the synonym's terms. A reading of the query is a path from
/// node 0 to the last node. Multi-word synonyms are one edge, which is what keeps phrase semantics right: a synonym of two words
/// matches as a two-word phrase, and a phrase query containing a word with a synonym is rewritten as a phrase per reading,
/// never as a bag of positions.
/// </summary>
public sealed class TokenGraph
{
    private TokenGraph(int slotCount, IReadOnlyList<TokenGraphEdge> edges, bool truncated)
    {
        SlotCount = slotCount;
        Edges = edges;
        IsTruncated = truncated;
    }

    /// <summary>The number of query words; the graph has one more node.</summary>
    public int SlotCount { get; }

    /// <summary>The edges ordered by start node, original words before synonyms, then by end node, then in the order the rules gave the synonyms.</summary>
    public IReadOnlyList<TokenGraphEdge> Edges { get; }

    /// <summary>Whether a synonym edge was left out to keep to <see cref="SynonymLimits.MaxPaths"/>.</summary>
    public bool IsTruncated { get; }

    /// <summary>Analyzes <paramref name="text"/> and builds its graph.</summary>
    /// <param name="analyzer">The analyzer of the field to search; it applies the stop words, so stop words are not in the graph.</param>
    /// <param name="text">The query text; never throws on any text.</param>
    /// <param name="synonyms">The synonyms, or <see langword="null"/> for none.</param>
    /// <returns>The graph; with no words it has no edges.</returns>
    public static TokenGraph FromText(Analyzer analyzer, string text, SynonymMap? synonyms = null)
    {
        ArgumentNullException.ThrowIfNull(analyzer);
        ArgumentNullException.ThrowIfNull(text);
        var terms = new List<List<string>>();
        var gaps = new List<int>();
        var starts = new List<int>();
        var ends = new List<int>();
        TokenSource stream = analyzer.GetTokens(text.AsMemory());
        while (stream.MoveNext())
        {
            Token t = stream.Token;
            if (t.PositionIncrement == 0 && terms.Count > 0)
            {
                string term = t.Term.ToString();
                if (!terms[^1].Contains(term))
                {
                    terms[^1].Add(term);
                }

                starts[^1] = Math.Min(starts[^1], t.StartOffset);
                ends[^1] = Math.Max(ends[^1], t.EndOffset);
                continue;
            }

            terms.Add([t.Term.ToString()]);
            gaps.Add(Math.Max(0, t.PositionIncrement - 1));
            starts.Add(t.StartOffset);
            ends.Add(t.EndOffset);
        }

        var slots = new QuerySlot[terms.Count];
        for (int i = 0; i < slots.Length; i++)
        {
            slots[i] = new QuerySlot(terms[i], gaps[i], starts[i], ends[i]);
        }

        return Build(slots, synonyms);
    }

    /// <summary>Builds the graph of already analyzed words.</summary>
    /// <param name="slots">The query words in order.</param>
    /// <param name="synonyms">The synonyms, or <see langword="null"/> for none.</param>
    /// <returns>The graph.</returns>
    public static TokenGraph Build(IReadOnlyList<QuerySlot> slots, SynonymMap? synonyms = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        int n = slots.Count;
        var edges = new List<TokenGraphEdge>();
        for (int i = 0; i < n; i++)
        {
            foreach (string term in slots[i].Terms)
            {
                edges.Add(new TokenGraphEdge(i, i + 1, [term], false, slots[i].GapBefore));
            }
        }

        bool truncated = false;
        if (synonyms is not null && synonyms.KeyCount > 0)
        {
            SynonymLimits limits = synonyms.Limits;
            for (int i = 0; i < n; i++)
            {
                int maxLen = Math.Min(limits.MaxQueryWords, n - i);
                for (int len = 1; len <= maxLen; len++)
                {
                    foreach (string[] key in KeyCombinations(slots, i, len))
                    {
                        if (!synonyms.TryGet(key, out string[][] alts))
                        {
                            continue;
                        }

                        foreach (string[] alt in alts)
                        {
                            var edge = new TokenGraphEdge(i, i + len, alt, true, slots[i].GapBefore);
                            if (edges.Any(e => e.From == edge.From && e.To == edge.To && e.Terms.SequenceEqual(edge.Terms)))
                            {
                                continue;
                            }

                            edges.Add(edge);
                            if (CountPaths(edges, n) > limits.MaxPaths)
                            {
                                edges.RemoveAt(edges.Count - 1);
                                truncated = true;
                            }
                        }
                    }
                }
            }
        }

        // Stable: ties keep the order in which edges were added.
        IReadOnlyList<TokenGraphEdge> ordered = [.. edges.OrderBy(e => e.From).ThenBy(e => e.IsSynonym).ThenBy(e => e.To)];
        return new TokenGraph(n, ordered, truncated);
    }

    /// <summary>Lists the readings of the whole query.</summary>
    /// <returns>Every path from the first node to the last, original reading first; at most <see cref="SynonymLimits.MaxPaths"/> of them unless the original words alone have more stacked forms.</returns>
    public IReadOnlyList<IReadOnlyList<TokenGraphEdge>> Paths() => PathsBetween(0, SlotCount);

    /// <summary>Lists the paths between two nodes.</summary>
    /// <param name="from">The start node.</param>
    /// <param name="to">The end node.</param>
    /// <returns>The paths, in edge order.</returns>
    public IReadOnlyList<IReadOnlyList<TokenGraphEdge>> PathsBetween(int from, int to)
    {
        var result = new List<IReadOnlyList<TokenGraphEdge>>();
        if (from == to)
        {
            return result;
        }

        var stack = new List<TokenGraphEdge>();
        Walk(from, to, stack, result);
        return result;
    }

    /// <summary>
    /// The nodes every reading passes through, 0 and the last node included: no edge spans over them. The readings of the stretches
    /// between consecutive cut nodes are independent, which lets a query builder combine them instead of multiplying them.
    /// </summary>
    /// <returns>The cut nodes in increasing order.</returns>
    public IReadOnlyList<int> CutNodes()
    {
        var cuts = new List<int>();
        for (int k = 0; k <= SlotCount; k++)
        {
            bool spanned = false;
            foreach (TokenGraphEdge e in Edges)
            {
                if (e.From < k && k < e.To)
                {
                    spanned = true;
                    break;
                }
            }

            if (!spanned)
            {
                cuts.Add(k);
            }
        }

        return cuts;
    }

    private void Walk(int node, int target, List<TokenGraphEdge> stack, List<IReadOnlyList<TokenGraphEdge>> result)
    {
        if (node == target)
        {
            result.Add([.. stack]);
            return;
        }

        foreach (TokenGraphEdge e in Edges)
        {
            if (e.From == node && e.To <= target)
            {
                stack.Add(e);
                Walk(e.To, target, stack, result);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    private static long CountPaths(List<TokenGraphEdge> edges, int n)
    {
        var count = new long[n + 1];
        count[0] = 1;
        for (int k = 0; k < n; k++)
        {
            if (count[k] == 0)
            {
                continue;
            }

            foreach (TokenGraphEdge e in edges)
            {
                if (e.From == k)
                {
                    count[e.To] = Math.Min(1L << 40, count[e.To] + count[k]);
                }
            }
        }

        return count[n];
    }

    // The runs of terms a rule key may match when words carry stacked forms: the product of the forms, at most 16 runs.
    private static List<string[]> KeyCombinations(IReadOnlyList<QuerySlot> slots, int start, int len)
    {
        var runs = new List<string[]> { new string[len] };
        for (int j = 0; j < len; j++)
        {
            var next = new List<string[]>();
            foreach (string[] run in runs)
            {
                foreach (string term in slots[start + j].Terms)
                {
                    if (next.Count >= 16)
                    {
                        break;
                    }

                    string[] copy = (string[])run.Clone();
                    copy[j] = term;
                    next.Add(copy);
                }
            }

            runs = next;
        }

        return runs;
    }
}
