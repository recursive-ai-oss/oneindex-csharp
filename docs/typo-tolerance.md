# Typo tolerance and as-you-type

What user text does when a word is misspelled or half typed. It applies to forgiving user text (`index.Search("...")`). The strict
parser and query JSON keep their explicit `term~edits` fuzzy semantics and do not use these settings.

Behaviour and defaults follow Meilisearch's published *Typo tolerance* documentation (settings `enabled`, `minWordSizeForTypos`,
`disableOnWords`, `disableOnAttributes`, `disableOnNumbers`, and the description "How typo tolerance works"). Where this page says
"counts as" it states what the documentation says; the exact edit model (below) is ours.

## What counts as a typo

For a query word `q` and an indexed word `w`, both as code points after analysis:

    typos(q, w) = osa(q, w) + (first(q) != first(w) ? 1 : 0)

- `osa` is the optimal string alignment distance: insertion, deletion, substitution, and the swap of two adjacent characters each
  cost one (a swapped pair is not edited again). A transposition therefore counts as one typo.
- A typo on the first character counts as two: when the first characters differ, one more typo is charged. `caturday` is two
  typos from `saturday`, so it matches only a word with a budget of two.

## Budget per word

| Query word length (code points) | Typos allowed |
|---|---|
| below `MinWordSizeForOneTypo` (default 5) | 0 |
| from `MinWordSizeForOneTypo` | 1 |
| from `MinWordSizeForTwoTypos` (default 9) | 2 |

Never more than two. Settings are validated: `0 <= one <= two <= 255`. The length is that of the analyzed word (after stemming, if the
field's analyzer stems).

A word gets no typos (the budget is 0) when typo tolerance is off, when it is in `DisableOnWords` (compared ignoring case, against the
analyzed word and the word as typed), or when `DisableOnNumbers` is set and the word is only digits. In a field listed in
`DisableOnFields` only matches without typos count.

## As-you-type

The last word of the text matches words that **begin** like it (prefix search), with typos allowed in that beginning: a word `w`
matches `q` as a beginning when some non-empty beginning `p` of `w` has `typos(q, p)` within the budget. The budget is that of the
typed word, so a short word being typed (under 5 characters) matches exact beginnings only.

The last word is the one being typed unless: the text ends in a space or a quote, it is quoted, it is excluded (`-word`), or the
settings turn prefix search off. Words before the last are matched as whole words with their typo budget. (chosen here: the trailing
space rule is not taken from Meilisearch's documentation, which does not state it.)

Closer matches score higher and every expanded word is scored as the word it is, so a document that matches the full word does not
move down the list as the word is typed out; a test follows one target character by character.

## Ranking

BM25F stays the base score. Each indexed word that a query word expands to is scored as a term of its own (its own document frequency
and idf) and multiplied by two factors; the products are summed over the expansions a document matches:

    boost(match) = typo factor * exactness factor
    typo factor      = 1 / (1 + typos)                    1, 0.5, 0.333 for 0, 1, 2 typos
    exactness factor = 1 for a whole word, 0.8 for a longer word that only begins like the query word

The constants are choices made here; the order they give (exact word, then longer words, then typos) is what Meilisearch's `typo`
and `exactness` ranking rules describe. Proximity is left to the ranking-rules work. With typo tolerance and prefix search off the
query is the plain BM25F term query, so scores are unchanged. `Explain` shows each expansion with its typos, the typo factor, the
exactness factor and the boost (`docs/scoring.md`).

When a word expands to more than `MaxExpandedTerms` (1,024) indexed words, the closest, then whole words before longer ones, then the
most frequent are kept; this expansion never throws, because user text must not fail on a short word.

## Matching

`TypoTermQuery` (`OneIndex.Search`) holds the query word, its fields, `MaxTypos`, `Prefix` and the fields without typos.
Candidates come from the term dictionary with Levenshtein automata (Schulz and Mihov 2002), a superset that each candidate is then
costed against with the reference computation above:

- budget 1: the first character is fixed (a different first character would cost at least two): one automaton run with that
  character as the enumeration prefix;
- budget 2: the same run with two edits, plus a run with one edit and no fixed first character (a different first character costs
  one extra);
- as-you-type: the automaton is wrapped so that once a non-empty beginning is within the budget every longer word is accepted
  (prefix Levenshtein); with budget 0 it is a plain prefix scan.

## Settings (attributes on the document type)

    [TypoTolerance(Enabled = true, MinWordSizeForOneTypo = 5, MinWordSizeForTwoTypos = 9,
                   DisableOnNumbers = false, DisableOnWords = ["shrek"], DisableOnFields = ["Code"])]
    [PrefixSearch(true)]
    [Synonyms("tv", "television")]            // a group: each member also matches the others
    [OneWaySynonyms("laptop", "notebook")]    // a query for laptop also matches notebook
    [StopWords("the", "of")]
    public class Product { ... }

All settings are query-time. They are recorded in the schema of each commit (`docs/commit-format.md`, schema version 2), so the index
knows the settings it was last written with. Opening an index whose attributes differ only in these settings is not an error: the
attributes in the code are used at once, `IndexWriter.SchemaSettingsChanged` reports it, and the next commit records them.

- **Synonyms** are analyzed with the field analyzer and matched with the machinery of `docs/synonyms.md`, per operand (a word or a
  quoted phrase, up to three words), at the synonym boost of 0.8. Synonym words are matched exactly, without typos. Synonyms
  spanning several operands are not applied.
- **Stop words** are dropped from user text before matching (also inside a phrase, which keeps its gap). They are not removed from
  the index. A text of only stop words matches nothing.

## Checks

`tests/OneIndex.Tests/TypoToleranceTests.cs`: the cost against an independent recursion; the documented examples; the automaton
matcher against a naive scan of every indexed word, for 0 to 2 typos with and without prefix matching over generated vocabularies
(`ONEINDEX_TYPO_CASES` and `ONEINDEX_TYPO_WORDS` scale it); ranking order; explain; schema round trip and reopen; the attributes end
to end; ranking while a word is typed character by character.
