# The VectorData adapter

`RecursiveAI.OneIndex.VectorData` is a vector-store collection over OneIndex for `Microsoft.Extensions.VectorData` (version 10.11.0 of the abstractions, the one dependency). It covers what the abstraction defines: collections, upsert, get, delete, vector search with filters, and keyword plus vector hybrid search.

```csharp
var store = new OneIndexVectorStore("data/vectors");          // a directory; new OneIndexVectorStore() keeps collections in memory
var hotels = store.GetCollection<string, Hotel>("hotels");
await hotels.EnsureCollectionExistsAsync();
await hotels.UpsertAsync(hotel);                                // every write is committed before it returns
await foreach (var r in hotels.SearchAsync(queryVector, top: 5, new() { Filter = h => h.Rating >= 4 }))
    Console.WriteLine($"{r.Record.Name} {r.Score}");
```

## What is supported

- Keys `string`, `Guid`, `int`, `long`. A `Guid` key is generated when the record carries `Guid.Empty` and the key is auto-generated (the default for `Guid`). Asking for a collection with a key type other than the record's key property throws `InvalidOperationException`.
- Data properties: strings (full-text indexed when `IsFullTextIndexed`), integers, floating point numbers, `bool`, `DateTime`, `DateTimeOffset`, string collections. `IsIndexed` makes a property filterable.
- Vector properties `ReadOnlyMemory<float>` or `float[]`, with the distances cosine similarity and distance, dot product, negative dot product, and Euclidean (squared) distance. Scores come back in the unit of the chosen distance function. The vector index is the engine's: flat below the segment threshold, HNSW above.
- Filters are the abstraction's LINQ predicates; they are applied while the neighbours are chosen, so a filtered search returns a full page when enough records pass. `Skip`, `ScoreThreshold`, `IncludeVectors` work.
- Hybrid search (`IKeywordHybridSearchable`): the keywords search one full-text property (the one named by `AdditionalProperty`, or the only one; none or several without a choice throws), fused with the vector by reciprocal rank fusion ([hybrid.md](hybrid.md)).
- Records are described by the abstraction's attributes or by a `VectorStoreCollectionDefinition`.

## What is not

- **Dynamic collections** (`GetDynamicCollection`, dictionary records) and **embedding generation** by the store: `NotSupported`. Pass vectors yourself.
- Distance functions Manhattan and Hamming.
- Filter shapes the LINQ translator refuses by design ([linq.md](linq.md)): comparing with `null` (`r.Name == null`), and `Any(x => values.Contains(x))` over a collection property.
- A caveat on `Guid` keys: the abstraction's key-type test also exercises a dynamic collection, which is not supported, so that one test is excluded.

## Reflection, trimming and AOT

The adapter reads and writes user record types through reflection, as the abstraction itself does. `OneIndexVectorStore.GetCollection` and `OneIndexRecords<TKey, TRecord>` carry `RequiresUnreferencedCode` and `RequiresDynamicCode`, so a trimmed or NativeAOT application gets warnings at the call, not silent breakage. The package is therefore **not** marked `IsAotCompatible` or `IsTrimmable`. The core package stays reflection-free; an application that needs AOT uses `SearchIndex<T>` with the generated mapping directly.

## Conformance

The tests run the abstraction's own conformance suite (`Microsoft.Extensions.VectorData.ConformanceTests` 10.11.0) against a store over a temporary directory, plus 7 contract tests of our own.

| | Tests |
|---|---:|
| Contract tests (`ContractTests`) | 7 passed |
| Conformance suite, test classes wired: collection management, distance functions, index kinds, filters (typed), basic, no-data, no-vector and multi-vector models, key types, hybrid search | 125 ran: 112 passed, 13 excluded |
| Excluded | 13 (listed in `tests/OneIndex.VectorData.Tests/known-unsupported.txt`): Manhattan and Hamming distance (2); the `Guid` key test, which also needs a dynamic collection (1); filters comparing with `null` (5) and `Contains` / `Any` over a collection property (5) |

Not wired: the dynamic-model, embedding-generation and embedding-type suites (the adapter has neither dynamic collections nor generated embeddings), and the data-type and dependency-injection suites (not run yet). `tests/OneIndex.VectorData.Tests/run.sh <dll>` runs everything except the excluded tests.
