# Aggregations

This note is normative for `OneIndex.Aggregations` (`Aggregation` requests, `AggregationCollector`, the result types and `AggregationBuilder<T>`). Facets, which share the machinery, are in [facets.md](facets.md). Names follow Meilisearch where it has a term and Elasticsearch's published aggregation documentation otherwise.

An aggregation is computed over **every document a query matches** (deleted documents excluded), from the fields' columns ([columns.md](columns.md)). The request classes are plain data (a name, a field, a few settings, optional sub-aggregations) so they can be built in code or read from a description. Results come back in request order in an `AggregationResultSet`, addressable by name.

```csharp
var collector = new AggregationCollector(schema, [
    new TermsAggregation("by_tag", "tags") { Size = 5, SubAggregations = [new StatsAggregation("price", "price")] },
    new DateHistogramAggregation("per_month", "added") { CalendarInterval = DateInterval.Month },
    new PercentilesAggregation("p", "price") { Percents = [50, 99] }]);
searcher.Search(query, collector);
BucketsResult tags = collector.GetResults().Get<BucketsResult>("by_tag");
```

Happy path: `index.Search(...).Aggregate(a => a.Terms("by_tag", x => x.Tags, size: 5, sub: s => s.Stats("price", x => x.Price)).DateHistogram("per_month", x => x.Added, DateInterval.Month))`, results in `SearchResults<T>.Aggregations`. The builder exposes `Terms`, `Range`, `Histogram`, `DateHistogram`, `Stats`, `Cardinality` and `Percentiles`; the request classes carry every option.

Validation happens when the collector is built (`ArgumentException` naming the aggregation): the field must exist, keep a column, and have a type the aggregation reads; names must be unique among siblings; settings must be in range.

## Counting rules (all bucket aggregations)

A document counts **once per bucket**, however many of its values fall in it. Sub-aggregations see the whole document (all its values of the sub-field), not only the values that put it in the bucket. Documents without a value of the bucketed field are in no bucket. Counts are exact.

## Buckets

| Request | Fields | Buckets |
|---|---|---|
| `TermsAggregation` | keyword, bool, integral, fractional, date | one per distinct value. `Size` (default 10) buckets in `Order` (count descending, key ascending or descending; count ties by key ascending), dropping those below `MinDocCount` (default 1). `OtherDocCount` sums the counts of buckets cut by `Size`. Keys: keywords by ordinal UTF-16 order, numbers and dates by value; `-0` and `0` are one key. |
| `RangeAggregation` | integral, fractional, date | one per requested range, in request order; `From` inclusive, `To` exclusive, either may be open; ranges may overlap. Values compare as `double` (exact to 2^53, dates are milliseconds). Label `from-to` with `*` for an open end, or the given key. |
| `HistogramAggregation` | integral, fractional | fixed width: value `v` goes to bucket `floor((v - Offset) / Interval)`, key `id * Interval + Offset`. With `MinDocCount` 0 (the default, as in Elasticsearch) empty buckets between the first and last used bucket are returned; with 1 or more only buckets that qualify. NaN, infinite or overflowing values are ignored. |
| `DateHistogramAggregation` | date | UTC time buckets: a `CalendarInterval` (second, minute, hour, day, week starting Monday, month, quarter, year) or a `FixedInterval` (whole milliseconds, counted from the epoch plus `Offset`). Key `Whole`/`Number` is the bucket start in milliseconds; `Text` is ISO 8601. Empty buckets are filled like the histogram. Month, quarter and year buckets ignore values outside years 1–9999. |

A histogram that would return more than `AggregationCollector.MaxBuckets` (65,536) buckets throws `TooManyBucketsException` when results are read.

## Metrics

| Request | Result |
|---|---|
| `StatsAggregation` | `Count` (of **values**: a document with three values adds three), `Min`, `Max`, `Sum`, `Avg`. Integral and date sums are exact (128-bit, rounded once to a double); fractional sums use Neumaier compensated summation. NaN values are skipped. All null/zero for no values. |
| `CardinalityAggregation` | the number of distinct values, estimated: see below. |
| `PercentilesAggregation` | each requested percent with its estimated value (NaN with no values): see below. |

