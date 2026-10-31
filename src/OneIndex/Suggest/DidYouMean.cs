using System.Text;
using OneIndex.Analysis;
using OneIndex.Codecs;
using OneIndex.Search;

namespace OneIndex.Suggest;

/// <summary>When a query word is corrected.</summary>
public enum SuggestMode : byte
{
    /// <summary>Only words that occur in no document of the field are corrected (the default).</summary>
    Missing,

    /// <summary>Also words that occur, when a close term is at least <see cref="SuggestOptions.PopularFactor"/> times more common.</summary>
    Popular,
}

/// <summary>Bounds and weights of <see cref="DidYouMean"/>. Every default is documented in docs/did-you-mean.md.</summary>
public sealed class SuggestOptions
{
    /// <summary>The most suggestions returned. Default 3.</summary>
    public int MaxSuggestions { get; init; } = 3;

    /// <summary>The shortest word (in code points) that may be corrected with one edit; shorter words are left alone. Default 4.</summary>
    public int OneEditMinLength { get; init; } = 4;

    /// <summary>The shortest word that may be corrected with two edits. Default 6 (measured in docs/did-you-mean.md: 7 loses about 1 point of top-1 on two-edit typos, 8 about 9).</summary>
    public int TwoEditsMinLength { get; init; } = 6;

    /// <summary>How many leading code points of a word must be right (people rarely mistype the first letter, and fixing them keeps the dictionary walk small). Default 1; 0 allows any first letter.</summary>
    public int PrefixLength { get; init; } = 1;

    /// <summary>When a word is corrected. Default <see cref="SuggestMode.Missing"/>.</summary>
    public SuggestMode Mode { get; init; } = SuggestMode.Missing;

    /// <summary>With <see cref="SuggestMode.Popular"/>, how many times more documents a candidate needs than the word it replaces. Default 10.</summary>
    public int PopularFactor { get; init; } = 10;

    /// <summary>The most dictionary terms examined per word and segment; the walk is in byte order, so a cut-off is deterministic. Default 20,000.</summary>
    public int MaxTermsVisited { get; init; } = 20_000;

    /// <summary>The candidates kept per word for combining. Default 4.</summary>
    public int CandidatesPerWord { get; init; } = 4;

    /// <summary>The partial suggestions kept while combining the words of a multi-word query. Default 8.</summary>
    public int BeamWidth { get; init; } = 8;

    /// <summary>The most postings entries stepped through when counting documents that hold two words together, per word pair and segment. Default 50,000.</summary>
    public int MaxCooccurrenceScan { get; init; } = 50_000;

    /// <summary>The score lost per edit. Default 4: one more edit costs as much as a candidate being about 55 times rarer.</summary>
    public double EditPenalty { get; init; } = 4.0;

    /// <summary>The score weight of the natural log of (1 + document frequency). Default 1.</summary>
    public double FrequencyWeight { get; init; } = 1.0;

    /// <summary>The score weight of the natural log of (1 + documents holding a word and the word before it). Default 1.</summary>
    public double CooccurrenceWeight { get; init; } = 1.0;

    /// <summary>The shared default options.</summary>
    public static SuggestOptions Default { get; } = new();
}

/// <summary>A dictionary term close to a misspelt word.</summary>
/// <param name="Term">The term as stored.</param>
/// <param name="Edits">The edit distance from the word, at least 1.</param>
/// <param name="DocFreq">The documents containing it, summed over segments (deleted documents included, as everywhere in the term statistics).</param>
public readonly record struct TermCandidate(string Term, int Edits, long DocFreq);

/// <summary>One corrected word of a <see cref="Suggestion"/>.</summary>
/// <param name="Original">The analyzed word that was typed.</param>
/// <param name="Replacement">The term it was replaced with.</param>
/// <param name="StartOffset">The index in the query text of the typed word's first character.</param>
/// <param name="EndOffset">The index just past its last character.</param>
/// <param name="Edits">The edit distance between the two.</param>
public readonly record struct TokenCorrection(string Original, string Replacement, int StartOffset, int EndOffset, int Edits);

/// <summary>A corrected query.</summary>
/// <param name="Text">The query text with each corrected word replaced by the dictionary term (in its indexed, case-folded form); everything else is as typed.</param>
/// <param name="Score">The ranking score; higher is better, comparable only within one call.</param>
/// <param name="Edits">The total edits over all corrected words.</param>
/// <param name="Corrections">What changed, in query order.</param>
public sealed record Suggestion(string Text, double Score, int Edits, IReadOnlyList<TokenCorrection> Corrections);

