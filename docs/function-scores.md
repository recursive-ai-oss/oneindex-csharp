# Function scores

A function score changes the relevance score of a query with numbers computed from column values: popularity, recency, distance. It does not change which documents match (unless `MinScore` is set); it changes their order. The semantics and formulas follow the published `function_score` documentation of Elasticsearch (names: `field_value_factor`, `gauss`/`exp`/`linear`, `score_mode`, `boost_mode`, `max_boost`, `min_score`); this note is the normative text for OneIndex and lists every difference.

```csharp
index.Search("coffee")
     .ScoreBy(b =>
     {
         b.FieldValue(x => x.Rating, modifier: FieldValueModifier.Log1p);                       // popularity
         b.Decay(x => x.Opened, DecayShape.Gauss, DateTimeOffset.UtcNow, TimeSpan.FromDays(30)); // recency
         b.Distance(x => x.Location, DecayShape.Exp, 48.85, 2.29, scaleMeters: 2_000);           // nearness
         b.Weight(2f, when: x => x.Open == true);                                                // boost what matches
     })
     .Explain()
     .Take(10);
```

Low level: `new FunctionScoreQuery(inner, [functions]) { ScoreMode, BoostMode, MaxBoost, MinScore, Boost }` over the query objects; query JSON node `functionScore` ([query-json.md](query-json.md)).

## Evaluation

For each document the inner query matches:

1. Each function, in order, is tried. A function **applies** when its `Filter` (if any) matches the document and it has a value (below). Its **value** is `raw × weight`, computed in double and rounded to a 32-bit float.
2. The values of the functions that applied combine by `ScoreMode` into the **function score**: `Multiply` the product (default), `Sum`, `Average` (weighted: the sum of the values, which already include their weights, divided by the sum of the weights that applied; 0 when those weights are all 0), `First` (the first function that applied), `Max`, `Min`.
3. The function score is capped: `min(function score, MaxBoost)`. `MaxBoost` defaults to the largest float.
4. It combines with the inner score by `BoostMode`: `Multiply` (default), `Replace` (the function score alone), `Sum`, `Average` (the mean of the two), `Max`, `Min`.
5. The result is multiplied by the query's `Boost`.
6. If no function applied, the inner score is kept (times `Boost`); `BoostMode` plays no part.
7. A document whose score is below `MinScore` is dropped. This makes every match be scored, so such queries do not skip documents.

All arithmetic is in 32-bit floats from step 2 on, in the order above and the order of the functions, so the same document always gets the same bits, and the explanation equals the score exactly. A NaN becomes 0 and an overflow the largest float: scores stay finite.

## The functions

Every function has `Weight` (finite, not negative; default 1) and an optional `Filter` (a query; its score does not matter).

| Function | Raw value |
|---|---|
| `WeightFunction` | 1 (so its value is its weight; with a filter it boosts what matches) |
| `FieldValueFactorFunction(field)` | `modifier(factor × value)` of a numeric, date (milliseconds since 1970) or bool (0, 1) column |
| `DecayFunction(shape, field, origin, scale)` | the decay of a numeric or date column (below) |
| `GeoDecayFunction(shape, field, lat, lng, scale)` | the decay of the great-circle distance in metres ([geo.md](geo.md)) |
| `CustomFunction(description, bind)` | your code: `bind` runs once per segment with the segment reader and returns `docId => double?`. Low level only; not in query JSON |

**No value.** A function with no value for the document does not apply. For `FieldValueFactorFunction` this means: the column has no value and `Missing` is null, or the result is negative or not finite (the logarithm of 0 or less, the square root of a negative number, 1/0, a negative factor times a positive value). With `Missing` set, that number takes the place of the value, and the factor and modifier still apply to it. For the decay functions a document without a value scores **1** (it applies, as Elasticsearch's documentation says: "If the numeric field is missing in the document, the function will return 1"). A custom function returns `null` for "does not apply"; a negative or non-finite number also means that.

**Several values.** The factor reads the first value in stored order. A decay uses the value nearest to the origin (the distance is the minimum), as Elasticsearch's documentation says for its default `multi_value_mode`. Other multi-value modes are not offered.

### Modifiers (`field_value_factor`)

With `x = factor × value`: `none` x; `log` log₁₀ x; `log1p` log₁₀(1 + x); `log2p` log₁₀(2 + x); `ln` ln x; `ln1p` ln(1 + x); `ln2p` ln(2 + x); `square` x²; `sqrt` √x; `reciprocal` 1/x.

### Decay

With `d = max(0, |value − origin| − offset)` (for geo, the distance in metres in place of `|value − origin|`), `scale > 0`, `offset ≥ 0` (default 0) and `0 < decay < 1` (default 0.5):

- `gauss`: `exp(−d² / 2σ²)`, `σ² = −scale² / (2 ln decay)`
- `exp`: `exp(λ d)`, `λ = ln(decay) / scale`
- `linear`: `max((s − d) / s, 0)`, `s = scale / (1 − decay)`

All three equal 1 within `offset` of the origin and `decay` at `scale` beyond it; `linear` reaches 0 at `s` beyond it.

**Units.** Numbers: the field's own. Dates: milliseconds since 1970 for `origin`, `scale` and `offset` at the low level and in query JSON (an ISO date string is also accepted for `origin`); the LINQ overload takes a `DateTimeOffset` and `TimeSpan`s. Geo: metres. There is no `now` default for the origin: it is always given, so a query means the same thing whenever it runs.

## Execution

A function score has no score bound, so block-max WAND does not prune under it (the inner query's own pruning is not used either); every match of the inner query is scored. Top-k collection is otherwise unchanged. `TotalHits` follows the usual rule of the collector in use.

## Explain

`SearchQuery.Explain()` attaches an `Explanation` to each returned hit. For a function score the root reads `function score: query score <q> and function score <f> by <boost mode>, boost <b>`, with two children: the inner query's explanation and `function score, <score mode> of <n> function(s), capped at <max>`, whose children are one node per function that applied: the function, its raw value, the weight. When no function applied the root says so and has the inner explanation as its only child. The root value equals the hit's score exactly (tests assert it on every hit, on all execution paths).

## Differences from Elasticsearch

- No `random_score` and no `script_score` (the custom function is the low-level equivalent; no scripting language).
- A function with no value does not apply (Elasticsearch raises an error for a missing field without `missing`, and for a negative score).
- No `now` default for date origins; no unit strings (`"2km"`, `"10d"`): numbers, and `TimeSpan` in C#.
- Only the nearest value of a multi-valued field decays; no `multi_value_mode`.
- `min_score` is `MinScore`; `weight` per function and `filter` per function are kept; `first` is `First`.

## How it is checked

`FunctionScoreTests` compares the engine with these formulas computed directly from the documents' own values (not the columns), with the inner score taken from the same search without functions: random corpora over several segments with updates and deletes, random functions (every decay shape on numbers, dates and geo points, every modifier, `Missing` and filters, every score and boost mode, `MaxBoost`, `MinScore`), all matches compared with a relative tolerance of 1e-6 (the measured worst deviation is about 1e-7, 32-bit float rounding), the top 7 against a brute-force rescoring of every match, and the explanation of every hit against its score.
