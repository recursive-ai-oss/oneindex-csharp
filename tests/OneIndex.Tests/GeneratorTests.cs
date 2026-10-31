using OneIndex.Generated;

namespace OneIndex.Tests;

/// <summary>An attributed type; the schema generator describes it at build time.</summary>
public class Invoice
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Boost = 2)]
    public string Title { get; set; } = string.Empty;

    [FullText(Language = "en")]
    public string Body { get; set; } = string.Empty;

    [Filterable]
    [Sortable]
    public DateTime Modified { get; set; }

    [Keyword]
    [Facet]
    public string[] Tags { get; set; } = [];

    [Vector(From = nameof(Body))]
    public float[] Embedding { get; set; } = [];

    public string NotSearchable { get; set; } = string.Empty;
}

public class GeneratorTests
{
    [Fact]
    public void TheGeneratorDescribesAnAttributedClassInDeclarationOrder()
    {
        Assert.Equal("OneIndex.Tests.Invoice", OneIndex_Tests_InvoiceSchemaDescriptor.TypeName);
        Assert.Equal("Id", OneIndex_Tests_InvoiceSchemaDescriptor.IdField);
        Assert.Equal(["Id", "Title", "Body", "Modified", "Tags", "Embedding"], OneIndex_Tests_InvoiceSchemaDescriptor.FieldNames);
        Assert.Equal(["id", "text", "text", "value,filterable,sortable", "keyword,facet", "vector"], OneIndex_Tests_InvoiceSchemaDescriptor.FieldKinds);
    }
}
