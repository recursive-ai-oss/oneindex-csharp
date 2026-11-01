# Filter strings

A filter string selects documents by field values, with conditions joined by `AND`, `OR` and `NOT`. The syntax follows Meilisearch's documented filter expressions. A filter never scores.

```csharp
Query filter = FilterString.Parse("Tags = invoice AND (Total >= 100 OR Priority = 'high') AND Paid = false", index.Schema);
```

In query JSON a filter string is the value of `"filter"` (see [query-json.md](query-json.md)).

## Grammar

```
filter    := or
or        := and ( OR and )*
and       := unary ( AND unary )*
unary     := NOT unary | "(" or ")" | condition
condition := field "=" value | field "!=" value
           | field ( ">" | ">=" | "<" | "<=" ) value
           | field value TO value
           | field [ NOT ] IN "[" [ value ( "," value )* ] "]"
           | field [ NOT ] EXISTS
           | field IS [ NOT ] ( NULL | EMPTY )
           | "_geoRadius" "(" [ field "," ] lat "," lng "," meters ")"
           | "_geoBoundingBox" "(" [ field "," ] "[" lat "," lng "]" "," "[" lat "," lng "]" ")"
field     := word | quoted
value     := word | quoted
quoted    := '...' | "..."          inside, \\ \" \' are the escapes
```

- Keywords (`AND OR NOT TO IN EXISTS IS NULL EMPTY`) are case-insensitive. `NOT` binds tighter than `AND`, which binds tighter than `OR`. To use a keyword as a field name or a value, quote it (`Color = "AND"`).
- A word runs up to white space or one of `( ) [ ] , = ! < > ' "`. It may hold `:`, `-`, `.`, `+` and the like, so `2024-01-31T10:00:00Z` and `-3.5` need no quotes. Text with spaces needs quotes.
- Limits: 8,192 characters, depth 64 (parentheses and nested `NOT`s), 512 conditions, 1,024 values in an `IN` list.

## Fields and values

A field must exist in the schema, be indexed (`[Filterable]`, `[Keyword]`, or the identifier), and not be full-text; `Unfiltered = x` and `Body = x` are errors that say why. The value is read by the field's type:

| Field | Value | Notes |
|---|---|---|
| keyword (also multi-valued) | any text | compared exactly: case-sensitive, no analysis (Meilisearch compares strings case-insensitively; this is a documented difference). On a multi-valued field a condition holds when any element matches. `>` `<` `TO` compare in byte order of the UTF-8 text. |
| integer | a whole number in the `long` range | a fractional number is rounded inward: `> 2.5` is `>= 3`, `<= 2.5` is `<= 2`, `2.5 TO 7.5` is `3 TO 7`; `= 2.5` matches nothing and `!= 2.5` everything |
| number | a finite decimal number, with optional exponent | |
| date | `2024-01-31`, `2024-01-31T10:00`, `...T10:00:00`, with fraction and `Z` or an offset | UTC unless an offset is given; stored with millisecond precision |
| boolean | `true` or `false` (any case) | only `=` and `!=`, `IN`, `EXISTS` |

Comparing with a quoted number or date (`Total > "100"`) is accepted; a value that does not fit the field's type is an error pointing at the value, naming what the field holds.

## Meaning

- `a != v` is `NOT a = v`: documents without a value for the field match it, as `NOT` does in Meilisearch.
- `field IN [a, b]` is `field = a OR field = b`; an empty list matches nothing.
- `field EXISTS` holds when the document has a value. `IS NULL` and `IS EMPTY` hold when it has none: a document never stores an explicit null, and empty strings and empty arrays are not indexed, so the two tests are the same as `NOT ... EXISTS` (Meilisearch distinguishes null from empty; this index does not). `IS NOT NULL` and `IS NOT EMPTY` are `EXISTS`.
- `field a TO b` is `field >= a AND field <= b` (both bounds included).
- `_geoRadius(lat, lng, meters)` holds for documents with a point within the distance; `_geoBoundingBox([topRightLat, topRightLng], [bottomLeftLat, bottomLeftLng])` for documents with a point in the rectangle, crossing the antimeridian when the bottom left longitude is greater than the top right one. The geo field is named first when the schema has several (`_geoRadius(Location, 45.47, 9.18, 2000)`); with one searchable geo field it can be left out. The names are case-insensitive; numbers are plain decimals, latitude -90 to 90, longitude -180 to 180, radius not negative, and errors point at the number. Details: [geo.md](geo.md). They combine with `AND`, `OR` and `NOT` like any condition. `Print` always writes the field name and numbers in shortest round-trip form.
- `CONTAINS`, `STARTS WITH` and array-form filters are not supported yet.

## Query objects

`AND` is a boolean query of `Filter` clauses, `OR` of `Should` clauses with `MinimumShouldMatch` 1, `NOT x` is `[Filter MatchAll, MustNot x]`. Conditions are `TermQuery` (keywords), `GeoRadiusQuery` and `GeoBoundingBoxQuery` (the geo functions) and `RangeQuery` (everything else; `=` is a range with equal bounds; `EXISTS` a range with no bounds). A chain `a AND b AND c` is one boolean query with three clauses; parentheses make a nested one.

## Errors and printing

`QueryParseException` has the offset, line, column, what was expected, and for unknown fields the nearest field name (`Did you mean 'Color'?`). Syntax errors point at the token that cannot continue the filter; field errors at the field name; value errors at the value; a missing token at the end of the input.

`FilterString.Print(query, schema)` writes the query shapes above back as text that parses to an equal query: strings in double quotes, dates as `"yyyy-MM-ddTHH:mm:ss.fffZ"`, a one-valued range as `field = value`, all booleans below the top parenthesized.
