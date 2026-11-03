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

## Not done

A multi-vector is stored as a packed `Bytes` field, in the stored source; there is no dedicated schema field type or attribute, so the engine does not index token vectors and a MaxSim query is a rerank or a bounded scan, not an index lookup.

## Checks

`MaxSimTests.MatchesBruteForceOnGeneratedData`: 200 seeded trials (dimension 1 to 12, 1 to 6 query tokens, 1 to 8 documents of 0 to 8 tokens each; more than 500 query/document scores), each equal to a brute-force sum of maxima of cosines to 4 decimals, and each document's pack/unpack round trip equal to the input. Damaged packed bytes (truncated, wrong count) raise `FormatException`, and the fuzz target `static` feeds arbitrary bytes to `Unpack`.
