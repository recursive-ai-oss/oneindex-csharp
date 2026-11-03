# Automatic embedding and the embedding cache

This note is normative for `[Vector(From = ...)]`, the embedder an index uses, and the cache that keeps unchanged text from being embedded again.

## Declaring it

```csharp
public class Article {
    [SearchId] public string Id { get; set; }
    [FullText] public string Body { get; set; }
    [Vector(From = nameof(Body), Dimensions = 384)] public float[]? Embedding { get; set; }
}
```

`From` names a text or keyword member of the same type (a build-time check in the schema: otherwise `InvalidOperationException` naming the member). `Dimensions` may be left out when `From` is set: the index then takes the embedder's `Dimension` when it is opened or created, and opening it without an embedder throws `InvalidOperationException` naming the member (`SearchTool.For<T>()` needs no embedder). When `Dimensions` is declared, an embedder whose `Dimension` differs is refused when the index opens (`InvalidOperationException` naming both). Either way the length is recorded in the index's schema, so an index made with one length refuses to open with another. A non-nullable `float[]` property needs an initial value; an empty array means no vector was supplied. A multi-valued source is joined with a line feed.

## Which embedder

There is no options object on `SearchIndex.Open<T>`. The embedder comes from, in this order:

1. the argument: `SearchIndex.Open<T>(path, embedder)` and `SearchIndex.InMemory<T>(embedder)` (`null` = none);
2. `SearchEmbedders.Default`, set once at startup by the application, or by `BgeSmallEmbedder.UseAsDefault()` in `RecursiveAI.OneIndex.Models`, which loads the packed model and sets it.

Nothing registers itself: no reflection, no module initializer, so referencing `.Models` alone changes nothing and the behaviour is the same under NativeAOT and trimming. `SearchIndex<T>.Embedder` returns the one in use. An index without an embedder accepts documents without vectors and searches stay text-only; a vector the application sets on the document is always kept and never overwritten.

## When text is embedded

* **Upsert** (`Upsert`, `Upsert(IEnumerable)`, `UpsertAsync`): before the document reaches the writer, with the document role. `Upsert(IEnumerable)` embeds in batches of at most 32 distinct texts (the documents are taken 64 at a time), `UpsertAsync` uses `IEmbedder.EmbedAsync`. Nothing is embedded at commit, flush or merge.
* **Search**: the search text is embedded with the *query* role when a search is hybrid (`hybrid.md`), and cached in memory (the last 256 texts), so paging or repeating a search embeds once.

**Failure.** If the embedder throws, returns a NaN or an infinity, or is cancelled, `Upsert` throws (`EmbeddingException` wrapping the cause and naming the model; `OperationCanceledException` for cancellation) **before** anything of the call is written: a batch call writes none of its documents' batch in that group of 64, and no half-embedded document ever exists, because the document is complete before the writer sees it. Documents of earlier groups in the same call stay buffered, as they would after any other exception. The index stays usable.

## The cache

A cache entry is the SHA-256 of the model identity (`IEmbedder.ModelId`), a zero byte, and the UTF-8 source text. A document stores its entry in a hidden bytes field `<Member>#embedding` of the stored source, next to the vector (the stored source keeps the original, un-normalised vector). Writing it into the schema makes it part of the type's field list; an index created before the member had `From` gains the field through the normal additive schema evolution, and its documents count as stale until re-embedded.

An upsert looks the text up in this order and embeds only on a miss:

1. an in-memory map of recent entries (up to 8,192 vectors; cleared when full), which serves identical texts of different documents and documents not yet committed;
2. the stored source of the committed document with the same identifier: same entry, so the stored vector is reused.

Hence: re-upserting the same documents (a reindex from a database), reopening the index, merges, and schema-evolution rebuilds embed nothing. Merges and rebuilds move stored documents, vectors included, and never reference the embedder. Changing the text changes the entry, so only that document is embedded. Changing the model identity changes every entry:

* new upserts embed again under the new identity;
* `SearchIndex<T>.ReembedStale()` (and `ReembedStaleAsync`) embeds again every committed document whose entry is missing or differs, buffering the replacements for the next commit, and returns how many; unchanged documents cost nothing. Call it after switching the embedder.

A model change is not a schema difference, so it does not start the background schema rebuild by itself; `ReembedStale` is the explicit step. A model identity must change whenever its output changes (`IEmbedder.ModelId` documents this), and it also covers a changed query instruction or quantization.

## Durability

Embedding happens before the write, so the commit protocol is unchanged: a document is either in a commit with its vector and entry or not in it. The kill campaign `tests/Durability` (`embed-kill`) runs a writer child that upserts through the index with a slow deterministic embedder, single and batched, commits every few operations and is killed at random moments; after each kill the directory must reopen, hold exactly the last acknowledged commit or the one in flight, every document must carry the vector its text determines, and a vector search for it must find it.

## Limits

One vector field per `From` member; the source member must fit the model's input (the model cuts, it does not refuse). Texts are embedded in the caller's thread; a slow embedder slows `Upsert`.

## Sparse and multi-vector members

`[Sparse(From = ...)]` (docs/sparse.md) and `[MultiVector(From = ...)]` (docs/multi-vector.md) use the same scheme with their own encoders (`ISparseEncoder`, `IMultiVectorEncoder`; defaults `SearchSparseEncoders.Default` and `SearchMultiVectorEncoders.Default`) and their own hidden cache fields (`<Member>#sparse`, `<Member>#multivector`). `ReembedStale` handles all three kinds in one pass.
