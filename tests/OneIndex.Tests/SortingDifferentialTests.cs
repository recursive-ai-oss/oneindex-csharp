using System.Globalization;
using OneIndex.Search;

namespace OneIndex.Tests;

public class Venue
{
    [SearchId]
    public string Id { get; set; } = string.Empty;

    [FullText]
    public string Title { get; set; } = string.Empty;

    [Keyword]
    [Sortable]
    public string? Name { get; set; }

    [Filterable]
    [Sortable]
    public int? Rating { get; set; }

    [Filterable]
    [Sortable]
    public double? Price { get; set; }

    [Filterable]
    [Sortable]
    public DateTime? Opened { get; set; }

    [Filterable]
    [Sortable]
    public bool? Open { get; set; }

    [Geo]
    public GeoPoint? Location { get; set; }
}

/// <summary>
/// The sort and search-after paths against a model written with LINQ-to-Objects: the same documents ordered by the same keys, ties by
/// identifier, documents without a value last (or first). Walks run across commits with updates and deletes between pages, and every page
/// is compared with what the model says should follow the cursor.
/// </summary>
public sealed class SortingDifferentialTests(ITestOutputHelper output)
{
    private static readonly string[] Names = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel"];
    private static readonly double[] Prices = [0.0, -0.0, 1.5, 2.5, 10, 99.99, -4, 1e9];

    private static int Cases => int.TryParse(Environment.GetEnvironmentVariable("ONEINDEX_DIFF_CASES"), CultureInfo.InvariantCulture, out int n) ? n : 40;

    private enum Field
    {
        Name,
        Rating,
        Price,
        Opened,
        Open,
    }

    private sealed record Key(Field Field, bool Descending, bool MissingFirst);

    private static Venue Make(Random r, string id)
    {
        bool has() => r.Next(100) >= 15;
        return new Venue
        {
            Id = id,
            Title = "venue " + id,
            Name = has() ? Names[r.Next(Names.Length)] : null,
            Rating = has() ? r.Next(0, 6) : null,
            Price = has() ? Prices[r.Next(Prices.Length)] : null,
            Opened = has() ? new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(r.Next(0, 8)) : null,
            Open = has() ? r.Next(2) == 0 : null,
        };
    }

    private static object? Value(Venue v, Field f) => f switch
    {
        Field.Name => v.Name,
        Field.Rating => v.Rating,
        Field.Price => v.Price,
        Field.Opened => v.Opened,
        _ => v.Open,
    };

    // A model of the order: the keys in turn with the missing placement, then the identifier in byte order.
    private static int Compare(Venue a, Venue b, IReadOnlyList<Key> keys)
    {
        foreach (Key k in keys)
        {
            object? x = Value(a, k.Field);
            object? y = Value(b, k.Field);
            if (x is null || y is null)
            {
                if (x is null && y is null)
                {
                    continue;
                }

                bool aMissing = x is null;
                return aMissing == k.MissingFirst ? -1 : 1;
            }

            int c = x switch
            {
                string s => string.CompareOrdinal(s, (string)y),
                int i => i.CompareTo((int)y),
                double d => d.CompareTo((double)y),
                DateTime t => t.CompareTo((DateTime)y),
                _ => ((bool)x).CompareTo((bool)y),
            };
            if (c != 0)
            {
                return k.Descending ? -c : c;
            }
        }

        return string.CompareOrdinal(a.Id, b.Id);
    }

    private static List<Key> RandomKeys(Random r)
    {
        var fields = Enum.GetValues<Field>().OrderBy(_ => r.Next()).Take(r.Next(1, 5)).ToList();
        return fields.ConvertAll(f => new Key(f, r.Next(2) == 0, r.Next(4) == 0));
    }

    private static SearchQuery<Venue> Apply(SearchQuery<Venue> q, IReadOnlyList<Key> keys)
    {
        for (int i = 0; i < keys.Count; i++)
        {
            Key k = keys[i];
            MissingValuePlacement m = k.MissingFirst ? MissingValuePlacement.First : MissingValuePlacement.Last;
            q = (i, k.Field, k.Descending) switch
            {
                (0, Field.Name, false) => q.OrderBy(x => x.Name, m),
                (0, Field.Name, true) => q.OrderByDescending(x => x.Name, m),
                (_, Field.Name, false) => q.ThenBy(x => x.Name, m),
                (_, Field.Name, true) => q.ThenByDescending(x => x.Name, m),
                (0, Field.Rating, false) => q.OrderBy(x => x.Rating, m),
                (0, Field.Rating, true) => q.OrderByDescending(x => x.Rating, m),
                (_, Field.Rating, false) => q.ThenBy(x => x.Rating, m),
                (_, Field.Rating, true) => q.ThenByDescending(x => x.Rating, m),
                (0, Field.Price, false) => q.OrderBy(x => x.Price, m),
                (0, Field.Price, true) => q.OrderByDescending(x => x.Price, m),
                (_, Field.Price, false) => q.ThenBy(x => x.Price, m),
                (_, Field.Price, true) => q.ThenByDescending(x => x.Price, m),
                (0, Field.Opened, false) => q.OrderBy(x => x.Opened, m),
                (0, Field.Opened, true) => q.OrderByDescending(x => x.Opened, m),
                (_, Field.Opened, false) => q.ThenBy(x => x.Opened, m),
                (_, Field.Opened, true) => q.ThenByDescending(x => x.Opened, m),
                (0, Field.Open, false) => q.OrderBy(x => x.Open, m),
                (0, Field.Open, true) => q.OrderByDescending(x => x.Open, m),
                (_, Field.Open, false) => q.ThenBy(x => x.Open, m),
                _ => q.ThenByDescending(x => x.Open, m),
            };
        }

        return q;
    }