/// <summary>
/// "Did you mean": corrected versions of a query, from the terms of a field's dictionary. For each misspelt word the candidates
/// are the dictionary terms a Levenshtein automaton accepts (Schulz and Mihov 2002: the dictionary and the automaton are walked
/// together, so only the neighbourhood of the word is visited), ranked by edit distance, document frequency and, between
/// neighbouring words of a multi-word query, how many documents hold both. The method is written in docs/did-you-mean.md.
/// Output is deterministic: ties break by the term's byte order.
/// <para>
/// An instance reuses its analyzer's buffers: use one per thread. The segments are read through
/// <see cref="ITextFieldReader"/>, which is synchronous over cached ranges.
/// </para>
/// </summary>
public sealed class DidYouMean
{
    private readonly Analyzer _analyzer;
    private readonly SuggestOptions _options;

    /// <summary>Creates a suggester.</summary>
    /// <param name="analyzer">The analyzer of the field to suggest from, so that query words are normalized as the index's terms were. Prefer an analyzer that does not stem: a stemmed field's terms are stems.</param>
    /// <param name="options">The options; <see cref="SuggestOptions.Default"/> when <see langword="null"/>.</param>
    public DidYouMean(Analyzer analyzer, SuggestOptions? options = null)
    {
        _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
        _options = options ?? SuggestOptions.Default;
    }

    /// <summary>Suggests corrected queries.</summary>
    /// <param name="segments">The field's reader in each segment of one snapshot.</param>
    /// <param name="text">The query text; any text, never throws for its content.</param>
    /// <param name="cancellationToken">Cancels the dictionary walks.</param>
    /// <returns>At most <see cref="SuggestOptions.MaxSuggestions"/> suggestions, best first; empty when nothing needs or can be corrected.</returns>
    /// <exception cref="OperationCanceledException">Cancelled.</exception>
    public IReadOnlyList<Suggestion> Suggest(IReadOnlyList<ITextFieldReader> segments, string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(text);
        List<Word> words = Tokenize(text);
        if (words.Count == 0)
        {
            return [];
        }

        var options = new List<WordOption>[words.Count];
        bool anyCandidate = false;
        for (int i = 0; i < words.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options[i] = OptionsFor(segments, words[i], cancellationToken, out bool corrected);
            anyCandidate |= corrected;
        }

        if (!anyCandidate)
        {
            return [];
        }

        return Combine(segments, text, words, options, cancellationToken);
    }

    /// <summary>Suggests corrected queries from a single reader.</summary>
    /// <param name="field">The field's reader.</param>
    /// <param name="text">The query text.</param>
    /// <param name="cancellationToken">Cancels the dictionary walks.</param>
    /// <returns>See <see cref="Suggest(IReadOnlyList{ITextFieldReader}, string, CancellationToken)"/>.</returns>
    public IReadOnlyList<Suggestion> Suggest(ITextFieldReader field, string text, CancellationToken cancellationToken = default)
        => Suggest([field], text, cancellationToken);

    /// <summary>The asynchronous twin of <see cref="Suggest(IReadOnlyList{ITextFieldReader}, string, CancellationToken)"/>. Readers are synchronous over cached ranges, so it completes synchronously; the signature is for callers that are asynchronous throughout.</summary>
    /// <param name="segments">The field's reader in each segment.</param>
    /// <param name="text">The query text.</param>
    /// <param name="cancellationToken">Cancels the dictionary walks.</param>
    /// <returns>The suggestions.</returns>
    public ValueTask<IReadOnlyList<Suggestion>> SuggestAsync(IReadOnlyList<ITextFieldReader> segments, string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<IReadOnlyList<Suggestion>>(Suggest(segments, text, cancellationToken));
    }

    /// <summary>Finds the dictionary terms near one word.</summary>
    /// <param name="segments">The field's reader in each segment.</param>
    /// <param name="term">An analyzed word (not run through the analyzer again).</param>
    /// <param name="cancellationToken">Cancels the walks.</param>
    /// <returns>Up to <see cref="SuggestOptions.CandidatesPerWord"/> terms within the word's edit budget, best first (fewest edits, then most documents, then byte order); the word itself is not listed. Empty when the word is too short to correct.</returns>
    public IReadOnlyList<TermCandidate> SuggestTerm(IReadOnlyList<ITextFieldReader> segments, string term, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(term);
        int maxEdits = EditBudget(term);
        return maxEdits == 0 ? [] : Gather(segments, term, maxEdits, cancellationToken);
    }

    private readonly record struct Word(string Term, int Start, int End, bool Correctable);

    private readonly record struct WordOption(string Term, int Edits, long DocFreq);

