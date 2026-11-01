using OneIndex.Index;
using OneIndex.Queries;

namespace OneIndex.Fuzz;

/// <summary>A small document type for the user-text target: a stemmed body, a plain title, a keyword and a number.</summary>
public class FuzzDocument
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Tag { get; set; } = string.Empty;

    public int Number { get; set; }

    public GeoPoint? Place { get; set; }
}

/// <summary>
/// The mapping of <see cref="FuzzDocument"/>, written by hand (the low-level API). The generator's registration runs when the
/// assembly loads, which is before the instrumented library has its coverage memory; registering inside the target avoids that.
/// </summary>
internal sealed class FuzzMapping : SearchMapping<FuzzDocument>
{
    public FuzzMapping()
        : base(
        [
            new MappedMember("Id", FieldType.Keyword, multi: false, indexed: true, column: true, facet: false, fullText: false, isId: true, boost: 1f, analyzer: null),
            new MappedMember("Title", FieldType.Text, multi: false, indexed: true, column: false, facet: false, fullText: true, isId: false, boost: 2f, analyzer: null),
            new MappedMember("Body", FieldType.Text, multi: false, indexed: true, column: false, facet: false, fullText: true, isId: false, boost: 1f, analyzer: "en"),
            new MappedMember("Tag", FieldType.Keyword, multi: false, indexed: true, column: false, facet: false, fullText: false, isId: false, boost: 1f, analyzer: null),
            new MappedMember("Number", FieldType.Integral, multi: false, indexed: true, column: true, facet: false, fullText: false, isId: false, boost: 1f, analyzer: null),
            new MappedMember("Place", FieldType.GeoPoint, multi: false, indexed: true, column: true, facet: false, fullText: false, isId: false, boost: 1f, analyzer: null),
            new MappedMember("Vec", FieldType.Vector, multi: false, indexed: true, column: false, facet: false, fullText: false, isId: false, boost: 1f, analyzer: null, dimensions: 3),
        ])
    {
    }

    // Typo tolerance with low thresholds, a listed word, synonyms of both kinds and stop words, so that the settings paths are fuzzed too.
    protected override TextSettings CreateSettings() => new()
    {
        Typo = new TypoSettings { MinWordSizeForOneTypo = 3, MinWordSizeForTwoTypos = 6, DisableOnWords = ["cat"], DisableOnFields = ["Title"] },
        Synonyms = [new SynonymRule(["dog", "cat"], []), new SynonymRule(["acme corp"], ["overdue", "state of the art"])],
        StopWords = ["the", "of"],
    };

    public override string? GetId(FuzzDocument value) => value.Id;

    public override Document ToDocument(FuzzDocument value)
    {
        Document d = new Document().Add("Id", value.Id).Add("Title", value.Title).Add("Body", value.Body).Add("Tag", value.Tag).Add("Number", (long)value.Number);
        return value.Place is { } p ? d.Add("Place", FieldValue.FromGeoPoint(p.Latitude, p.Longitude)) : d;
    }

    public override FuzzDocument FromDocument(Document document)
        => new()
        {
            Id = document.Get("Id").AsText(),
            Title = document.Get("Title").AsText(),
            Body = document.Get("Body").AsText(),
            Tag = document.Get("Tag").AsText(),
            Number = (int)document.Get("Number").AsInt64(),
            Place = document.Fields.Any(f => f.Name == "Place") ? new GeoPoint(document.Get("Place").AsGeoPoint().Latitude, document.Get("Place").AsGeoPoint().Longitude) : null,
        };
}

/// <summary>
/// Feeds arbitrary UTF-16 text (lone surrogates and control characters included) to the user-text parser and to a search over a
/// small index, with and without a filter. Nothing may be thrown, and a search must return a bounded, finite-scored page.
/// </summary>
internal static class UserTextTarget
{
    private static SearchIndex<FuzzDocument>? built;

    public static void Run(ReadOnlySpan<byte> data)
    {
        SearchIndex<FuzzDocument> index = built ??= Build();
        var chars = new char[data.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)(data[2 * i] | (data[(2 * i) + 1] << 8));
        }

        string text = new(chars);
        UserText parsed = UserText.Parse(text);
        Require(parsed.Required.Count + parsed.Excluded.Count <= UserText.MaxOperands, "operand cap");
        foreach (IReadOnlyList<UserTextOperand> group in parsed.Required)
        {
            Require(group.Count > 0, "empty group");
            foreach (UserTextOperand o in group)
            {
                Require(o.Text.Length <= UserText.MaxLength && !o.Text.Contains('\0'), "operand text");
            }
        }

        SearchResults<FuzzDocument> page = index.Search(text).Take(5).Execute();
        Require(page.Count <= 5 && page.TotalHits >= page.Count, "page");
        foreach (SearchHit<FuzzDocument> hit in page)
        {
            Require(float.IsFinite(hit.Score), "score");
            _ = hit.Highlights;
        }

        int bound = chars.Length % 7;
        _ = index.Search(text).Where(x => x.Number > bound).Skip(1).Take(3).CountExactly().Count();
    }

    public static void WriteSeeds(string directory)
    {
        string[] seeds = ["overdue \"acme corp\" -draft", "cat OR dog", "\"unterminated", "-", "state-of-the-art", "", "\ud83d\ude00 \ud800 x", "acmee cor", "overdu runnr", "the of", "acme corp state of the a"];
        for (int i = 0; i < seeds.Length; i++)
        {
            byte[] bytes = new byte[seeds[i].Length * 2];
            for (int c = 0; c < seeds[i].Length; c++)
            {
                bytes[2 * c] = (byte)seeds[i][c];
                bytes[(2 * c) + 1] = (byte)(seeds[i][c] >> 8);
            }

            File.WriteAllBytes(Path.Combine(directory, "seed-" + i), bytes);
        }
    }

    private static SearchIndex<FuzzDocument> Build()
    {
        SearchMappings.Register(new FuzzMapping());
        SearchIndex<FuzzDocument> index = SearchIndex.InMemory<FuzzDocument>();
        string[] words = ["overdue", "acme", "corp", "draft", "cat", "dog", "state", "art", "running", "runner", "the", "of", "\u6771\u4eac\u90fd", "caf\u00e9"];
        for (int i = 0; i < 40; i++)
        {
            index.Upsert(new FuzzDocument
            {
                Id = "d" + i,
                Title = words[i % words.Length] + " " + words[(i * 3) % words.Length],
                Body = string.Join(' ', Enumerable.Range(0, 8).Select(k => words[(i + (k * 5)) % words.Length])),
                Tag = words[i % 4],
                Number = i,
            });
        }

        index.Commit();
        return index;
    }

    private static void Require(bool condition, string what)
    {
        if (!condition)
        {
            throw new InvalidOperationException("user text invariant failed: " + what);
        }
    }
}