Fractional sums depend on the order values are added, so they can differ in the last bits between an index in one segment and the same documents in several; the differential test allows a relative `1e-9`. Everything else is independent of segment layout, merges and deletes.

## Cardinality: HyperLogLog++

Heule, Nunkesser and Hall, "HyperLogLog in Practice: Algorithmic Engineering of a State of the Art Cardinality Estimation Algorithm", EDBT 2013. Values are hashed to 64 bits (the SplitMix64 finalizer for integers, an 8-bytes-at-a-time multiplicative hash with the same finalizer for keywords; no per-process seed, so results repeat). Precision `p` (default 14, range 4–18) gives `2^p` one-byte registers.

- **Sparse form.** While at most `2^p / 8` distinct hashes have been seen the sketch keeps the hashes themselves, so the answer is **exact** (up to 64-bit collisions): 2,048 distinct values at the default. This plays the role of the paper's sparse representation. Difference: the paper packs 25-bit codes; here a whole hash costs 8 bytes and the switch point makes the memory equal to the dense form.
- **Dense form.** Registers hold the rank of the first 1 bit among the hash bits below the top `p`. The estimate uses the closed-form **improved estimator of Ertl** ("New cardinality estimation algorithms for HyperLogLog sketches", 2017, Algorithm 6) in place of the paper's empirical bias-correction tables and linear-counting switch. It needs no tables, is accurate from empty to saturated, and was verified against the standard error here. Difference to record: results are not bit-identical to implementations that use the paper's tables.
- **Error.** Standard error `1.04 / sqrt(2^p)`: 0.81% at `p = 14`. The tests assert at most 4 standard errors over 42 trials per precision at sizes up to 20·2^p and observed 1.6–2.9 (`p` 8, 12, 14, 16).
- **Merging** segment sketches is the union (register-wise maximum, or set union while sparse), so the segment layout does not change the answer.

## Percentiles: t-digest

Dunning and Ertl, "Computing Extremely Accurate Quantiles Using t-Digests", 2019 (arXiv:1902.04023): the **merging digest** with the arcsine scale function `k1(q) = δ/(2π)·asin(2q − 1)`. Values are buffered and, when the buffer is full, sorted and merged with the centroids in one pass so that no centroid spans more than one unit of `k`; centroids stay tiny at the extremes and grow towards the middle. `Compression` (δ) defaults to 200. A quantile interpolates linearly between the exact minimum, the centroid centres (half a centroid's weight past the weight before it) and the exact maximum.

Chosen over other sketches because it is mergeable, has bounded memory (a few hundred centroids), and is accurate where percentile queries look (p1, p99, p99.9). Error measured here at δ = 200 over 300,000 values (uniform, log-normal, sorted, few distinct): rank error at most 0.035% anywhere and 0.013% in the 0.01% and 99.99% tails; the tests assert 0.5% and 0.05%. For fewer values than centroids the digest holds every value and interpolates between neighbours, within one rank. The result is a pure function of the order values are added; the order follows document order within a segment and segment order across segments, so equal indexes give equal answers, while the same documents in a different segment layout may differ within the error bound.

## Cost

Like facets, aggregations need every match: they make a search visit all of them, and WAND skipping does not apply (see facets.md, "Cost and exactness"). Collecting a document allocates nothing once a segment's state exists: counters are `int[]` per dictionary term (keywords) or entries of an open-addressing table (numbers, histograms), one stamp per bucket to count a document once, register and buffer arrays for the sketches. The per-segment state is built when the segment starts (sized by its dictionary) and merged into the result when the next segment starts or results are read. A two-facet query over 2,600 documents allocated about 8.5 KB in total, nearly all of it results.

## Verified against

`AggregationDifferentialTests`: seeded random corpora (several segments, merged and not, deleted documents, missing and repeated values, multi-valued keyword and integer fields, -0.0) and random queries; every facet, terms/range/histogram/date-histogram bucket (with sub-aggregations), stat, cardinality (exact range) and distinct page equals a LINQ-to-Objects computation. Percentiles are checked by rank against the sorted values. `AggregationSketchTests` checks the two sketches' error bounds on large inputs.