    private List<Word> Tokenize(string text)
    {
        var words = new List<Word>();
        TokenSource stream = _analyzer.GetTokens(text.AsMemory());
        while (stream.MoveNext())
        {
            Token t = stream.Token;
            if (t.PositionIncrement == 0 && words.Count > 0)
            {
                continue; // a stacked form: the first form at the position is the word
            }

            words.Add(new Word(t.Term.ToString(), t.StartOffset, t.EndOffset, t.Type != TokenType.Number));
        }

        return words;
    }

    private int EditBudget(string term)
    {
        int length = EditDistance.CodePointCount(term);
        if (length <= _options.PrefixLength)
        {
            return 0;
        }

        return length >= _options.TwoEditsMinLength ? 2 : length >= _options.OneEditMinLength ? 1 : 0;
    }

    private List<WordOption> OptionsFor(IReadOnlyList<ITextFieldReader> segments, Word word, CancellationToken ct, out bool corrected)
    {
        corrected = false;
        long df = DocFreq(segments, word.Term);
        var list = new List<WordOption>();
        int maxEdits = word.Correctable ? EditBudget(word.Term) : 0;
        bool consider = maxEdits > 0 && (df == 0 || _options.Mode == SuggestMode.Popular);
        if (consider)
        {
            foreach (TermCandidate c in Gather(segments, word.Term, maxEdits, ct))
            {
                if (df == 0 || c.DocFreq >= df * (long)_options.PopularFactor)
                {
                    list.Add(new WordOption(c.Term, c.Edits, c.DocFreq));
                    corrected = true;
                }
            }
        }

        // An unknown word keeps itself only when nothing better exists; a known word always may stay as it is.
        if (df > 0 || list.Count == 0)
        {
            list.Add(new WordOption(word.Term, 0, df));
        }

        return list;
    }

    private static long DocFreq(IReadOnlyList<ITextFieldReader> segments, string term)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(term);
        long df = 0;
        foreach (ITextFieldReader seg in segments)
        {
            if (seg.TryGetTerm(utf8, out TermInfo info))
            {
                df += info.DocFreq;
            }
        }

