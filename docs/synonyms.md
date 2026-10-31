# Synonyms and query-side expansion

Synonyms are applied at **query time** over analyzed terms. Nothing is stored in the index for them, so changing the synonym
table never needs a reindex. Semantics follow Meilisearch's documented ones (the *Synonyms* page of its relevancy documentation:
one-way and mutual associations, multi-word synonyms treated as phrases, normalization with the index settings, the exact query
taking precedence over its synonyms, the 1 to 3 word and 50 / 100 word limits). Terms are analyzed by the field's own analyzer,
so case, accents, stemming and stop words behave as in the index.

## Design: a token graph, then query objects

The analysis layer builds a **token graph** (`OneIndex.Analysis.TokenGraph`), a lattice over the query. The search layer lowers it
to the existing query objects (`OneIndex.Search.TokenGraphQueries`, which needs no new query kinds):

- Nodes are the boundaries between query words (`n` words give nodes `0..n`).
- An **original edge** `i -> i+1` carries the analyzed word (one edge per stacked form when the analyzer stacks a stem beside the
  word).
- A **synonym edge** `i -> j` is added for every rule whose key is the analyzed run of words `i..j-1` (1 to 3 words), one edge per
  alternative, carrying the alternative's terms (1 or more words).
- A **reading** of the query is a path from node 0 to the last node. The original reading comes first.

This is the equivalent expansion that keeps positions right: a multi-word synonym is a single edge, so it matches as a phrase, and
a phrase query over a word with a synonym is rewritten as one phrase per reading instead of a bag of positions.

### Rules

- `SynonymRules.AddOneWay(from, to...)`: `from` also finds each `to`, not the reverse (`phone => iphone`).
- `SynonymRules.AddGroup(a, b, c)`: every member reads as every other (`shoe <=> boot <=> slipper`); the same as one-way rules from
  each member to all the others. `AddAll` takes Meilisearch's settings shape (key to list).
- **Not transitive**: with `a => b` and `b => c`, `a` reads as `a` or `b`, never `c` (Meilisearch's mutual association is spelled out
  per member for the same reason).
- `Build(analyzer, limits)` analyzes every key and alternative. A phrase that analyzes to no terms (all stop words) is dropped;
  an alternative equal to its key is dropped; rules with the same key merge in the order added. Keys longer than
  `MaxQueryWords` can never fire and are dropped.

### Limits (`SynonymLimits`)

| Limit | Default | Behaviour past it |
|---|---|---|
| `MaxQueryWords` | 3 | A run of more query words is not looked up (Meilisearch: "terms with 4 or more words will not return any synonym matches"). A rule *whose alternative* is longer is fine (`lotr => lord of the rings`). |
| `MaxSynonymsPerTerm` | 50 | Later synonyms of the key are dropped, in the order added; counted in `SynonymMap.DroppedSynonyms`. |
| `MaxWordsPerTerm` | 100 | The first synonym that would take a key's synonyms past 100 words, and all later ones, are dropped. |
| `MaxPaths` | 64 | Readings of one query. Synonym edges are added left to right, shorter spans first, in rule order; an edge that would take the path count past the cap is left out (`TokenGraph.IsTruncated`). The original reading is always kept. |

A bag-of-words query does not multiply readings: `CutNodes` finds the nodes every reading passes through, the readings of each
stretch between them are alternatives (Should), and the stretches are combined with Must, so the clause count grows with the sum of
the stretches' readings, not their product. A phrase query needs the product (one phrase per whole reading), which `MaxPaths` bounds.

### Lowering

`TokenGraphQueries.ToWords(graph, field, synonymBoost)`: all words must match, in any position. A reading's original edges are
`TermQuery` clauses; a synonym of one word is a `TermQuery`, of several words a `PhraseQuery` (Meilisearch: multi-word synonyms are
phrases). A reading is a `Must` group of its parts; readings of a stretch are `Should`.
`TokenGraphQueries.ToPhrase(graph, field, slop, synonymBoost)`: one `PhraseQuery` per distinct whole reading (a `TermQuery` for a
one-term reading with slop 0), combined with `Should`.

**Exact query first.** Parts that come from a synonym carry `synonymBoost` (default **0.8** [agent default]); the original words
carry 1, so a document with the user's own words outscores one with only a synonym. A document with both scores both.

A query with no terms (empty, or only stop words) lowers to `MatchAllQuery`.

## Stop words

See docs/analysis.md. In short: configured stop words are dropped by the analyzer, so index and query sides agree, and stop words
vanish from phrases. Positions:

### Positions and gaps

`StopWordSettings.PreserveGaps` (default **false**) decides what a dropped word does to positions.

- **Collapsed (default).** The neighbours become adjacent: `king of queens` indexes `king@0 queens@1`. The query phrase
  `"king of queens"` is `[king, queens]`, exact, and matches text with the stop word and text without it. This is Meilisearch's
  documented behaviour (stop words are ignored in phrases: `"the great gatsby"` matches what `"great gatsby"` matches). The cost
  is that proximity cannot tell `king queens` from `king of queens`.
- **Preserved.** `king@0 queens@2`. Positions stay truthful, but the query objects have no per-term positions, so
  a phrase with a removed word becomes a `PhraseQuery` over the remaining terms with `Slop` raised by the number of removed
  positions between them. That accepts a little extra distance beyond the removed words; it is an over-approximation, documented
  as such.

Synonym rules are analyzed with the same analyzer, so a rule written `the big apple => nyc` fires on the query `big apple`.

## Verification

`tests/OneIndex.Tests/SynonymTests.cs`: Meilisearch's documented examples (great/fantastic, phone => iphone, the four-way shoe
group, san francisco / sf, lord of the rings / lotr, 50 and 100 limits, the Résumé / CV normalization case, exact-first scoring);
overlapping rules; expansion bounds; and 4 seeded runs of 2,400 random (rules, query, document) cases, each lowered query evaluated
by a small reference evaluator and compared with an independent recursive enumeration of readings, for both the bag-of-words and
the phrase forms (0 disagreements). `StopWordConfigurationTests.cs` covers the stop-word cases.
