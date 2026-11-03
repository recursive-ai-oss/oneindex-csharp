# Documentation

Normative, language-neutral notes on what OneIndex does and how its files look. Start with [architecture.md](architecture.md); the repository README covers installation and a first search.

## Start here

| Page | What it covers |
|---|---|
| [architecture.md](architecture.md) | The layers, their namespaces and what each builds on. |
| [mapping.md](mapping.md) | Attributed document types and the source-generated mapping. |
| [reads-like.md](reads-like.md) | Each layer next to the .NET API it is shaped after: EF Core, `Utf8JsonReader`, `TensorPrimitives`. |
| [migrating-from-lucene-net.md](migrating-from-lucene-net.md) | Concept and call mapping from Lucene.NET 4.8, analysis equivalences, a worked example. |
| [cli.md](cli.md) | The `oneindex` tool: index a folder, query with citations, build a static index. |

## Storage, files and durability

| Page | What it covers |
|---|---|
| [storage.md](storage.md) | The `IStorage` range-read interface and its backends, the fault injector and the block-transform slot. |
| [commit-format.md](commit-format.md) | Index file frames, the commit file and the commit protocol. |
| [segments.md](segments.md) | Segments, deletes, snapshots and merges. |
| [durability.md](durability.md) | What survives kills, failed calls and power cuts, under which conditions, and how each promise is tested. |
| [processes.md](processes.md) | One writer, many reader processes; per-OS behaviour. |
| [schema-evolution.md](schema-evolution.md) | Opening an index with a changed schema; background rebuilds and the swap. |

## Text index

| Page | What it covers |
|---|---|
| [analysis.md](analysis.md) | Tokens, analyzers, the registry, Unicode and Snowball versions, folding. |
| [analysis-asian.md](analysis-asian.md) | Japanese, Chinese, Korean, Thai, Lao, Khmer and Myanmar segmentation. |
| [decompounding.md](decompounding.md) | Splitting compounds in German, Dutch, the Scandinavian languages, Finnish and Icelandic. |
| [term-dictionary.md](term-dictionary.md) | The term dictionary format and term lookup, prefix, fuzzy and regex. |
| [postings.md](postings.md) | Postings, field lengths and block-max WAND. |
| [columns.md](columns.md) | Per-document values for filtering, sorting, faceting and aggregation. |
| [points.md](points.md) | The point index for numeric, date and geo ranges. |
| [stored-source.md](stored-source.md) | The compressed stored document and its LZ format. |
| [dynamic.md](dynamic.md) | `[Dynamic]` JSON fields indexed by path. |

## Queries and ranking

| Page | What it covers |
|---|---|
| [queries.md](queries.md) | Semantics and limits of every query object. |
| [scoring.md](scoring.md) | BM25F, proximity and the explain format. |
| [search-contracts.md](search-contracts.md) | The shapes the engine is built against: queries, collectors, scorers. |
| [ranking-rules.md](ranking-rules.md) | Optional bucket-sort ranking rules. |
| [function-scores.md](function-scores.md) | Changing scores with column values: factors, decay, weights. |
| [typo-tolerance.md](typo-tolerance.md) | Typo budgets and as-you-type. |
| [synonyms.md](synonyms.md) | Query-side synonyms and stop words. |
| [did-you-mean.md](did-you-mean.md) | Corrected-query suggestions. |
| [sorting.md](sorting.md) | Sorting by columns; `Skip` and search-after paging. |
| [facets.md](facets.md) | Facets, facet value search and distinct. |
| [aggregations.md](aggregations.md) | Terms, range, histogram, date histogram, stats, cardinality and percentiles. |
| [geo.md](geo.md) | Radius and bounding-box filters, distance sort. |

## Query surfaces

| Page | What it covers |
|---|---|
| [user-text.md](user-text.md) | Forgiving user text, which never throws. |
| [query-syntax.md](query-syntax.md) | The opt-in strict syntax with positioned errors. |
| [filter-strings.md](filter-strings.md) | The filter-string grammar. |
| [linq.md](linq.md) | LINQ predicates translated to query objects. |
| [query-json.md](query-json.md) | Versioned query JSON and its schema. |
| [search-tool.md](search-tool.md) | `SearchTool.For<T>()`: a search as an LLM tool. |

## Vectors and models

| Page | What it covers |
|---|---|
| [vectors.md](vectors.md) | Distance kernels, HNSW, exact search, quantized codecs, filtered search, merge. |
| [sparse.md](sparse.md) | Learned sparse vectors on the postings. |
| [multi-vector.md](multi-vector.md) | MaxSim over token vectors. |
| [ivf.md](ivf.md) | The clustered IVF index. |
| [hybrid.md](hybrid.md) | Fusing keyword and vector lists. |
| [rerank.md](rerank.md) | The `IReranker` stage and the default cross-encoder. |
| [auto-embedding.md](auto-embedding.md) | `[Vector(From = ...)]` and the embedding cache. |
| [models.md](models.md) | `RecursiveAI.OneIndex.Models`: tokenizer, embedder contract, default models. |

## Packages and platforms

| Page | What it covers |
|---|---|
| [entity-framework.md](entity-framework.md) | The EF Core integration: outbox log, applier, search composition. |
| [files.md](files.md) | Folder indexing, passages and citations. |
| [vectordata.md](vectordata.md) | The `Microsoft.Extensions.VectorData` adapter. |
| [browser.md](browser.md) | Running in the browser: memory, OPFS, HTTP. |
| [static-index.md](static-index.md) | The chunked static index served from a file server. |

## Measurement

| Page | What it covers |
|---|---|
| [relevance.md](relevance.md) | BEIR method, baselines and results. |
| [benchmarks.md](benchmarks.md) | How speed is measured against the comparators. |

The public API of each package is listed under [`api/`](../api) (one text file per package), generated by `tools/ApiListing`.
