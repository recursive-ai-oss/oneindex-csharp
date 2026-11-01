# Dynamic JSON fields

A `[Dynamic]` property holds a JSON value whose shape is not known in advance. The whole value is stored with the document and comes back unchanged. Its leaf values are also indexed by path, so documents can be filtered by what is inside the JSON without declaring each key.

```csharp
public class Ticket
{
    [SearchId] public string Id { get; set; } = "";
    [Dynamic]  public JsonElement Fields { get; set; }
}

index.Search().Where(t => t.Fields.Path("customer.age") >= 18 && t.Fields.Path("tags") == "red");
```

At the low level the field is `FieldType.Json` (`SchemaBuilder.Json(name)`), the value `FieldValue.FromJson`. A JSON field is indexed by default; with `Indexed = false` it is only stored (and can be switched on later, which rebuilds the index from stored source: schema-evolution.md).

## Paths

A path is the object keys from the root down, joined by dots: `customer.address.city`. Arrays take no part in the path: the elements of an array count as several values at the same path, at any depth, so `{"tags":["a","b"]}` has two values at `tags` and `{"items":[{"sku":"x"},{"sku":"y"}]}` has two at `items.sku`. A dot inside a key is written `\.` and a backslash `\\`. An empty key and an empty string are not indexed (the keyword index holds no empty term), and neither is a scalar (or an array of scalars) at the root, which has no path; it stays in the stored source. The logical name of a path is the property name, a dot and the path: `Fields.customer.age`. That is the name the filter-string grammar and query JSON use; LINQ takes the property and the path separately (`Fields.Path("customer.age")`).

## What is indexed

Each leaf becomes a value of a derived field, named after its path and a letter for the kind:

| JSON leaf | Derived field | Notes |
|---|---|---|
| string | `path#k` keyword | exact match, prefix, fuzzy, regex, phrase of one term, ordering by UTF-8 bytes. Not analyzed: use a declared `[FullText]` property for text search. Strings longer than 1,024 bytes are not indexed. |
| string that reads as a date | also `path#d` date | formats `yyyy-MM-dd`, `yyyy-MM-ddTHH:mm[:ss[.fffffff]]` with an optional zone; no zone means UTC |
| number | `path#n` fractional | every number is a 64-bit float, so integers above 2^53 lose precision in the index (the stored JSON keeps them). Numbers that are not finite are not indexed. |
| `true`, `false` | `path#b` bool | |
| `null`, empty arrays and objects | nothing | a null is a missing value |

Mixed kinds at one path are fine: a path that holds numbers in some documents and strings in others has a `#n` and a `#k` field, and a query reaches the one that matches the kind of its value. Every derived field is multi-valued and kept in a column (so facets and the filters work), is never stored on its own, and holds each distinct value of a document once.

The derived fields are appended to the schema as paths first appear in a flushed segment, ordered by name after the declared fields so that the schema depends on which paths exist, not on the order they arrived in. A segment written before a path appeared has no values for it. Merges handle that like any other absent field.

## Queries

A comparison on a path matches a document when **any** value at the path of the kind compared satisfies it:

- `== "x"` any string equals x; `== 5` any number equals 5; `== true` any boolean is true; `== date` any string that reads as a date is that instant.
- `>`, `>=`, `<`, `<=` the same for numbers, strings (byte order) and dates. Booleans cannot be ordered.
- `!=` and `NOT` are the complement of `==`: they also match documents without the path or with only other kinds of value there.
- `Exists()` (`EXISTS`, `exists`) the path holds a string, number or boolean.
- A path nothing has ever produced matches nothing.

`DynamicValue` evaluates the same comparisons on a `JsonElement` directly, so a predicate can be checked against plain objects. The differential test does exactly that: generated documents with mixed kinds per path, arrays and missing paths, every predicate run both ways.

Filter strings and query JSON choose the kind from the literal (filter-strings.md, query-json.md); the strict query syntax does not reach paths.

## Limits

One limits object (`IndexLimits`) bounds what hostile JSON can do. Leaves over a limit are not indexed; they stay in the stored value and nothing fails.

| Limit | Default | Meaning |
|---|---|---|
| `MaxDynamicDepth` | 16 | nesting of objects and arrays down to a leaf |
| `MaxDynamicPaths` | 1,000 | derived fields per JSON field in the index; when full, further paths are not indexed. Candidates in one flush are admitted in name order |
| `MaxDynamicPathBytes` | 256 | length of a path |
| `MaxDynamicValues` | 10,000 | leaf values indexed per document per JSON field |
| `MaxDynamicKeywordBytes` | 1,024 | length of a string indexed as a keyword |
| `MaxFields` | 4,096 | the schema as a whole, derived fields included |

When the path limit is reached while documents are written in a different order (a threaded rebuild against a cooperative one), which paths made it in can differ; below the limit the result never depends on order.

## Derived fields in the schema

The serialized schema lists the derived fields in a section of its own (commit-format.md). Opening an index with the declared schema compares the declared fields only: the derived fields of the committed schema whose JSON field is still declared and indexed are kept, the others are dropped. Switching `Indexed` off on a JSON field removes its derived fields from the schema; the stored value remains.
