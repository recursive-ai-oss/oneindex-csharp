# Multi-vector search: MaxSim

Late interaction (ColBERT; Khattab and Zaharia, SIGIR 2020) represents a text as one vector per token and scores a query against a document by **MaxSim**: for each query token take the best similarity among the document's tokens, then sum (section 3.5, equation 3). OneIndex defines the interface and the scoring; the application supplies the model (no multi-vector model is packed with OneIndex).

```csharp
public interface IMultiVectorEncoder { string ModelId { get; } int Dimension { get; } float[][] Encode(string text, EmbeddingRole role, CancellationToken ct = default); }

doc.Tokens = MultiVector.Pack(encoder.Encode(text, EmbeddingRole.Document));        // a Bytes property of the document type
var first = index.Search("late interaction").Take(100).Execute();                      // any first stage
var best = MaxSim.Rerank(first, d => d.Tokens, encoder.Encode(query, EmbeddingRole.Query), take: 10);
var scan = MaxSim.Search(index.Search().Where(d => d.Lang == "en"), d => d.Tokens, queryTokens, k: 10);   // MaxSim alone
```

- `MultiVector.Pack` / `Unpack`: a little-endian `u32` token count, a `u32` dimension, then the vectors as little-endian floats. Unpack validates the lengths and raises `FormatException`. Packing refuses unequal lengths and NaN or infinity.
- `MaxSim.Score(query, document, dimension)`: the vectors are scaled to length 1 first, so the similarity is cosine; either side with no tokens scores 0.
- `MaxSim.Rerank` re-orders first-stage hits (the rerank stage); documents without a multi-vector score 0 and sort last, ties keep the first-stage order.
- `MaxSim.Search` is an exact scan of the documents that pass the optional filter, limited to `SearchQuery<T>.MaxResultWindow` (10,000) candidates. For larger collections narrow with a filter, or rerank a first stage. There is no approximate multi-vector index.

## The `[MultiVector]` field

```csharp
public class Passage
{
    [SearchId] public string Id { get; set; } = "";
    [FullText] public string Text { get; set; } = "";
    [Keyword] public string Lang { get; set; } = "";
    [MultiVector(Dimensions = 128)] public float[][]? Tokens { get; set; }     // one row per token
}

var first = index.Search("late interaction").Take(100).Execute();
var best = MaxSim.Rerank(first, p => p.Tokens, encoder.Encode(query, EmbeddingRole.Query), take: 10);
var scan = MaxSim.Search(index.Search().Where(p => p.Lang == "en"), p => p.Tokens, queryTokens, k: 10);
```

The generated mapping packs the rows with `MultiVector.Pack(rows, Dimensions)` into a stored `Bytes` field and unpacks them on read (AOT-clean, no reflection). A row of the wrong length, a NaN or an infinity is refused at `Upsert` with an `ArgumentException`. `MaxSim.Rerank` and `MaxSim.Search` have overloads taking `Func<T, float[][]?>` beside the packed-bytes ones. A multi-vector cannot also be `[FullText]`, `[Keyword]`, `[Filterable]`, `[Sortable]`, `[Facet]`, `[Geo]`, `[SearchId]`, `[Vector]` or `[Sparse]` (a compile-time diagnostic).

## Encoding at upsert: `[MultiVector(From = ...)]`

```csharp
public class Passage
{
    [SearchId] public string Id { get; set; } = "";
    [FullText] public string Text { get; set; } = "";
    [MultiVector(From = nameof(Text), Dimensions = 128)] public float[][]? Tokens { get; set; }
}

using var index = SearchIndex.Open<Passage>("data/passages", embedder: null, sparseEncoder: null, reranker: null, multiVectorEncoder: encoder);
index.Upsert(new Passage { Id = "p1", Text = "..." });                    // Tokens is encoded from Text
var best = index.Search("late interaction").MaxSim(window: 100).Take(10).Execute();   // the query is encoded too
```

