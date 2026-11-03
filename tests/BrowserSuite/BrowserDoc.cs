namespace OneIndex.BrowserSuite;

/// <summary>The document type of the browser suite: text, filters, a sortable number and a vector.</summary>
public sealed class BrowserDoc
{
    public const int Dimensions = 32;

    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 2)]
    public string Title { get; set; } = string.Empty;

    [FullText]
    public string Body { get; set; } = string.Empty;

    [Filterable, Facet]
    public string Group { get; set; } = string.Empty;

    [Filterable, Sortable]
    public int Length { get; set; }

    [Vector(Dimensions = Dimensions)]
    public float[]? Embedding { get; set; }
}
