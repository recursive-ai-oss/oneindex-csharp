namespace OneIndex.Harness;

/// <summary>One document of a benchmark or relevance corpus.</summary>
/// <param name="Id">External identifier, returned in hits.</param>
/// <param name="Title">Title; may be empty.</param>
/// <param name="Text">Body text.</param>
/// <param name="Length">A numeric column for filter, sort and statistics rows (0 when the corpus has none).</param>
/// <param name="Category">A keyword column for filter and facet rows (empty when the corpus has none).</param>
public sealed record Doc(string Id, string Title, string Text, long Length = 0, string Category = "");

/// <summary>How text is analyzed. Both indexing and query text use the same analysis.</summary>
public enum AnalysisKind
{
    /// <summary>Lowercase, possessive removal, English stop words and Porter-style stemming (the BEIR relevance setup).</summary>
    English,

    /// <summary>Standard word tokenization and lowercase only: no stop words, no stemming (the benchmark setup).</summary>
    Standard,
}

/// <summary>Which fields a relevance index carries, and which a benchmark index carries.</summary>
public enum IndexProfile
{
    /// <summary>Title, text and a flat title+text field, for the BEIR layouts.</summary>
    Beir,

    /// <summary>Text, a numeric column and a keyword column, for the benchmark rows.</summary>
    Bench,
}

/// <summary>How title and body take part in BM25 for a BEIR query.</summary>
public enum FieldLayout
{
    /// <summary>One field holding title and text concatenated.</summary>
    Flat,

    /// <summary>Title and text scored separately; a document takes the better of the two (Elasticsearch's multi_match "best_fields").</summary>
    MultiFieldMax,

    /// <summary>Title and text scored separately; the document score is the sum.</summary>
    MultiFieldSum,

    /// <summary>Title and text combined per term before saturation (BM25F, Robertson and Zaragoza section 3.5), equal field weights.</summary>
    Bm25F,
}

/// <summary>Index-time settings shared by every engine.</summary>
/// <param name="Directory">Where the index lives (created fresh).</param>
/// <param name="Analysis">The analysis chain.</param>
/// <param name="Profile">The fields to index.</param>
/// <param name="RamBufferMb">Write buffer before a segment flush, in MiB.</param>
public sealed record EngineSettings(string Directory, AnalysisKind Analysis, IndexProfile Profile, int RamBufferMb = 256);

/// <summary>A scored hit.</summary>
/// <param name="Id">The document's external id.</param>
/// <param name="Score">Its score (0 for unscored sorts).</param>
public readonly record struct Hit(string Id, float Score);

/// <summary>The answer to one query.</summary>
/// <param name="Hits">Top hits, best first.</param>
/// <param name="Total">Number of matching documents.</param>
/// <param name="Facets">Category counts, most frequent first, when the query asked for them.</param>
/// <param name="Stats">Count, min, max and sum of the numeric column over the matches, when the query asked for them.</param>
public sealed record SearchResult(Hit[] Hits, long Total, (string Value, long Count)[]? Facets = null, (long Count, long Min, long Max, long Sum)? Stats = null);

/// <summary>A query a harness can send to any engine. Terms are already lower-cased analyzer output of the Standard analysis.</summary>
public abstract record HarnessQuery;

/// <summary>Free text, analyzed like the corpus and scored as a disjunction of its terms over the layout's fields.</summary>
public sealed record MatchQuery(string Text, FieldLayout Layout) : HarnessQuery;

/// <summary>A disjunction of terms over the body field, scored by BM25.</summary>
public sealed record TermsQuery(string[] Terms) : HarnessQuery;

/// <summary>An exact phrase (slop 0) over the body field.</summary>
public sealed record PhraseQuery(string[] Terms) : HarnessQuery;

/// <summary>Documents containing every required term and a term starting with <paramref name="Prefix"/> (as-you-type when <paramref name="Required"/> is not empty).</summary>
public sealed record PrefixQuery(string[] Required, string Prefix) : HarnessQuery;

/// <summary>Documents containing a term within <paramref name="MaxEdits"/> Levenshtein edits of <paramref name="Term"/> (transpositions count as one edit unless the engine cannot).</summary>
public sealed record FuzzyQuery(string Term, int MaxEdits) : HarnessQuery;

/// <summary>A terms query restricted to documents with the numeric column in range and the keyword column among the categories.</summary>
public sealed record FilteredQuery(string[] Terms, long MinLength, long MaxLength, string[] Categories) : HarnessQuery;

/// <summary>A terms query that also counts the keyword column over all matches.</summary>
public sealed record FacetQuery(string[] Terms) : HarnessQuery;

/// <summary>A terms query that also reports statistics of the numeric column over all matches.</summary>
public sealed record StatsQuery(string[] Terms) : HarnessQuery;

/// <summary>A terms query ordered by the numeric column, largest first, instead of by score.</summary>
public sealed record SortedQuery(string[] Terms) : HarnessQuery;

/// <summary>An engine under test: Lucene.NET today, OneIndex through its adapter, anything else that can index and answer <see cref="HarnessQuery"/>.</summary>
public interface ITextEngine : IDisposable
{
    /// <summary>Name used in result tables.</summary>
    string Name { get; }

    /// <summary>The exact version or build of the engine.</summary>
    string Version { get; }

    /// <summary>Whether this engine can run now; <paramref name="reason"/> says why not.</summary>
    bool IsAvailable(out string reason);

    /// <summary>Whether the engine implements the query kind.</summary>
    bool Supports(Type queryType);

    /// <summary>Creates a fresh, empty index in <see cref="EngineSettings.Directory"/>.</summary>
    void Create(EngineSettings settings);

    /// <summary>Adds a document. Safe to call from several threads.</summary>
    void Add(Doc doc);

    /// <summary>Makes everything added so far durable (the commit whose latency the benchmark times).</summary>
    void Commit();

    /// <summary>Opens the committed index for searching.</summary>
    void OpenSearcher();

    /// <summary>Sets the BM25 parameters used by the following searches.</summary>
    void SetBm25(float k1, float b);

    /// <summary>Answers a query with up to <paramref name="k"/> hits. Safe to call from several threads once the searcher is open.</summary>
    SearchResult Search(HarnessQuery query, int k);

    /// <summary>Total size of the index files in bytes.</summary>
    long IndexSizeBytes();
}

internal static class JsonOptions
{
    internal static readonly System.Text.Json.JsonSerializerOptions Indented = new() { WriteIndented = true };
}
