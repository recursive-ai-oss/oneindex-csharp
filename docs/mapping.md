# Mapping documents

A document type is an ordinary class whose properties carry attributes. A source generator (it runs at build time inside the `RecursiveAI.OneIndex` package) writes, for each such class, a `SearchMapping<T>`: the schema, and the conversion between the class and an untyped `Document`. Nothing is found by reflection at run time, so mapping is NativeAOT- and trim-safe. The generated mapping registers itself from a module initializer of the assembly that defines the type.

```csharp
public class Invoice {
    [SearchId] public string Id { get; set; }
    [FullText(Boost = 2)] public string Title { get; set; }
    [FullText(Language = "en")] public string Body { get; set; }
    [Filterable, Sortable] public DateTime Modified { get; set; }
    [Keyword, Facet] public string[] Tags { get; set; }
}
```

## Attributes

| Attribute | On | Effect |
|---|---|---|
| `[SearchId]` | one `string` | The document's identifier: a keyword field, unique. Upsert replaces the document with the same id; `Delete(id)` removes it. Required to open an index. |
| `[FullText(Boost, Language, Analyzer)]` | `string`, `string[]`, `List<string>`, `IEnumerable<string>`, ... | Analyzed text searched by user text and scored with BM25F. `Boost` (default 1, finite, above 0) is the field's weight. `Language` (`"en"`, ...) picks the language analyzer; `Analyzer` names a registered analyzer (`"cjk"`, `"en-both"`) and wins over `Language`. Default analyzer: `standard`. |
| `[Keyword]` | the same types | The whole value is matched exactly (tags, codes). Filterable with `==` and `Contains`. A collection holds several values. |
| `[Filterable]` | numbers, dates, `bool`, strings | Queries can filter on it. On a string it implies `[Keyword]`. |
| `[Sortable]` | single-valued members | Kept column-wise for sorting (sorting itself arrives later). |
| `[Facet]` | keyword and value members | Kept for counts of values (facets arrive later); implies filterable. |
| `[Dynamic]` | a `System.Text.Json.JsonElement` (or `JsonElement?`) | A JSON value whose leaf paths are indexed on the fly and can be filtered by path; the whole value is stored and comes back unchanged. See dynamic.md. |
| `[Vector]` | | Declared, not supported yet: a compile-time error. |

Supported member types: `string`; `string[]`, `List<string>`, `IEnumerable<string>`, `IReadOnlyList<string>`, `IReadOnlyCollection<string>`, `IList<string>`, `ICollection<string>`; `sbyte`, `byte`, `short`, `ushort`, `int`, `uint`, `long`; `float`, `double`; `bool`; `DateTime`, `DateTimeOffset`; and the nullable forms of the value types (a null is simply absent).

A `JsonElement` property is only mapped when it is marked `[Dynamic]`; `[Dynamic]` on any other type is OI0001.

Public read/write properties of supported types **without** attributes are stored and returned with the document but cannot be searched or filtered. Read-only properties and properties of other types are not part of the document and come back with their constructor defaults.

The type must be a non-generic, non-abstract class, reachable from its assembly (not private), with a public or internal parameterless constructor; the attributed properties need a getter and a plain `set` (not `init`, not `required`).

## Diagnostics

| Id | Meaning |
|---|---|
| OI0001 | An attributed member has a type the attribute cannot map. |
| OI0002 | `[Vector]`: declared but not supported yet. |
| OI0003 | Conflicting attributes (`[FullText]` with `[Keyword]`, or with a filtering attribute; `[Sortable]` on a collection; `[Dynamic]` with any other search attribute). |
| OI0004 | A bad identifier: several `[SearchId]`, or one that is not a plain string. |
| OI0005 | The type cannot be mapped (generic, abstract, private, no usable constructor). |
| OI0006 | A member cannot be mapped (a field, no getter or setter, `init`, `required`). |
| OI0007 | A bad attribute value (`Boost` not finite or not above 0). |

Checked when an index is opened instead, because the generator cannot know them: a type without `[SearchId]` (`InvalidOperationException`), and a `Language` or `Analyzer` that no analyzer is registered under.

## Fields and the schema

A field is named like its property. `[FullText]` strings are text fields with positions; keywords and the identifier are keyword fields (column-wise); numbers, dates and booleans are indexed as ordered values when filterable. Opening an existing directory with a type whose schema differs from the one in the index fails: changing a type needs schema evolution, which is not built yet.

## The index

`SearchIndex.Open<T>(path)` and `SearchIndex.InMemory<T>()` take no options: everything comes from the attributes. `Upsert`, `Delete` and `Commit` (each with an async twin) go to the single writer; **searches see the last commit only**. A commit is atomic and durable (see [commit-format.md](commit-format.md)). Disposing discards uncommitted changes.

The index is thread-safe. A search in flight keeps the commit it started on, even if a newer commit is published meanwhile.

**Stored source is not built yet.** Hits return the document from a copy the index keeps in memory, so a hit's `Document` is available for documents added in the current session; searching a directory index reopened later works, but reading its hits throws `InvalidOperationException` until stored source lands.

## Highlights

`hit.Highlights` maps a property name to fragments of its stored text, computed on first use. A `Fragment` has the plain `Text`, its `Offset` in the field value and the `Matches` (offset and length inside the fragment); `Marked(open, close)` wraps matches (default `<em>`/`</em>`, no escaping).

- Fields without a match are absent. For a multi-valued field, values are searched in order.
- Fragments are cut around the first match not yet shown: about `FragmentLength / 4` characters before it, about `FragmentLength` long in total (default **160**), widened to whole words, never inside a surrogate pair. The first match is always inside its fragment, matches that start inside it are kept whole, and the next fragment starts at the next match beyond. At most `MaxFragments` (default **3**) per field. `Search(...).Highlight(new HighlightOptions { ... })` changes both.
- Matching re-analyzes the stored text with the field's analyzer (so stems and case folding match, and CJK bigrams are located) and uses the original offsets, so marks are on the text as written. Excluded operands, range filters and keyword filters mark nothing.