        return df;
    }

    private List<TermCandidate> Gather(IReadOnlyList<ITextFieldReader> segments, string term, int maxEdits, CancellationToken ct)
    {
        byte[] prefix = PrefixBytes(term, _options.PrefixLength);
        var automaton = new LevenshteinAutomaton(term, maxEdits, transpositions: true);
        var found = new Dictionary<string, (int Edits, long DocFreq)>(StringComparer.Ordinal);
        foreach (ITextFieldReader seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            using ITermEnumerator terms = seg.Intersect(automaton, prefix);
            int visited = 0;
            while (terms.MoveNext())
            {
                if (++visited > _options.MaxTermsVisited)
                {
                    break;
                }

                if ((visited & 255) == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }

                string candidate = Encoding.UTF8.GetString(terms.Term);
                if (string.Equals(candidate, term, StringComparison.Ordinal))
                {
                    continue;
                }

                int edits = EditDistance.OptimalStringAlignment(term, candidate, maxEdits);
                if (edits > maxEdits)
                {
                    continue; // cannot happen with a matching automaton; kept so a wrong distance never ranks
                }

                found[candidate] = found.TryGetValue(candidate, out (int Edits, long DocFreq) old)
                    ? (edits, old.DocFreq + terms.Info.DocFreq)
                    : (edits, terms.Info.DocFreq);
            }
        }

        var all = new List<TermCandidate>(found.Count);
        foreach (KeyValuePair<string, (int Edits, long DocFreq)> kv in found)
        {
            all.Add(new TermCandidate(kv.Key, kv.Value.Edits, kv.Value.DocFreq));
        }

        // Fewest edits, then most documents, then UTF-8 byte order (ordinal UTF-16 order differs only above U+FFFF, so compare bytes).
        all.Sort(static (x, y) =>
        {
            int c = x.Edits.CompareTo(y.Edits);
            if (c != 0)
            {
                return c;
            }

            c = y.DocFreq.CompareTo(x.DocFreq);
            return c != 0 ? c : CompareUtf8(x.Term, y.Term);
        });
        if (all.Count > _options.CandidatesPerWord)
        {
            all.RemoveRange(_options.CandidatesPerWord, all.Count - _options.CandidatesPerWord);
        }

        return all;
    }

    private static byte[] PrefixBytes(string term, int codePoints)
    {
        if (codePoints <= 0)
        {
            return [];
        }

        int taken = 0;
        var sb = new StringBuilder();
        foreach (Rune r in term.EnumerateRunes())
        {
            if (taken++ == codePoints)
            {
                break;
            }

            sb.Append(r.ToString());
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static int CompareUtf8(string a, string b)
        => Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    private readonly record struct Partial(double Score, int[] Choice);

    private List<Suggestion> Combine(IReadOnlyList<ITextFieldReader> segments, string text, List<Word> words, List<WordOption>[] options, CancellationToken ct)
    {
        var cooc = new Dictionary<(string, string), long>();
        var beam = new List<Partial>();
        for (int o = 0; o < options[0].Count; o++)
        {
            beam.Add(new Partial(Unary(options[0][o]), [o]));
        }

        TrimBeam(beam, options);
        for (int i = 1; i < words.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var next = new List<Partial>();
            foreach (Partial p in beam)
            {
                WordOption prev = options[i - 1][p.Choice[^1]];
                for (int o = 0; o < options[i].Count; o++)
                {
                    WordOption cur = options[i][o];
                    double score = p.Score + Unary(cur);
                    if (prev.DocFreq > 0 && cur.DocFreq > 0)
                    {
                        score += _options.CooccurrenceWeight * Math.Log(1 + Cooccurrence(segments, prev.Term, cur.Term, cooc, ct));
                    }

                    int[] choice = new int[p.Choice.Length + 1];
                    p.Choice.CopyTo(choice, 0);
                    choice[^1] = o;
                    next.Add(new Partial(score, choice));
                }
            }

            beam = next;
            TrimBeam(beam, options);
        }

        var result = new List<Suggestion>();
        foreach (Partial p in beam)
        {
            var corrections = new List<TokenCorrection>();
            for (int i = 0; i < words.Count; i++)
            {
                WordOption chosen = options[i][p.Choice[i]];
                if (chosen.Edits > 0)
                {
                    corrections.Add(new TokenCorrection(words[i].Term, chosen.Term, words[i].Start, words[i].End, chosen.Edits));
                }
            }

            if (corrections.Count == 0)
            {
                continue; // the query as typed is not a suggestion
            }

            var sb = new StringBuilder();
            int at = 0;
            foreach (TokenCorrection c in corrections)
            {
                sb.Append(text, at, c.StartOffset - at).Append(c.Replacement);
                at = c.EndOffset;
            }

            sb.Append(text, at, text.Length - at);
            result.Add(new Suggestion(sb.ToString(), p.Score, corrections.Sum(c => c.Edits), corrections));
        }

        result.Sort(static (x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : string.CompareOrdinal(x.Text, y.Text);
        });
        if (result.Count > _options.MaxSuggestions)
        {
            result.RemoveRange(_options.MaxSuggestions, result.Count - _options.MaxSuggestions);
        }

        return result;
    }

    private double Unary(WordOption o)
        => (_options.FrequencyWeight * Math.Log(1 + o.DocFreq)) - (_options.EditPenalty * o.Edits);

    private void TrimBeam(List<Partial> beam, List<WordOption>[] options)
    {
        // Best score first; equal scores by the choice indexes, which follow the candidates' deterministic order.
        beam.Sort(static (x, y) =>
        {
            int c = y.Score.CompareTo(x.Score);
            return c != 0 ? c : x.Choice.AsSpan().SequenceCompareTo(y.Choice);
        });
        if (beam.Count > _options.BeamWidth)
        {
            beam.RemoveRange(_options.BeamWidth, beam.Count - _options.BeamWidth);
        }
    }

    // Documents in which both terms occur, summed over segments, by leapfrogging the two postings lists. Each step counts
    // against MaxCooccurrenceScan, so a pair of very common words costs a bounded amount.
    private long Cooccurrence(IReadOnlyList<ITextFieldReader> segments, string a, string b, Dictionary<(string, string), long> cache, CancellationToken ct)
    {
        if (cache.TryGetValue((a, b), out long known))
        {
            return known;
        }

        byte[] ua = Encoding.UTF8.GetBytes(a);
        byte[] ub = Encoding.UTF8.GetBytes(b);
        long together = 0;
        foreach (ITextFieldReader seg in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (!seg.TryGetTerm(ua, out TermInfo ia) || !seg.TryGetTerm(ub, out TermInfo ib))
            {
                continue;
            }

            DocIdSetIterator x = seg.GetPostings(ia, PostingsFeatures.None);
            DocIdSetIterator y = seg.GetPostings(ib, PostingsFeatures.None);
            int steps = 0;
            int dx = x.NextDoc();
            int dy = y.NextDoc();
            while (dx != DocIdSetIterator.NoMoreDocs && dy != DocIdSetIterator.NoMoreDocs && steps++ < _options.MaxCooccurrenceScan)
            {
                if (dx == dy)
                {
                    together++;
                    dx = x.NextDoc();
                    dy = y.NextDoc();
                }
                else if (dx < dy)
                {
                    dx = x.Advance(dy);
                }
                else
                {
                    dy = y.Advance(dx);
                }
            }
        }

        cache[(a, b)] = together;
        return together;
    }
}
