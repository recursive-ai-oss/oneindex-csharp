# Multi-vector search: MaxSim

Late interaction (ColBERT; Khattab and Zaharia, SIGIR 2020) represents a text as one vector per token and scores a query against a document by **MaxSim**: for each query token take the best similarity among the document's tokens, then sum (section 3.5, equation 3). OneIndex defines the interface and the scoring; the application supplies the model.

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

## Not done

The token vectors are stored with the document, not indexed: a MaxSim query is a rerank or a bounded scan, not an index lookup, and there is no approximate multi-vector index.

## Checks

`MaxSimTests.MatchesBruteForceOnGeneratedData`: 200 seeded trials (dimension 1 to 12, 1 to 6 query tokens, 1 to 8 documents of 0 to 8 tokens each; more than 500 query/document scores), each equal to a brute-force sum of maxima of cosines to 4 decimals, and each document's pack/unpack round trip equal to the input. Damaged packed bytes (truncated, wrong count) raise `FormatException`, and the fuzz target `static` feeds arbitrary bytes to `Unpack`.

`MultiVectorFieldTests`: a generated-mapping document type with a `[MultiVector(Dimensions = 5)]` property; 150 documents (some with no tokens, some with none stored), 20 seeded queries of 1 to 4 tokens; `MaxSim.Search` over a filter and `MaxSim.Rerank` over a 60-hit first stage each equal the brute-force sum of cosine maxima to 4 decimals, and the property round-trips; a wrong row length is refused at `Upsert`.