    private static string Ids(IEnumerable<Venue> v) => string.Join(",", v.Select(x => x.Id));

    [Fact]
    public void SortedPagesMatchLinqToObjects()
    {
        long pages = 0;
        for (int seed = 1; seed <= Cases; seed++)
        {
            var r = new Random(seed);
            using SearchIndex<Venue> index = SearchIndex.InMemory<Venue>();
            var corpus = new Dictionary<string, Venue>();
            int n = r.Next(5, 260);
            for (int i = 0; i < n; i++)
            {
                Venue v = Make(r, "v" + i.ToString("D4", CultureInfo.InvariantCulture));
                corpus[v.Id] = v;
                index.Upsert(v);
                if (r.Next(60) == 0)
                {
                    index.Commit(); // several segments, and merges when there are many
                }
            }

            index.Commit();
            List<Key> keys = RandomKeys(r);
            Venue[] expected = [.. corpus.Values.OrderBy(x => x, Comparer<Venue>.Create((a, b) => Compare(a, b, keys)))];

            // The whole order in one page, then Skip/Take windows.
            SearchResults<Venue> all = Apply(index.Search().Take(500), keys).Execute();
            Assert.Equal(Ids(expected), Ids(all.Select(h => h.Document)));
            Assert.Equal(n, all.TotalHits);
            Assert.True(all.TotalHitsExact);
            int skip = r.Next(0, n);
            int take = r.Next(1, 40);
            Assert.Equal(Ids(expected.Skip(skip).Take(take)), Ids(Apply(index.Search().Skip(skip).Take(take), keys).Execute().Select(h => h.Document)));
            pages += 2;

            // A filter narrows the same order.
            Venue[] filtered = [.. expected.Where(x => x.Rating >= 3)];
            Assert.Equal(Ids(filtered.Take(30)), Ids(Apply(index.Search().Where(x => x.Rating >= 3).Take(30), keys).Execute().Select(h => h.Document)));
            pages++;
        }

        output.WriteLine($"{Cases} corpora, {pages} pages compared");
    }

    [Fact]
    public void SearchAfterWalksReturnEveryDocumentOnceAndSurviveChangesBetweenPages()
    {
        long walks = 0;
        long pagesChecked = 0;
        for (int seed = 1000; seed < 1000 + Cases; seed++)
        {
            var r = new Random(seed);
            using SearchIndex<Venue> index = SearchIndex.InMemory<Venue>();
            var corpus = new Dictionary<string, Venue>();
            int n = r.Next(3, 200);
            for (int i = 0; i < n; i++)
            {
                Venue v = Make(r, "v" + i.ToString("D4", CultureInfo.InvariantCulture));
                corpus[v.Id] = v;
                index.Upsert(v);
            }

            index.Commit();
            List<Key> keys = RandomKeys(r);
            int pageSize = r.Next(1, 25);
            bool mutate = seed % 2 == 0;
            int nextId = n;
            var seen = new List<string>();
            Venue? last = null;
            SortCursor? cursor = null;
            while (true)
            {
                SearchQuery<Venue> q = Apply(index.Search().Take(pageSize), keys);
                if (cursor is not null)
                {
                    // Round-trip the token the way an application passing it to a client would.
                    string token = cursor.ToToken();
                    Assert.True(SortCursor.TryParse(token, out SortCursor? parsed));
                    q = q.SearchAfter(token);
                    cursor = parsed;
                }

                SearchResults<Venue> page = q.Execute();
                Venue[] model = [.. corpus.Values.Where(x => last is null || Compare(x, last, keys) > 0).OrderBy(x => x, Comparer<Venue>.Create((a, b) => Compare(a, b, keys))).Take(pageSize)];
                Assert.Equal(Ids(model), Ids(page.Select(h => h.Document)));
                pagesChecked++;
                if (page.Count == 0)
                {
                    break;
                }

                seen.AddRange(page.Select(h => h.Id));
                last = page[^1].Document;
                cursor = page.NextCursor;
                Assert.NotNull(cursor);
                if (page.Count < pageSize)
                {
                    break;
                }

                if (mutate)
                {
                    // Documents come, go and change between pages; the cursor holds values, not a position.
                    for (int m = r.Next(0, 4); m > 0; m--)
                    {
                        string id = r.Next(2) == 0 ? "v" + r.Next(0, nextId).ToString("D4", CultureInfo.InvariantCulture) : "v" + (nextId++).ToString("D4", CultureInfo.InvariantCulture);
                        if (r.Next(3) == 0)
                        {
                            index.Delete(id);
                            corpus.Remove(id);
                        }
                        else
                        {
                            Venue v = Make(r, id);
                            corpus[id] = v;
                            index.Upsert(v);
                        }
                    }

                    index.Commit();
                }
            }

            if (!mutate)
            {
                // Nothing changed, so the walk is the whole order and no document appears twice. (With changes, a document whose
                // sort values moved past the cursor legitimately appears again; each page was compared with the model above.)
                Assert.Equal(seen.Count, seen.Distinct().Count());
                Assert.Equal(Ids(corpus.Values.OrderBy(x => x, Comparer<Venue>.Create((a, b) => Compare(a, b, keys)))), string.Join(",", seen));
            }

            walks++;
        }

        output.WriteLine($"{walks} walks, {pagesChecked} pages compared with the model");
    }
}
