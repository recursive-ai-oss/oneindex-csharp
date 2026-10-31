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

Numeric widening conversions the compiler inserts are looked through (`x.Count > someLong`). Comparing an integer member with a fractional value rounds the bound inward (`x.Count > 2.5` means `>= 3`). Nullable members compare as C# does for non-null values; a document without the value matches `!=` and `!` and nothing else, as lifted comparisons do.

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

## Totals and paging

`Take(n)` (default 20) and `Skip(n)` page by rank; `Skip + Take` may not exceed 10,000. `TotalHits` and `Count()` are lower bounds unless `CountExactly()` was called. Enumeration, `ToList()` and `Execute()` run the search; `ExecuteAsync` is the async twin.

The differential test (`LinqTests`) compares `Where` against LINQ-to-Objects over seeded random documents (with updates and deletes between commits) and seeded random expression trees.
