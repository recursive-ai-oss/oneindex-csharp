namespace OneIndex.Analysis;

/// <summary>How a configured stop word list relates to a language's default list.</summary>
public enum StopWordMode : byte
{
    /// <summary>Only the configured words are stop words; the language's own list is not used. An empty configured list turns stop words off.</summary>
    Replace,

    /// <summary>The configured words are added to the language's own list.</summary>
    Extend,
}

/// <summary>
/// A per-index stop word configuration. Stop words are dropped by the analyzer, so the same configuration acts at index time
/// (the words are not indexed) and at query time (they are ignored in the query, inside phrases too): the query side runs the
/// same analyzer as the index side, which is what keeps the two in step.
/// <para>
/// Words are written as they would read after case folding and before stemming or accent folding, and they are folded here the
/// same way the standard analyzer folds text (a word that splits into several tokens contributes each). <c>"The"</c> and
/// <c>"the"</c> are one word.
/// </para>
/// </summary>
/// <param name="Words">The configured words. Duplicates are fine; empty entries are ignored.</param>
public sealed record StopWordSettings(IReadOnlyList<string> Words)
{
    /// <summary>Whether the words replace or extend the language's list. Default <see cref="StopWordMode.Replace"/>.</summary>
    public StopWordMode Mode { get; init; } = StopWordMode.Replace;

    /// <summary>
    /// Whether a dropped word leaves a gap in the positions of the words around it. Default false: the neighbours become
    /// adjacent, so a phrase query that skips a stop word matches exactly the text that has the stop word in that place, and
    /// the text that never had it. With true, positions stay truthful (useful for proximity), and a phrase over a removed
    /// word is only an approximation (see docs/synonyms.md, "Positions and gaps").
    /// </summary>
    public bool PreserveGaps { get; init; }

    /// <summary>Computes the effective set for a language.</summary>
    /// <param name="languageCode">An ISO 639-1 code as the language analyzers use, or <see langword="null"/> for none.</param>
    /// <returns>The set; empty when nothing is configured and there is no default.</returns>
    public StopWords Resolve(string? languageCode)
    {
        ArgumentNullException.ThrowIfNull(Words);
        var folded = new List<string>(Words.Count);
        var analyzer = new StandardAnalyzer();
        foreach (string w in Words)
        {
            if (!string.IsNullOrWhiteSpace(w))
            {
                foreach (AnalyzedToken t in analyzer.Analyze(w))
                {
                    folded.Add(t.Term);
                }
            }
        }

        StopWords configured = new(folded);
        if (Mode == StopWordMode.Extend && languageCode is not null && StopWords.ForLanguage(languageCode) is { } own)
        {
            return own.With(configured.Words);
        }

        return configured;
    }

    /// <summary>Wraps a stream in a <see cref="StopFilter"/> for the effective set.</summary>
    /// <param name="input">The stream so far (case folding must already have run).</param>
    /// <param name="languageCode">The language, or <see langword="null"/>.</param>
    /// <returns>The filter, or <paramref name="input"/> itself when the effective set is empty.</returns>
    public TokenSource CreateFilter(TokenSource input, string? languageCode)
    {
        StopWords set = Resolve(languageCode);
        return set.Count == 0 ? input : new StopFilter(input, set, PreserveGaps);
    }
}
