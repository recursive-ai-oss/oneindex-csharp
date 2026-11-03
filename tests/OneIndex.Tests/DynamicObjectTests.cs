using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OneIndex.Generators;

namespace OneIndex.Tests;

public class Address
{
    public string City { get; set; } = "";

    public int Zip { get; set; }

    public List<string> Tags { get; set; } = [];
}

public class Customer
{
    public string Name { get; set; } = "";

    public Address? Home { get; set; }

    public Address[] Others { get; set; } = [];

    public double? Score { get; set; }

    public DateTime Since { get; set; }

    public Guid Key { get; set; }

    public bool Vip { get; set; }

    public short Rank { get; set; }

    public decimal Balance { get; set; }

    public IReadOnlyList<int> Numbers { get; set; } = [];

    // Not writable: left out of the JSON.
    public string Derived => Name + "!";
}

public class Account
{
    [SearchId]
    public string Id { get; set; } = "";

    [Dynamic]
    public Customer Owner { get; set; } = new();
}

public class DynamicObjectTests
{
    [Fact]
    public void AnObjectPropertyIsStoredAsJsonAndComesBackEqual()
    {
        using SearchIndex<Account> index = SearchIndex.InMemory<Account>();
        var since = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        var key = Guid.NewGuid();
        index.Upsert(new Account
        {
            Id = "a1",
            Owner = new Customer
            {
                Name = "Acme", Home = new Address { City = "Paris", Zip = 75001, Tags = ["hq", "old"] }, Others = [new Address { City = "Oslo" }, new Address { City = "Rome", Zip = 5 }],
                Score = 2.5, Since = since, Key = key, Vip = true, Rank = -3, Balance = 12.34m, Numbers = [1, 2, 3],
            },
        });
        index.Upsert(new Account { Id = "a2", Owner = new Customer { Name = "Bob", Home = new Address { City = "Rome" } } });
        index.Commit();

        Customer c = index.Search().Where("Owner.Name = Acme").Take(5).Single().Document.Owner;
        Assert.Equal("Acme", c.Name);
        Assert.Equal("Paris", c.Home!.City);
        Assert.Equal(75001, c.Home.Zip);
        Assert.Equal(["hq", "old"], c.Home.Tags);
        Assert.Equal(["Oslo", "Rome"], c.Others.Select(o => o.City));
        Assert.Equal(2.5, c.Score);
        Assert.Equal(since, c.Since.ToUniversalTime());
        Assert.Equal(key, c.Key);
        Assert.True(c.Vip);
        Assert.Equal((short)-3, c.Rank);
        Assert.Equal(12.34m, c.Balance);
        Assert.Equal([1, 2, 3], c.Numbers);

        // Indexed by path like any dynamic JSON: nested objects and arrays of objects.
        Assert.Equal(["a2"], index.Search().Where("Owner.Home.City = Rome AND Owner.Vip != true").Take(5).Select(h => h.Id));
        Assert.Equal(["a1", "a2"], index.Search().Where("Owner.Others.City = Rome OR Owner.Home.City = Rome").Take(5).Select(h => h.Id).Order(StringComparer.Ordinal));
    }

    private static (ImmutableArray<Diagnostic> Generator, ImmutableArray<Diagnostic> Compilation) Run(string source)
    {
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(SearchIndex).Assembly.Location))
            .ToArray();
        CSharpCompilation compilation = CSharpCompilation.Create("dyn-test", [CSharpSyntaxTree.ParseText(source)], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        GeneratorDriver driver = CSharpGeneratorDriver.Create(new MappingGenerator());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out Compilation output, out ImmutableArray<Diagnostic> generator);
        return (generator, output.GetDiagnostics());
    }

    [Fact]
    public void TheGeneratedCodeForObjectsHasNoDiagnostics()
    {
        (ImmutableArray<Diagnostic> generator, ImmutableArray<Diagnostic> compilation) = Run("""
            using OneIndex; using System.Collections.Generic;
            namespace App;
            public class Leaf { public string? A { get; set; } public int? N { get; set; } public List<Leaf> Kids { get; set; } = new(); }
            public class Doc { [SearchId] public string Id { get; set; } = ""; [Dynamic] public Leaf Tree { get; set; } = new(); }
            """);
        Assert.Empty(generator);
        Assert.Empty(compilation.Where(d => d.Severity >= DiagnosticSeverity.Warning));
    }

    [Theory]
    [InlineData("public System.Collections.Generic.Dictionary<string, int> Bag { get; set; } = new();")]
    [InlineData("public int[][] Grid { get; set; } = new int[0][];")]
    [InlineData("public System.TimeSpan Span { get; set; }")]
    [InlineData("public object Anything { get; set; } = new();")]
    public void AnUnsupportedPropertyIsRefusedByNameAtBuildTime(string property)
    {
        (ImmutableArray<Diagnostic> generator, _) = Run("using OneIndex; namespace App; public class Inner { " + property + " } public class Doc { [SearchId] public string Id { get; set; } = \"\"; [Dynamic] public Inner Data { get; set; } = new(); }");
        Diagnostic d = Assert.Single(generator, x => x.Severity == DiagnosticSeverity.Error);
        Assert.Equal("OI0001", d.Id);
        Assert.Contains("Inner.", d.GetMessage());
    }

    [Fact]
    public void OtherAttributesOnAnObjectMemberStillConflict()
    {
        (ImmutableArray<Diagnostic> generator, _) = Run("using OneIndex; namespace App; public class Inner { public string A { get; set; } = \"\"; } public class Doc { [SearchId] public string Id { get; set; } = \"\"; [Dynamic, Filterable] public Inner Data { get; set; } = new(); }");
        Assert.Contains(generator, x => x.Id == "OI0003");
    }
}