The encoder is an `IMultiVectorEncoder`, supplied as for the dense embedder: an argument of `SearchIndex.Open<T>` / `InMemory<T>` (the five-argument `Open` and four-argument `InMemory` use exactly what they are given, `null` = none) or `SearchMultiVectorEncoders.Default`, set once by the application. Nothing registers itself. `SearchIndex<T>.MultiVectorEncoder` returns the one in use.

* **Declaring.** `From` names a text or keyword member of the same type. `Dimensions` is required and must equal the encoder's `Dimension`; a mismatch is refused when the index opens (`InvalidOperationException` naming both). The generator refuses (compile error `OI0007`) an empty `From`, one that names the member itself and one that names no member of the type. Whether the named member is a text or keyword field is checked when the mapping is built, as for `[Vector]`.
* **When.** `Upsert`, `Upsert(IEnumerable)` and the async twins encode a document whose `[MultiVector]` member is null, before it reaches the writer, in batches of at most 32 distinct texts with the document role (`IMultiVectorEncoder.Encode(IReadOnlyList<string>, ...)`, whose default calls the single-text method). Token vectors the application sets are kept and never overwritten. An encoder that throws, returns null, a row of the wrong length or a NaN or infinity makes the call throw `EmbeddingException` before anything is written.
* **The cache** is the one of dense embedding (docs/auto-embedding.md): the SHA-256 of the model identity, a zero byte and the UTF-8 text, in a hidden bytes field `<Member>#multivector` of the stored source next to the packed token vectors. An unchanged text under an unchanged `ModelId` is never encoded again: re-upserting, reopening, merges and schema rebuilds reuse the stored vectors. `SearchIndex<T>.ReembedStale()` (it covers dense, sparse and multi-vector members) encodes again every committed document whose entry is missing or differs, buffering the replacements for the next commit. A document whose tokens were supplied by the application has no entry and counts as stale. An index written before `From` was added opens unchanged; the hidden field arrives through additive schema evolution and no stored format changes.
* **The query stage.** `SearchQuery<T>.MaxSim(window = 100, encoder = null, query = null)` and `MaxSim(x => x.Tokens, ...)` (for types with several `[MultiVector]` members) re-order the best `window` hits of the search by MaxSim against the query encoded with the query role (cached for the last 256 texts). It sits beside `Rerank`: hits carry the MaxSim score as `Score` and the earlier one as `FirstStageScore`; documents without token vectors score 0 and sort last; ties keep the first-stage order; `Explain` adds a line; facets and aggregations still count all matches. With no search text the first stage is every document passing the filters, so `index.Search().Where(...).MaxSim(window: 1000, query: "text")` is an exact scan limited to `window` candidates (at most 10,000). It cannot be combined with `Rerank`, `OrderBy`, `SearchAfter` or `OrderByDistance` (`NotSupportedException`), has no query JSON or LINQ-keyword form, and needs an encoder (`InvalidOperationException` otherwise). `MaxSim.Rerank` and `MaxSim.Search` above stay for vectors you encoded yourself.

## Not done

The token vectors are stored with the document, not indexed: a MaxSim query is a rerank or a bounded scan, not an index lookup, and there is no approximate multi-vector index.

## Checks

`MaxSimTests.MatchesBruteForceOnGeneratedData`: 200 seeded trials (dimension 1 to 12, 1 to 6 query tokens, 1 to 8 documents of 0 to 8 tokens each; more than 500 query/document scores), each equal to a brute-force sum of maxima of cosines to 4 decimals, and each document's pack/unpack round trip equal to the input. Damaged packed bytes (truncated, wrong count) raise `FormatException`, and the fuzz target `static` feeds arbitrary bytes to `Unpack`.

`MultiVectorFieldTests`: a generated-mapping document type with a `[MultiVector(Dimensions = 5)]` property; 150 documents (some with no tokens, some with none stored), 20 seeded queries of 1 to 4 tokens; `MaxSim.Search` over a filter and `MaxSim.Rerank` over a 60-hit first stage each equal the brute-force sum of cosine maxima to 4 decimals, and the property round-trips; a wrong row length is refused at `Upsert`.
