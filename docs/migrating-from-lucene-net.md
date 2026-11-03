# Migrating from Lucene.NET

This page maps the concepts and calls of Lucene.NET 4.8 (the `4.8.0-beta` line on NuGet) to OneIndex. It is written from Lucene.NET's public documentation and from black-box runs of the pinned release in `tools/OneIndex.Harness` (see [relevance.md](relevance.md)); neither engine's source was consulted. There is **no compatibility layer**: OneIndex does not mimic Lucene's API, and it cannot open a Lucene index (see [Existing indexes](#existing-indexes)). The mapping tells you which OneIndex call replaces which Lucene.NET call, and where behaviour differs on purpose.

Two entry points exist, and most code needs only the first:

- **The happy path**: `SearchIndex.Open<T>(path)` over an attributed class. It replaces the writer, reader, searcher, analyzer, query parser and field-by-field document building in one object.
- **The low level**: `IndexWriter`, `IndexReader`, `IndexSnapshot`, `IndexSearcher`, query objects and collectors (namespaces `OneIndex.Index` and `OneIndex.Search`). This is the layer to read when you used Lucene.NET's classes directly.

## Concepts

| Lucene.NET | OneIndex | Notes |
|---|---|---|
| `Directory` (`FSDirectory`, `RAMDirectory`) | `IStorage`: `FileSystemStorage`, `MemoryStorage`, `ReadOnlyStorage`, HTTP and OPFS backends | A directory is a range-read interface with sync and async methods. `SearchIndex.Open<T>("path")` and `SearchIndex.InMemory<T>()` build the storage for you. |
| `IndexWriterConfig`, `IndexWriter` | `IndexWriterOptions`, `IndexWriter` (low level); `SearchIndex<T>` (`Upsert`, `Delete`, `Commit`) | One writer per index, thread-safe. A second writer on the same directory is refused with `IndexLockedException`. |
| `Document`, `Field`, `FieldType` | `Document` and `FieldDefinition` in a `Schema` (low level); attributes on a class (happy path) | Fields are declared once in a schema, not chosen per document. Types: text, keyword, integral, fractional, date, bool, geo point, vector, sparse vector, JSON. |
| `TextField` | `[FullText]` / `FieldType.Text` | Analyzed, scored. |
| `StringField` | `[Keyword]` / `FieldType.Keyword` | Matched whole; filterable; facetable with `[Facet]`. |
| `Int32Field`, `Int64Field`, `DoubleField`, `NumericDocValuesField`, `SortedDocValuesField` | `[Filterable]`, `[Sortable]` on a number, date, bool or string | Indexed (for range filters) and kept as a column (for sort, facets and aggregations) by the same declaration. |
| `StoredField` | stored source | The whole document is stored, compressed, unless the schema opts out. There is no per-field stored flag on the happy path. |
| `IndexReader`, `DirectoryReader.Open`, `OpenIfChanged` | `IndexReader.Open`, `Refresh()`; `IndexSnapshot` | A snapshot is a committed generation, reference-counted; its files are not deleted while it is open. |
| `SearcherManager`, near-real-time reader | `IndexWriter.OpenNearRealTimeReader()` | Opt-in; sees uncommitted changes. |
| `IndexSearcher` | `IndexSearcher` (low level), `SearchIndex<T>.Search(...)` | Create with `SnapshotSearch.CreateSearcher(snapshot)`. |
| `TopDocs`, `ScoreDoc` | `TopDocs`, `ScoreDoc` (low level); `SearchResults<T>`, `SearchHit<T>` (happy path) | `TopDocs.TotalHitsExact` says whether the total is exact; `TotalHits` is a lower bound unless `CountExactly()` was asked for. |
| `Analyzer`, `Tokenizer`, `TokenFilter` | `Analyzer`, `TokenSource` / `Tokenizer`, `TokenFilter`, `CustomAnalyzer` | Registered by name in a static `AnalyzerRegistry` (no discovery by reflection). |
| `Similarity` (`DefaultSimilarity`, `BM25Similarity`) | BM25F (`Bm25Parameters` in `QueryEngineOptions`), always | Lucene.NET 4.8's default similarity is TF-IDF; OneIndex scores BM25F with k1 = 0.9, b = 0.4 by default. See [Scoring](#scoring). |
| `Query` classes | records in `OneIndex.Search` | See the next table. |
| `QueryParser`, `MultiFieldQueryParser` | `Search("text")` (never throws), `SearchStrict(...)` (positioned errors), filter strings, query JSON | See [Queries](#queries). |
| `Sort`, `SortField` | `OrderBy`, `ThenBy`; `FieldSort`, `TopFieldCollector` | See [Sorting](#sorting). |
| `FacetsCollector`, taxonomy and sorted-set facets | `Facets(x => x.Tags)`; `FacetCollector`; aggregations | No separate taxonomy index or facet configuration. |
| `Highlighter`, `QueryScorer`, `SimpleHTMLFormatter` | `hit.Highlights`; `Highlighter`, `HighlightOptions` | Computed from the stored text on first use. |
| `IndexSearcher.Explain` | `Explain()`; `IndexSearcher.Explain` | A tree of `Explanation` values. |
| `GroupingSearch` | `Distinct(x => x.Field)` | Collapse to the best hit per value. |
| `MoreLikeThis` | `MoreLikeThisQuery`, `index.MoreLikeThis(id)` | Selects the most distinctive terms of a text or stored document by tf-idf and searches them ([more-like-this.md](more-like-this.md)). |
| `TieredMergePolicy`, `ConcurrentMergeScheduler` | `TieredMergePolicy`, `ThreadedMergeScheduler` (`CooperativeMergeScheduler` where there are no threads) | Merges are throttled and results are identical either way. |
| `Optimize` / `ForceMerge(n)` | `ForceMerge(n)` | |
| `IndexWriter.Commit` | `Commit()` | Writes and fsyncs every new file, then publishes the commit by an atomic rename. A crash leaves the previous commit complete. |

### Query types

| Lucene.NET | OneIndex |
|---|---|
| `TermQuery` | `TermQuery(field, term)` (the term is already analyzed) |
| `BooleanQuery` + `Occur.MUST` / `SHOULD` / `MUST_NOT` | `BooleanQuery` + `Occur.Must` / `Should` / `MustNot`, and `Occur.Filter` for a clause that selects without scoring |
| `PhraseQuery` (with `Slop`) | `PhraseQuery(field, terms) { Slop = n }` |
| `PrefixQuery` | `PrefixQuery` (constant score by default) |
| `FuzzyQuery` | `FuzzyQuery { MaxEdits = n }` (0 to 2, transposition counts as one edit) |
| `RegexpQuery` | `RegexQuery` |
| `WildcardQuery` | none; use `PrefixQuery` or `RegexQuery` |
| `NumericRangeQuery`, `TermRangeQuery` | `RangeQuery(field, lower, upper)` with `IncludeLower` / `IncludeUpper` |
| `MatchAllDocsQuery` | `MatchAllQuery` |
| `ConstantScoreQuery`, filters | a `Filter` clause in a `BooleanQuery`; `Where(...)` |
| `FunctionQuery`, `CustomScoreQuery` | `FunctionScoreQuery`, `ScoreBy(...)` (field value factor, decay, weights, delegates) |
| `DisjunctionMaxQuery` | `MultiFieldTermQuery` (scores a term across fields as one stream) |
| spatial strategies | `[Geo]`, `GeoRadiusQuery`, `GeoBoundingBoxQuery`, `OrderByDistance` |
| none in 4.8 | `KnnQuery`, `SparseQuery`, `HybridQuery` (vectors, learned sparse, fusion) |
| `SpanQuery` family, block joins, payloads | not in 1.0 (planned later, on evidence) |

### Queries

`QueryParser` parses a syntax and throws `ParseException` on bad input. OneIndex has three tiers instead:

- `index.Search(text)` takes what a person types: words, `"phrases"`, `-excluded`, `OR`, a typed last word as a prefix. It **never throws**; unbalanced quotes and stray operators are read as plain text ([user-text.md](user-text.md)).
- `index.SearchStrict(text)` is the opt-in parser with a field syntax (`title:acme`, `price:[10 TO 20]`, `"a b"~2`, `term~1`) and positioned `QueryParseException` errors ([query-syntax.md](query-syntax.md)). Its grammar is its own and not Lucene's.
- Filters: a LINQ predicate in `Where(x => ...)`, a Meilisearch-style filter string `Tags = invoice AND Price < 100` ([filter-strings.md](filter-strings.md)), or versioned query JSON ([query-json.md](query-json.md)).

A query string is not analyzed by the query object. `TermQuery("title", "headphon")` takes the term as indexed; the happy path and the user-text compiler analyze with the field's analyzer for you. (With Lucene.NET you analyzed the text through the analyzer's token stream or the query parser; do the same with `Analyzer` when you build query objects by hand.)

### Sorting

`new Sort(new SortField("price", SortFieldType.DOUBLE))` becomes `.OrderBy(x => x.Price)` (or `new FieldSort("price")` with a `TopFieldCollector`). Add `.ThenBy(...)`. A missing value goes last unless you pass `MissingValuePlacement.First`. Deep paging uses `SearchAfter(cursor)` with a `SortCursor` from the previous page (`results.NextCursor`) rather than `searcher.SearchAfter(lastDoc, ...)`; `Skip(n)` is limited to a 10,000-hit window.

## Analysis equivalences

| Lucene.NET | OneIndex name | Difference |
|---|---|---|
| `StandardAnalyzer` | `standard` | Same word segmentation family (Unicode word boundaries; OneIndex follows UAX #29 revision 47 with a 255-character token limit). **`StandardAnalyzer` also removes the 33-word English stop list; `standard` removes none.** Configure the Lucene side without its stop filter for a like-for-like comparison, or add a `StopFilter`. |
| `EnglishAnalyzer` | `en` | Both lower-case, drop possessives, remove English stop words and stem. They are not token-identical: Lucene.NET's `EnglishAnalyzer` uses its own 33-word list and the original Porter stemmer; `en` uses Snowball's English stop list and the Snowball English (Porter2) stemmer. Measured black-box on SCIDOCS texts, the token streams differ in 20 of 6,000 sampled texts when both sides use the same stop list (stems of short words), and 5,286 with each side's own list, almost all stop words ([relevance.md](relevance.md)). The effect on nDCG@10 was at most 0.1 point. |
| `SimpleAnalyzer`, `WhitespaceAnalyzer` | `whitespace-lowercase` (and `WhitespaceTokenizer`) | |
| `KeywordAnalyzer` | `keyword` | |
| `CJKAnalyzer` | `cjk` | Bigrams of Han, kana and Hangul runs; NFKC and case folding. |
| `*Analyzer` per language | the language code (`de`, `fr`, `es`, ...; 34 Snowball languages) | `<code>-both` indexes the word and its stem at the same position, so exact matches can rank above stem matches. |
| `ASCIIFoldingFilter`, `ICUFoldingFilter` | `AsciiFoldFilter`, `AccentFoldFilter`, `CaseFoldFilter` (full Unicode case fold), `NormalizationFilter` (NFC/NFKC) | |
| `SynonymFilter` | `[Synonyms]` and `[OneWaySynonyms]` on the document type | Applied at query time with the documented one-way and mutual forms ([synonyms.md](synonyms.md)). |
| `LengthFilter`, `StopFilter`, `LowerCaseFilter`, `PorterStemFilter` | `LengthFilter`, `StopFilter`, `LowerCaseFilter`, `StemFilter` | |
| Japanese, Chinese, Korean analyzers | `ja`, `zh`, `ko` from `RecursiveAI.OneIndex.Analysis.Asian` | Dictionary data packed with notices. |
| `CompoundWordTokenFilterBase` | `<code>-compound` analyzers (da, de, fi, is, nl, no, sv) | |

Custom analyzers: derive nothing; compose `new CustomAnalyzer(...)` from a tokenizer and filters, and register it with `AnalyzerRegistry.Register(name, factory)` so a schema can name it.

## Scoring

- Lucene.NET 4.8's out-of-the-box similarity is classic TF-IDF; `BM25Similarity` is opt-in. OneIndex scores **BM25F** (Robertson and Zaragoza): each field's term frequency is length-normalized, weighted by the field's boost and summed *before* saturation, so a field boost is a weight on evidence, not a multiplier on a score. Scores are therefore not comparable across the two engines, and `Boost` values need re-tuning rather than copying.
- BM25 itself matches: black-box `Explain` output of Lucene.NET's `BM25Similarity` shows the same idf `ln(1 + (N - n + 0.5)/(n + 0.5))` and tf saturation as OneIndex's `Explain`. OneIndex's defaults are k1 = 0.9, b = 0.4 (the BEIR lexical baseline); Lucene's `BM25Similarity` defaults are k1 = 1.2, b = 0.75. Set them with `QueryEngineOptions.Bm25` (a `Bm25Parameters`) at the low level if you want Lucene's.
- A multi-field query in Lucene is usually a `BooleanQuery` of per-field queries (a sum of per-field scores) or a `DisjunctionMaxQuery`. OneIndex's layout is one weighted stream; the per-field-sum layout stays available through boolean query objects. Relevance on four BEIR sets for both layouts, and what each costs, is in [relevance.md](relevance.md).
- Scores are `float`s on the same scale only within one query on one index. `TotalHits` can be a lower bound (block-max WAND skips non-competitive blocks); call `CountExactly()` or `Count` when you need the exact number.

## Behaviour that differs

- **No schema-less documents.** A field must be declared before it is used (`[Dynamic]` JSON fields are indexed by path for the open-ended part). Changing the schema is a managed evolution: additive changes and analyzer changes rebuild in the background from stored source, the old version serving until the swap ([schema-evolution.md](schema-evolution.md)).
- **Upsert is first-class.** `Upsert` replaces by id; `UpdateDocument(new Term("id", ...), doc)` is the Lucene equivalent. `AddUnique` refuses a duplicate id.
- **Deletes** are by id (`Delete(id)`) or by exact term (`DeleteByTerm(field, value)`), visible after the next commit; deleted documents are skipped through a per-segment bitmap and removed by merges. There is no `DeleteDocuments(Query)`: select with a search and delete the ids.
- **Commit** always means durable and atomic, and the same on every storage. There is no separate `Flush` visibility step to reason about; NRT readers are opt-in.
- **Typed corruption errors.** A damaged file raises `CorruptIndexException` naming the file; a format from a newer version raises `UnsupportedFormatException`. Every reader runs under one `IndexLimits` object.
- **Cancellation and async.** Every I/O entry point takes a `CancellationToken`, and sync and async twins exist at each layer. On local and memory storage the async twin completes synchronously.
- **Vectors, hybrid and rerank** have no Lucene.NET 4.8 counterpart; see [vectors.md](vectors.md) and [hybrid.md](hybrid.md).
- **No reflection and no codecs by name from config files.** Analyzers, codecs and query JSON nodes are static registries, so the library is trim- and NativeAOT-safe.

## Existing indexes

OneIndex does not read Lucene indexes (the on-disk formats are unrelated), and no importer ships. Migrate by **reindexing from your source of truth**: read the documents from their database or files, and `Upsert` them in batches (`Upsert(IEnumerable<T>)` batches embedding too) followed by one `Commit()`. If the original documents exist only in a Lucene index as stored fields, read them out with Lucene.NET once and write them into OneIndex; a stored-field export is a loop over `searcher.Doc(i)`.

## A worked example

The same search in both libraries: products, the words "wireless headphones" in the title or body, only the `audio` category under 100, cheapest first, with the category facet and a highlighted title. The OneIndex half is compiled and tested (`samples/OneIndex.Samples/Migration.cs`); the Lucene.NET half uses its public API and is shown for orientation.

### Lucene.NET 4.8

```csharp
using var dir = FSDirectory.Open("index");
var analyzer = new EnglishAnalyzer(LuceneVersion.LUCENE_48);
var config = new IndexWriterConfig(LuceneVersion.LUCENE_48, analyzer) { Similarity = new BM25Similarity() };
using var writer = new IndexWriter(dir, config);

var doc = new Document
{
    new StringField("id", "p1", Field.Store.YES),
    new TextField("title", "Wireless headphones", Field.Store.YES),
    new TextField("body", "Over-ear wireless headphones with noise cancelling.", Field.Store.YES),
    new DoubleField("price", 89, Field.Store.YES),
    new StringField("category", "audio", Field.Store.YES),
    new DoubleDocValuesField("price", 89),                  // doc values, for sorting
    new SortedSetDocValuesFacetField("category", "audio"),
};
writer.UpdateDocument(new Term("id", "p1"), facetsConfig.Build(doc));
writer.Commit();

using var reader = DirectoryReader.Open(dir);
var searcher = new IndexSearcher(reader);
var parser = new MultiFieldQueryParser(LuceneVersion.LUCENE_48, new[] { "title", "body" }, analyzer);
var query = new BooleanQuery
{
    { parser.Parse("wireless headphones"), Occur.MUST },
    { new TermQuery(new Term("category", "audio")), Occur.MUST },
    { NumericRangeQuery.NewDoubleRange("price", null, 100.0, true, false), Occur.MUST },
};
var sort = new Sort(new SortField("price", SortFieldType.DOUBLE));
var fc = new FacetsCollector();
TopDocs top = FacetsCollector.Search(searcher, query, null, 10, sort, fc);   // null: no Filter
var facets = new SortedSetDocValuesFacetCounts(new DefaultSortedSetDocValuesReaderState(reader), fc);
foreach (ScoreDoc hit in top.ScoreDocs)
{
    Document found = searcher.Doc(hit.Doc);
    // Highlighter + QueryScorer + SimpleHTMLFormatter for the title fragment
}
```

### OneIndex, happy path

```csharp
public class Product
{
    [SearchId] public string Id { get; set; } = "";
    [FullText(Boost = 2)] public string Title { get; set; } = "";
    [FullText(Language = "en")] public string Body { get; set; } = "";
    [Filterable, Sortable] public double Price { get; set; }
    [Keyword, Facet] public string Category { get; set; } = "";
}

using var index = SearchIndex.Open<Product>("index");
index.Upsert(products);                  // replaces by id; batches
index.Commit();

var results = index.Search("wireless headphones")
                   .Where(p => p.Category == "audio" && p.Price < 100)
                   .OrderBy(p => p.Price)
                   .Facets(p => p.Category)
                   .Take(10)
                   .Execute();
foreach (var hit in results)
    Console.WriteLine(hit.Id + " " + hit.Highlights["Title"][0].Marked());   // "<em>Wireless</em> <em>headphones</em>"
var counts = results.Facets["Category"].Values;                              // audio = 2
```

### OneIndex, low level

```csharp
Schema schema = new SchemaBuilder().Id("id")
    .Add(new FieldDefinition("title", FieldType.Text) { Analyzer = "en" })
    .Keyword("category")
    .Fractional("price")
    .Build();

using var writer = IndexWriter.Open(new MemoryStorage(), schema);              // or FileSystemStorage
writer.Upsert(new Document().Add("id", "p1").Add("title", "Wireless headphones").Add("category", "audio").Add("price", 89.0));
writer.Commit();

using IndexSnapshot snapshot = writer.OpenSnapshot();
SearchContext context = SnapshotSearch.CreateContext(snapshot);
var searcher = new IndexSearcher(context, new QueryEngine());
var query = new BooleanQuery(
[
    new BooleanClause(new TermQuery("title", "wireless"), Occur.Must),
    new BooleanClause(new TermQuery("title", "headphon"), Occur.Must),          // analyzed by the caller: "en" stems it
    new BooleanClause(new TermQuery("category", "audio"), Occur.Filter),         // selects, does not score
    new BooleanClause(new RangeQuery("price", null, FieldValue.FromFloat64(100)), Occur.Filter),
]);
TopDocs top = searcher.Search(query, 10);
```

Differences worth noticing in the example: there is no analyzer object to share between indexing and searching (the schema names the field's analyzer); the price filter and the sort are the same declaration (`[Filterable, Sortable]`); facets and highlights come back with the results rather than from separate collectors; and the replacement for `UpdateDocument(new Term("id", ...), doc)` is `Upsert`.
