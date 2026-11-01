# User text

What a person types into a search box. `index.Search(text)` accepts **any** string and never throws on it: unbalanced quotes, lone operators, control characters, lone surrogates and megabytes of input all give a sensible query. (Running the query can still fail for reasons that have nothing to do with the text: a disposed index, a cancelled token, a damaged file.)

This page is normative. The parser is `UserText.Parse`; the compiler to query objects is `UserTextCompiler`. Both are public so that the same text can be inspected or compiled without an index.

## Grammar

The input is first **cleaned**:

- it is cut to 4,096 UTF-16 code units (a surrogate pair at the cut is dropped whole);
- every control character, every Unicode whitespace character and every unpaired surrogate becomes a space.

Then it is read left to right as a list of **operands** separated by spaces:

| Form | Meaning |
|---|---|
| `word` | A word: everything up to the next space or double quote. |
| `"two words"` | A phrase: the words must appear next to each other, in order. The quote ends at the next `"`; an unclosed quote runs to the end of the input. A phrase with only spaces in it is ignored. |
| `-word`, `-"a phrase"` | Exclusion: documents containing it do not match. The `-` must touch the operand. A `-` followed by a space, or alone, is an ordinary character that analysis drops. Only one leading `-` is the operator (`--cat` excludes `-cat`, which analyzes to `cat`). A `-` inside a word is part of the word. |
| `a OR b` | Alternatives. `OR` (exactly those capitals) between two operands makes them one group that matches when any member does. `a OR b OR c` is one group. |

Rules around `OR`:

- Operands that are not joined by `OR` must all match (and).
- `OR` with no operand on its left (first in the text) or right (last, or followed by another `OR`) is ignored. `cat OR OR dog` is `cat OR dog`.
- An exclusion after `OR` cancels the operator: `cat OR -dog bird` requires `cat` and `bird` and excludes `dog`.
- A lowercase `or` is an ordinary word (and is dropped as a stop word by languages that have one).
- Nothing else is special: no parentheses, no `AND`/`NOT`, no `field:value`, no wildcards, no escaping. Those characters are ordinary text, and analysis decides what is left of them.

At most **32 operands** are read (positive and excluded together); the rest are ignored.

## Compilation

Each operand is analyzed with the analyzer of every full-text field. Fields that share an analyzer are searched together as one BM25F term (the fields' boosts are the weights); fields with different analyzers are searched separately with their own terms and their scores add. So a title analyzed as `standard` and a body analyzed as `en` each see the word the way they indexed it.

- A word that analyzes to one term is a `MultiFieldTermQuery` (one field: a `TermQuery`), or a `TypoTermQuery` when typo tolerance or as-you-type applies to it.
- A word that analyzes to several terms (`state-of-the-art`, `acme.corp`) and every phrase is a `PhraseQuery` per field. Positions that analysis skipped, such as stop words, widen the slop by as many positions, so `"state of the art"` matches the text it came from.
- A word that analysis removes entirely (a stop word, punctuation) is dropped. In a group of alternatives the others still count.

The query is: each group of alternatives is a `Must` clause; each exclusion is a `MustNot` clause. If only exclusions remain, a `MatchAllQuery` is the `Must` clause, so `-draft` means every document except those with `draft`.

| Input | Result |
|---|---|
| `null`, empty, or only whitespace and control characters | `MatchAllQuery`: every document, score 1 (a *blank search*, like a placeholder search in a search-as-you-type box). |
| Words exist but none survives analysis, and nothing is excluded (`?!`, `the` in English) | Matches nothing. |
| The schema has no full-text field | Matches nothing for any words. |

Scoring is BM25F as in [search-contracts.md](search-contracts.md); the filters from `Where` never change the score.

## Typos, as-you-type and settings

By default every unquoted word gets Meilisearch-style typo tolerance and the last word also matches words that begin with it; the
attributes `[TypoTolerance]`, `[PrefixSearch]`, `[Synonyms]`, `[OneWaySynonyms]` and `[StopWords]` on the document type change that
(`docs/typo-tolerance.md`). Quoted phrases and excluded words (`-word`) are matched as written.

## What is not here yet

Field-qualified terms arrive with the app-search layer. The strict parser with positioned errors is a separate, opt-in grammar.

## Highlighting

A hit's `Highlights` re-analyzes the stored text of each full-text field with the field's analyzer and marks the tokens whose terms the query contains (excluded operands never mark). A phrase marks its words only where they occur in order; overlapping or touching marks, as CJK bigrams are, merge into one run. See [mapping.md](mapping.md#highlights).
