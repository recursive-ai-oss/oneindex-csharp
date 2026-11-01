# LINQ filters

`index.Search(text).Where(predicate)` narrows a search with a C# lambda. The lambda is **translated, never run**: the engine walks the expression tree and builds query objects (a `BooleanQuery` of `Filter` clauses over `TermQuery` and `RangeQuery`). It does not compile the tree. Filters never change scores. Several `Where` calls combine with and.

The pieces of the lambda that do not mention the document (captured variables, constants, static members, `new DateTime(...)`, `now.AddDays(-7)`, `min - 1`) are read directly from the tree, with `FieldInfo`/`PropertyInfo` reads and method calls, and compared as values.

## What may appear

| Form | Fields | Becomes |
|---|---|---|
| `x.M == v`, `x.M != v` | `[Keyword]` strings; numbers, dates and booleans marked `[Filterable]` (or `[Facet]`) | term / one-value range; `!=` is its complement |
| `x.M < v`, `<=`, `>`, `>=` (the constant may be on either side) | numbers and dates | one-sided range |
| `x.Flag`, `!x.Flag` | `bool` | `== true`, `== false` |
| `x.Tags.Contains(v)` | a collection of strings marked `[Keyword]` | term (any element equals `v`) |
| `list.Contains(x.M)` | any filterable member; `list` is captured or constant | any of the list's values; an empty list matches nothing |
| `a && b`, `a \|\| b`, `!a` | | boolean clauses |
| a constant condition (`true`, a captured `bool`) | | everything or nothing |
| `Geo.WithinRadius(x.Location, lat, lng, meters)`, `Geo.WithinBoundingBox(x.Location, north, south, west, east)` | a `[Geo]` `GeoPoint` property (`x.Location.Value` for a nullable one); the numbers may not use the document | geo queries ([geo.md](geo.md)) |

Numeric widening conversions the compiler inserts are looked through (`x.Count > someLong`). Comparing an integer member with a fractional value rounds the bound inward (`x.Count > 2.5` means `>= 3`). Nullable members compare as C# does for non-null values; a document without the value matches `!=` and `!` and nothing else, as lifted comparisons do.

## Paths in dynamic JSON

A `[Dynamic]` property is reached with `Path`: `x.Meta.Path("customer.age") >= 18`, `x.Meta.Path("tags") == "red"`, `x.Meta.Path("customer.name").Exists()`. The comparison is with a string, a number, a `bool` or a date; the member must be on the left. See dynamic.md for what "any value at the path" means and how the path is written.

## What fails

Anything else throws `UnsupportedExpressionException` (a `NotSupportedException`) **when `Where` is called**, naming the sub-expression: `The expression '(x.Text == "doc 1")' cannot be used in Where: 'Text' is full-text; match it with the search text, or filter on a separate [Keyword] property.` The usual causes:

- a member that is not filterable (`'Note' is not filterable; mark it [Filterable] (or [Keyword] for text)`), or is full-text;
- comparing with `null`, or with something that uses the document (`x.Count > x.Big`);
- string methods (`StartsWith`, `Contains` on a string, `Equals`, `Length`), `ToString()`, other calls on the document's members, nested paths such as `x.Customer.Name`, `Any`, `All`, conditional expressions;
- ordering comparisons on keywords or booleans;
- a captured value that cannot be read (null reference inside it).

## Precision

- Dates are stored as UTC milliseconds. `DateTime` of kind `Local` is converted to UTC, `Unspecified` is read as UTC; reading back gives kind `Utc`. Constants in filters are rounded the same way, so a stored `2024-01-01T00:00:00.0005Z` equals a constant of `...0.000Z`; sub-millisecond differences are not visible to filters.
- `float` members are stored as `double`, exactly.
- Keyword values are compared exactly (case-sensitive, no analysis). Empty strings are not indexed.

## Ordering

`OrderBy`, `OrderByDescending`, `ThenBy`, `ThenByDescending` take a plain property read (`x => x.Modified`) of a keyword, number, date or bool member with a column, and an optional `MissingValuePlacement`; `OrderByDistance`/`ThenByDistance` order by distance from a point, `OrderByRelevance`/`ThenByRelevance` by score. `OrderBy` replaces an earlier ordering, `ThenBy` without one throws. Anything else throws `ArgumentException` naming the expression and what to mark `[Sortable]`. Ties, missing values, multi-valued members and search-after paging are specified in [sorting.md](sorting.md).

## Totals and paging

`Take(n)` (default 20) and `Skip(n)` page by rank (or by the ordering); `SearchAfter(cursor)` continues a sorted search after the cursor of a hit; `Skip + Take` may not exceed 10,000. `TotalHits` and `Count()` are lower bounds unless `CountExactly()` was called. Enumeration, `ToList()` and `Execute()` run the search; `ExecuteAsync` is the async twin.

The differential test (`LinqTests`) compares `Where` against LINQ-to-Objects over seeded random documents (with updates and deletes between commits) and seeded random expression trees.

## Filter strings

`Where(string)` takes a [filter string](filter-strings.md) (`Where("Rating >= 4 AND Tags = invoice")`). It is checked against the schema at the call and throws `QueryParseException` with the offset, line, column and a suggestion for a misspelt field. It selects without scoring and combines with `Where(predicate)` and other `Where(string)` calls by and. A test runs the same random predicates as the LINQ differential through both forms (the translated query printed as a filter string) and against LINQ-to-Objects.

## Score by functions and explain

`ScoreBy(b => ...)` changes the relevance score with functions of column values ([function-scores.md](function-scores.md)); `Explain()` attaches the score breakdown to each returned hit (`hit.Explanation`, whose root value equals `hit.Score`).
