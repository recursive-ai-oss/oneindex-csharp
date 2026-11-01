# Sorting and paging

This note is normative for ordering hits by columns and for paging with a cursor. The columns are in [columns.md](columns.md); the query surfaces that expose it are [linq.md](linq.md) and [query-json.md](query-json.md).

```csharp
var page = index.Search("overdue")
                .Where(x => x.Paid == false)
                .OrderByDescending(x => x.Modified)
                .ThenBy(x => x.Customer)
                .Take(20)
                .Execute();
var next = index.Search("overdue").Where(x => x.Paid == false)
                .OrderByDescending(x => x.Modified).ThenBy(x => x.Customer)
                .SearchAfter(page.NextCursor!).Take(20).Execute();
```

## The order

A sort is one to sixteen **keys**, applied in turn; a later key only separates documents the earlier ones tie. The order is total:

1. Each key compares the documents' values. A key is a column (`FieldSort`), the distance from a point (`GeoDistanceSort`, see [geo.md](geo.md)) or the relevance score (`ScoreSort`).
2. Documents with no value for a key go **last**, or **first** when the key says so (`MissingValuePlacement`). The placement does not depend on the direction: a descending key with missing values last puts them after the smallest value. (LINQ-to-Objects would put a missing value first when ascending; this follows Meilisearch and Elasticsearch, which put missing values last, and the placement is a parameter.)
3. The final tie-break is the document identifier in byte order of its UTF-8 text when the schema has an identifier field, else the global document id.

The identifier tie-break is what keeps the order the same across merges, which renumber documents. It is byte order, so it agrees with `string.CompareOrdinal` for text without supplementary-plane characters.

### Value order per column type

| Column | Order |
|---|---|
| keyword | byte order of the UTF-8 text |
| integral, date | the number; dates are milliseconds since 1970 |
| bool | false before true |
| fractional | numeric order; `-0.0` and `0.0` tie; NaN after every number |
| geo point | not sortable as a column; use `GeoDistanceSort` |
| text, bytes, vector, JSON | not sortable |

A document with several values for a key is placed by its **smallest** value when the key ascends and its **largest** when it descends (Elasticsearch's `min` and `max` modes).

### Score keys

`ScoreSort` orders by the BM25F score, descending by default. A sort without a score key does not compute scores at all (hits report `Score` 0), which is what makes it cheap; use `ThenByRelevance()` to break ties by score. NaN scores sort as the lowest.

## Execution

`TopFieldCollector` visits every match and keeps the first *k* in a binary heap whose root is the worst kept document, reading the key values from the segment's columns. The count of matches is therefore exact. Keyword values are copied into a buffer the collector owns, and allocation happens only when a document enters the kept set. Facets, aggregations and exact counts ride the same pass (`MultiCollector`).

Sorting by a column is not combined with `Distinct` (which ranks groups by relevance); that combination throws `NotSupportedException`.

## Paging

- **`Skip` and `Take`** page within the result window (`Skip + Take` at most 10,000). The cost grows with the depth because the collector keeps `Skip + Take` documents.
- **Search-after** (`SearchAfter(cursor)`, Elasticsearch's `search_after`; Meilisearch has only offset and limit paging) continues after a cursor and costs the same on every page. A `SortCursor` is the last hit's sort values plus its identifier (or global document id when the schema has no identifier field). A search-after search keeps only documents that sort **strictly after** the cursor under the same total order; `TotalHits` still counts every match of the query.

`Skip` and `SearchAfter` do not combine (`InvalidOperationException`).

### Stability

The cursor holds values and an identifier, not a position, so it does not depend on which commit it was made against:

- A document that exists unchanged from the first page to the last appears exactly once in a full walk, whatever else is added, changed or removed in between.
- A document whose sort values change can appear again (or not at all) if its new place is after (or before) the cursor, which is what the new values mean.
- A cursor over a score key is as stable as the scores, which depend on the whole collection.
- A cursor made without identifiers (a low-level index whose schema has no identifier field) holds the global document id, which a merge changes: it is valid within one snapshot only.

The walk test (`SortingDifferentialTests`) checks every page of every walk against a model that applies the same order to the current documents.

### Token form

`SortCursor.ToToken()` writes `version 1`, a CRC-32C of the canonical sort description, the key count and, per key, a kind byte (0 missing, 1 number as 8 bytes little-endian, 2 text as varuint length and bytes), then the identifier (kind 1: varuint length and bytes) or the document id (kind 2: 4 bytes), in unpadded URL-safe base64. A cursor only fits the sort that made it: the checksum is compared and a different sort raises `ArgumentException`. `SortCursor.TryParse` rejects malformed tokens without throwing and never reads past its input (at most 64 keys, 64 Ki characters).

## Query JSON

`"sort"` is an array of strings, after Meilisearch's sort parameter: `Field:asc`, `Field:desc`, `_geoPoint(lat, lng):asc` (naming the geo field first, `_geoPoint(Place, lat, lng)`, when the schema has several), `_score` (descending unless `:asc`), each optionally ending in `:first` or `:last` for missing values. `"searchAfter"` is a cursor token. The canonical writer always gives the direction and writes `:first` only when it differs from the default.

## Limits

Sixteen keys; windows of 10,000; cursors of 64 Ki characters. The collector's memory is proportional to `Skip + Take`.
