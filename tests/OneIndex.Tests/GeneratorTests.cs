using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OneIndex.Generators;
using OneIndex.Index;

namespace OneIndex.Tests;

/// <summary>The sample document type: the generator maps it at build time.</summary>
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

    // Not searchable: stored with the document and returned with it.
    public string Note { get; set; } = string.Empty;

    // Read-only members are not part of the document.
    public string Display => Id + ": " + Title;
}

public class GeneratorTests
{
    private const string Sample = """
        using System;
        using OneIndex;
        namespace App;
        public class Invoice {
            [SearchId] public string Id { get; set; } = "";
            [FullText(Boost = 2)] public string Title { get; set; } = "";
            [FullText(Language = "en")] public string Body { get; set; } = "";
            [Filterable, Sortable] public DateTime Modified { get; set; }
            [Keyword, Facet] public string[] Tags { get; set; } = [];
            [Filterable] public int? Amount { get; set; }
            [Filterable] public double Rate { get; set; }
            [Filterable] public bool Paid { get; set; }
            [Filterable] public DateTimeOffset Due { get; set; }
            [Keyword] public System.Collections.Generic.List<string> Names { get; set; } = new();
            public string Note { get; set; } = "";
        }
        """;

    private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compilation, int Sources) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SearchIndex).Assembly.Location))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create(
            "generated-test",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable, allowUnsafe: false));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MappingGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> generatorDiagnostics);
        return (generatorDiagnostics, output.GetDiagnostics(), driver.GetRunResult().GeneratedTrees.Length);
    }

    private static string[] Ids(ImmutableArray<Diagnostic> diagnostics) => diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Id).ToArray();

    [Fact]
    public void TheGeneratedMappingOfTheSampleCompilesWithoutWarnings()
    {
        (ImmutableArray<Diagnostic> generator, ImmutableArray<Diagnostic> compilation, int sources) = Run(Sample);
        Assert.Empty(generator);
        Assert.Equal(1, sources);
        Assert.Empty(compilation.Where(d => d.Severity >= DiagnosticSeverity.Info));
    }

    [Fact]
    public void TheSampleSchemaFollowsTheAttributes()
    {
        Schema schema = SearchMappings.Require<Invoice>().Schema;
        Assert.Equal("Id", schema.IdField);
        Assert.Equal(["Id", "Title", "Body", "Modified", "Tags", "Note"], schema.Fields.Select(f => f.Name).ToArray());

        FieldDefinition title = schema.Fields.Single(f => f.Name == "Title");
        Assert.Equal(FieldType.Text, title.Type);
        Assert.Equal(2f, title.Boost);
        Assert.Equal("standard", title.Analyzer);
        Assert.Equal("en", schema.Fields.Single(f => f.Name == "Body").Analyzer);

        FieldDefinition modified = schema.Fields.Single(f => f.Name == "Modified");
        Assert.Equal(FieldType.Date, modified.Type);
        Assert.True(modified.Indexed);
        Assert.True(modified.Column);

        FieldDefinition tags = schema.Fields.Single(f => f.Name == "Tags");
        Assert.Equal(FieldType.Keyword, tags.Type);
        Assert.True(tags.Multi);
        Assert.True(tags.Facet);

        FieldDefinition note = schema.Fields.Single(f => f.Name == "Note");
        Assert.False(note.Indexed);
        Assert.True(note.Stored);
    }

    [Fact]
    public void ADocumentRoundTripsThroughTheMapping()
    {
        SearchMapping<Invoice> mapping = SearchMappings.Require<Invoice>();
        var invoice = new Invoice { Id = "i1", Title = "T", Body = "B", Modified = new DateTime(2024, 5, 6, 7, 8, 9, 10, DateTimeKind.Utc), Tags = ["a", "b"], Note = "n" };
        Invoice back = mapping.FromDocument(mapping.ToDocument(invoice));
        Assert.Equal(invoice.Id, back.Id);
        Assert.Equal(invoice.Title, back.Title);
        Assert.Equal(invoice.Modified, back.Modified);
        Assert.Equal(DateTimeKind.Utc, back.Modified.Kind);
        Assert.Equal(invoice.Tags, back.Tags);
        Assert.Equal("n", back.Note);
    }

    [Theory]
    [InlineData("[Vector] public float[] V { get; set; } = [];", "OI0007")]
    [InlineData("[Vector(Dimensions = 4)] public string V { get; set; } = \"\";", "OI0001")]
    [InlineData("[Vector(Dimensions = 4), Keyword] public float[] V { get; set; } = [];", "OI0003")]
    [InlineData("[Dynamic] public string V { get; set; } = \"\";", "OI0001")]
    [InlineData("[Dynamic, Filterable] public System.Text.Json.JsonElement V { get; set; }", "OI0003")]
    [InlineData("[FullText] public int V { get; set; }", "OI0001")]
    [InlineData("[Keyword] public System.Collections.Generic.List<int> V { get; set; } = new();", "OI0001")]
    [InlineData("[Filterable] public decimal V { get; set; }", "OI0001")]
    [InlineData("[FullText, Keyword] public string V { get; set; } = \"\";", "OI0003")]
    [InlineData("[FullText, Filterable] public string V { get; set; } = \"\";", "OI0003")]
    [InlineData("[Sortable] public string[] V { get; set; } = [];", "OI0003")]
    [InlineData("[SearchId] public string V { get; set; } = \"\";", "OI0004")]
    [InlineData("[SearchId] public int V { get; set; }", "OI0004")]
    [InlineData("[Keyword] public string V { get; init; } = \"\";", "OI0006")]
    [InlineData("[Keyword] public string V { get; } = \"\";", "OI0006")]
    [InlineData("[Keyword] public string V = \"\";", "OI0006")]
    [InlineData("[FullText(Boost = 0)] public string V { get; set; } = \"\";", "OI0007")]
    [InlineData("[MultiVector(Dimensions = 4)] public float[] V { get; set; } = [];", "OI0001")]
    [InlineData("[MultiVector(Dimensions = 4)] public string V { get; set; } = \"\";", "OI0001")]
    [InlineData("[MultiVector] public float[][] V { get; set; } = [];", "OI0007")]
    [InlineData("[MultiVector(Dimensions = 70000)] public float[][] V { get; set; } = [];", "OI0007")]
    [InlineData("[MultiVector(Dimensions = 4), Keyword] public float[][] V { get; set; } = [];", "OI0003")]
    [InlineData("[MultiVector(Dimensions = 4, From = \"Nope\")] public float[][] V { get; set; } = [];", "OI0007")]
    [InlineData("[MultiVector(Dimensions = 4, From = \"\")] public float[][] V { get; set; } = [];", "OI0007")]
    [InlineData("[MultiVector(Dimensions = 4, From = \"V\")] public float[][] V { get; set; } = [];", "OI0007")]
    public void AnUnsupportedMemberIsACompileErrorThatNamesIt(string member, string id)
    {
        string source = "using OneIndex; public class Bad { [SearchId] public string Id { get; set; } = \"\"; " + member + " }";
        (ImmutableArray<Diagnostic> generator, _, _) = Run(source);
        Assert.Contains(id, Ids(generator));
        Assert.All(generator.Where(d => d.Severity == DiagnosticSeverity.Error), d => Assert.Contains("V", d.GetMessage()));
    }

    [Fact]
    public void ATypeThatCannotBeCreatedOrReachedIsACompileError()
    {
        Assert.Contains("OI0005", Ids(Run("using OneIndex; public class Outer { private class Inner { [SearchId] public string Id { get; set; } = \"\"; } }").Generator));
        Assert.Contains("OI0005", Ids(Run("using OneIndex; public class G<T> { [SearchId] public string Id { get; set; } = \"\"; }").Generator));
        Assert.Contains("OI0005", Ids(Run("using OneIndex; public abstract class A { [SearchId] public string Id { get; set; } = \"\"; }").Generator));
        Assert.Contains("OI0005", Ids(Run("using OneIndex; public class C { public C(int x) {} [SearchId] public string Id { get; set; } = \"\"; }").Generator));
    }

    [Fact]
    public void ASecondIdentifierIsACompileError()
    {
        Assert.Contains("OI0004", Ids(Run("using OneIndex; public class C { [SearchId] public string A { get; set; } = \"\"; [SearchId] public string B { get; set; } = \"\"; }").Generator));
    }

    [Fact]
    public void ATypeWithoutAnIdentifierIsRefusedWhenAnIndexIsOpened()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<NoId>());
        Assert.Contains("SearchId", e.Message);
    }

    [Fact]
    public void ATypeWithoutAMappingSaysHowToGetOne()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<Unmapped>());
        Assert.Contains("no OneIndex mapping", e.Message);
    }

    [Fact]
    public void AnUnknownLanguageIsRefusedWhenAnIndexIsOpened()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SearchIndex.InMemory<BadLanguage>());
        Assert.Contains("xx", e.Message);
        Assert.Contains("Body", e.Message);
    }
}

public class NoId
{
    [FullText]
    public string Body { get; set; } = string.Empty;
}

public class Unmapped
{
    public string Body { get; set; } = string.Empty;
}

public class BadLanguage
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText(Language = "xx")]
    public string Body { get; set; } = string.Empty;
}
