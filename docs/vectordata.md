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
- **Data-type suite.** Each of its tests also creates a dynamic collection, so all 16 are excluded; the typed path (`Guid`, `DateOnly`, `TimeOnly`, nullable values, string collections) is covered by `ContractTests`. Filters comparing a whole array (`r.StringArray == array`) are not translatable.
- **Registration extensions.** The package ships no `AddOneIndex...` dependency-injection extension (it would add a dependency beyond the abstraction); applications register `OneIndexVectorStore` and their collections as services, as the tests do. Embedding generators are not resolved from the container (`EmbeddingGeneratorIsResolved` is excluded).
- A caveat on `Guid` keys: the abstraction's key-type test also exercises a dynamic collection, which is not supported, so that one test is excluded.

## Reflection, trimming and AOT

The adapter reads and writes user record types through reflection, as the abstraction itself does. `OneIndexVectorStore.GetCollection` and `OneIndexRecords<TKey, TRecord>` carry `RequiresUnreferencedCode` and `RequiresDynamicCode`, so a trimmed or NativeAOT application gets warnings at the call, not silent breakage. The package is therefore **not** marked `IsAotCompatible` or `IsTrimmable`. The core package stays reflection-free; an application that needs AOT uses `SearchIndex<T>` with the generated mapping directly.

## Conformance

The tests run the abstraction's own conformance suite (`Microsoft.Extensions.VectorData.ConformanceTests` 10.11.0) against a store over a temporary directory, plus 8 contract tests of our own.

| | Tests |
|---|---:|
| Contract tests, wired conformance suites, dependency-injection suite (`dotnet test`) | 168 passed, 0 failed, the unsupported cases below reported as skipped |
| Skipped with a reason (`tests/OneIndex.VectorData.Tests/Unsupported.cs`) | Manhattan and Hamming distance (2); the `Guid` key test, which also needs a dynamic collection (1); `EmbeddingGeneratorIsResolved` in 9 variants (lifetime and service key); the 16 data-type tests, which need a dynamic collection |

Filters comparing with `null` and `Contains` / `Any` over a collection property, excluded before, now pass (10 tests). Wired suites: collection management, distance functions, index kinds, filters (typed), basic, no-data, no-vector and multi-vector models, key types, hybrid search, dependency injection, data types (all excluded). Not wired: dynamic-model, embedding-generation and embedding-type suites (no dynamic collections, no generated embeddings). A plain `dotnet test` runs them all; the unsupported cases skip themselves, each naming the feature.
