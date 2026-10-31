# Analysis

An `Analyzer` turns text into terms. It is a tokenizer followed by filters, built once and reused. This page is normative for the contract, the registered analyzers, the Unicode and Snowball versions and the folding rules; two indexes built by different builds agree on terms only if the analyzer name and these rules agree.

## Token

`TokenSource` is a pull source: `Reset(text)`, then `MoveNext()` until it returns false. One mutable `Token` object is shared by the whole chain; a tokenizer fills it, each filter edits it in place or drops it. Once the term buffer has grown to the longest term, a chain allocates nothing per token (the tests measure this for `standard`, `en`, `en-both` and `cjk`).

- **Term text** is UTF-16. .NET strings are UTF-16, so offsets index the original string directly (highlighting, snippets), case and accent folding edit characters in place, and the single conversion to UTF-8 happens where a term enters the term dictionary.
- **Offsets** are `[StartOffset, EndOffset)` in characters of the original text. A term made by a filter (a stem, a folded form) keeps the offsets of the word it came from; a CJK bigram covers both characters.
- **Positions**: the first token's position is its increment minus 1, so an increment of 1 gives position 0. An increment of 0 stacks a token on the previous position (synonyms, a stemmed twin). A filter that removes a token adds its increment to the next token's, so a phrase across a removed stop word keeps its gap.
- **Type** classifies a token: `Word`, `Number` (digits only), `Ideographic` (Han, Hiragana, Katakana or Hangul text), `Synonym` (added by a filter at another token's position), `Other`.
- **Marks** are flags: `Keyword` (a filter must not rewrite the term; the stemmer honours it) and `Stemmed` (the term is a stem a stemming filter added beside its word).

### Stemmed and unstemmed forms

`StemFilter` has two outputs. `Replace` swaps the word for its stem. `Both` keeps the word and, when the stem differs, adds the stem right after it as a second token at the same position: increment 0, type `Synonym`, marks `Stemmed`, same offsets. A stem is never empty; a word the algorithm would delete is kept as it is.

An indexer that wants exact matches to rank above stem matches reads the mark: tokens without `Stemmed` are the words as written (after case folding), tokens with it are stems, and the indexer can store them in separate term spaces or fields. A query analyzed with the same `-both` analyzer produces the same pairs; one analyzed with the plain language analyzer produces only stems. The registry names the two forms `en` (stems only) and `en-both`.

## Analyzer and registry

An analyzer instance is not thread-safe; each thread asks `AnalyzerRegistry` for its own. The name is what a schema stores. The registry is a plain `switch` and a fixed table in code, not discovery by reflection, so trimming and NativeAOT keep what is named and nothing else.

| Name | Chain |
|---|---|
| `standard` | `StandardTokenizer` → `CaseFoldFilter`. The default for text fields. No stop words, no stemming, no accent folding. |
| `cjk` | `StandardTokenizer` with CJK bigrams → `NormalizationFilter` (NFKC) → `CaseFoldFilter`. |
| `<code>` | `StandardTokenizer` → [`TurkishCaseFilter` for `tr`] → `CaseFoldFilter` → `ApostropheFilter` → [`ElisionFilter` for `fr`, `it`, `ca`] → [`StopFilter` where Snowball publishes a list] → `StemFilter(Replace)` → [`AsciiFoldFilter` for `en`; `AccentFoldFilter` for `fr`, `es`, `it`, `pt`, `ca`, `nl`, `ro`]. |
| `<code>-both` | The same with `StemFilter(Both)`. |
| `keyword` | The whole text as one term. |
| `whitespace-lowercase` | Split at white space, invariant lower case; a reference for tests. |

Accent folding runs after stemming on purpose: the stemming algorithms read accented letters. It is applied to the Latin-script languages whose readers routinely drop accents when typing; it is not applied to languages where the accented letters are distinct letters (Danish, Norwegian, Swedish, Finnish, Turkish, Polish, Czech and the others).

Language codes and the Snowball algorithm each uses: `ar` arabic, `ca` catalan, `cs` czech, `da` danish, `de` german, `el` greek, `en` english (Porter2), `eo` esperanto, `es` spanish, `et` estonian, `eu` basque, `fa` persian, `fi` finnish, `fr` french, `ga` irish, `hi` hindi, `hu` hungarian, `hy` armenian, `id` indonesian, `it` italian, `lt` lithuanian, `ne` nepali, `nl` dutch, `no` norwegian, `pl` polish, `pt` portuguese, `ro` romanian, `ru` russian, `sr` serbian, `st` sesotho, `sv` swedish, `ta` tamil, `tr` turkish, `yi` yiddish.

`standard` is the analyzer to compare with other engines' "standard tokenization, lower-case, no stemming": Lucene's own `StandardAnalyzer` also removes English stop words, so a comparison configures that side without its stop filter.

## Word and sentence boundaries

`StandardTokenizer` splits at the word boundaries of Unicode Standard Annex #29 revision 47 (Unicode 17.0.0), rules WB1 to WB999, implemented in `Analysis/Unicode/WordBreaker.cs`. A segment becomes a token when it contains a letter, a decimal digit or a letter number (general category L\*, Nd, Nl); white space, punctuation, symbols and emoji are dropped. `SentenceBreaker.cs` implements SB1 to SB998 for the file chunker.

Both walk the text once with a few facts about the segment they are building, so cost is linear even for a million combining marks, a million flag symbols or a million closing brackets (a test runs those). They read UTF-16 and treat a lone surrogate as a character with no properties.

Conformance: 100% of `WordBreakTest.txt` (1,944 cases) and `SentenceBreakTest.txt` (512 cases) from the UCD 17.0.0 pass (`tests/OneIndex.Tests/Uax29ConformanceTests.cs`; the two files are in `testdata/ucd/` with a manifest).

Choices the Annex leaves open:

- **Token length.** A word longer than 255 characters is cut into pieces of 255 (never inside a surrogate pair), so no input makes an unbounded term. `StandardTokenizer(maxTokenLength)` changes the limit.
- **What is a token.** Letters and digits as the UCD classifies them for Unicode 17.0.0 (generated, not taken from the platform's older tables).
- **CJK.** Ideographs, kana and Hangul are letters, so each Han ideograph and each kana is its own token (UAX #29 breaks around them), Katakana and Hangul words stay whole, and `cjk` makes bigrams from them.

### CJK bigrams

With `cjkBigrams`, a run of characters of one script (Han, Hiragana, Katakana or Hangul, from `Scripts.txt`; U+30FC and U+FF70 count as Katakana) becomes overlapping two-character terms: 日本語 gives 日本 and 本語, and a lone character stays a one-character term. Combining marks and format characters stay with their base character. Runs of different scripts do not bigram together. Each term has increment 1 and type `Ideographic`. Half-width Katakana voicing marks (U+FF9E, U+FF9F) are Common script and end a run before NFKC joins them; dictionary segmentation (Japanese, Chinese, Korean) is the job of a separate package.

## Normalization and folding

- **Normalization (NFC, NFD, NFKC, NFKD)** is OneIndex's own implementation of UAX #15 over tables generated from the UCD (`Analysis/Unicode/Normalizer.cs`), not the platform's. Finding that decided it: with invariant globalization (the default for browser WebAssembly builds, and set for this repository's test projects), `string.Normalize` and `IsNormalized` return non-ASCII text unchanged without raising anything, so an index written in such a process would hold different terms from one written with ICU. The implementation passes all of `NormalizationTest.txt` (UCD 17.0.0, about 19,000 lines, all five columns and all four forms). The test reads the file from a cache because it is 2.5 MB; set `ONEINDEX_UCD_DIR`.
- **Case folding** is full folding from `CaseFolding.txt` (statuses C and F): `ß` → `ss`, `ſ` → `s`, final sigma → sigma, `İ` → `i` plus a combining dot. It does not depend on the culture. The Turkic mappings (status T) are not used; the `tr` analyzer maps `I` → `ı` and `İ` → `i` first.
- **Accent folding** (`AccentFoldFilter`): decompose (NFD), drop the combining marks of the diacritical blocks (U+0300–036F, 1AB0–1AFF, 1DC0–1DFF, 20D0–20FF, FE20–FE2F), compose (NFC). Hangul and kana come back unchanged.
- **ASCII folding** (`AsciiFoldFilter`): compatibility decomposition (NFKD) so ligatures, full-width forms and superscripts become plain, accents dropped, then a short table for Latin letters that do not decompose: Æ→AE, Œ→OE, ß→ss, Ø→O, Đ→D, Ð→D, Ł→L, Ħ→H, ı→i, Ŧ→T, Þ→TH, ĸ→q, Ŋ→N and the same lower case, plus typographic quotes and dashes. Characters with no ASCII form stay.
- **Apostrophes** (`ApostropheFilter`): U+2018, U+2019, U+02BC and U+FF07 become `'`, so stop words and elision match.
- **Elision** (`ElisionFilter`): French `l' m' t' qu' n' s' j' d' c' jusqu' quoiqu' lorsqu' puisqu'`, Italian `c' l' all' dall' dell' nell' sull' coll' pell' gl' agl' dagl' degl' negl' sugl' un' m' t' s' v' d'`, Catalan `d' l' m' n' s' t'`.

## Stemmers

Thirty-four stemmers, one per language above, generated from the Snowball algorithm scripts by `tools/SnowballGen`, a compiler written for this repository from the Snowball manual (<https://snowballstem.org/compiler/snowman.html>). Only the `.sbl` scripts and the manual were read; the Snowball project's own compiler, runtime and generated stemmers were not.

- **Scripts:** `algorithms/<language>.sbl` of Snowball release v3.1.1 (commit cd195b51e948a902a4312f023f4a14392516a543), copied unchanged into `tools/SnowballGen/algorithms/`. BSD-3-Clause, see `THIRD-PARTY-NOTICES.md`.
- **Runtime:** `Analysis/Snowball/SnowballProgram.cs` is the string machine the manual describes (cursor, limits, slice, `among` with longest match and routine conditions, forward and backward modes). Strings are UTF-16 code units, which is exact for every supported script (all in the Basic Multilingual Plane).
- **Choices where the manual is silent:** after a replacement, a cursor at or past the replaced text moves with it and a cursor inside it moves to its start; a routine named in an `among` entry runs with the cursor just past the matched text; backward `insert` leaves the cursor on the left of the new text and `attach` on the right (forward is the mirror image); saved cursors in backward code are kept as the distance from the end of the text so deletions to the left do not disturb them. The published vocabularies confirm each.
- **Conformance:** every stemmer reproduces its published vocabulary exactly, 100% of 11,481,578 words across the 34 languages (table below). The test needs the snowball-data repository (<https://github.com/snowballstem/snowball-data>, main, archive SHA-256 `5bacb3c4f822d167707d87e08e807f65c27ac517543ef57fe4a2bfcf5bfcb7ed`) in a cache; set `ONEINDEX_SNOWBALL_DATA`. The data is fetch-only. Its licences are per directory (BSD-3-Clause for the original lists; Wikipedia-derived lists CC BY-SA; the Arabic list GPL-3.0), which is acceptable for a test oracle that is never redistributed.

| Language | Words | Language | Words | Language | Words |
|---|---|---|---|---|---|
| arabic | 9,196,214 | hindi | 65,118 | polish | 62,576 |
| armenian | 101,780 | hungarian | 29,881 | portuguese | 32,016 |
| basque | 21,386 | indonesian | 64,586 | romanian | 87,642 |
| catalan | 48,897 | irish | 186,280 | russian | 49,785 |
| czech | 58,137 | italian | 37,426 | serbian | 29,996 |
| danish | 23,868 | lithuanian | 86,109 | sesotho | 9,848 |
| dutch | 45,670 | nepali | 4,000 | spanish | 28,378 |
| english | 42,649 | norwegian | 20,913 | swedish | 30,738 |
| esperanto | 84,456 | persian | 104,268 | tamil | 443,271 |
| estonian | 57,537 | | | turkish | 96,325 |
| finnish | 84,399 | | | yiddish | 99,996 |
| french | 21,653 | | | | |
| german | 35,053 | | | | |
| greek | 90,727 | | | | |

Every row matched in full (matched = words).

## Stop words

The lists Snowball publishes at `https://snowballstem.org/algorithms/<language>/stop.txt`, for Danish, Dutch, English, Finnish, French, German, Hungarian, Indonesian, Irish, Italian, Norwegian, Portuguese, Russian, Sesotho, Spanish and Swedish (`tools/generate-stopwords.py` writes `StopWordData.g.cs`, recording each list's SHA-256). Other languages have no list and keep their stop words. Stop words are matched after case folding and before stemming, exactly as written (so accented forms are listed with accents); `StopWords` is a sorted array searched without allocating.

### Configured stop words

`StopWordSettings` (words, `Mode`, `PreserveGaps`) configures stop words per index and is passed to `LanguageAnalyzer` (or to a custom chain through `CreateFilter`). `Mode.Replace` (default) uses only the configured words, so an empty list turns stop words off; `Mode.Extend` adds them to the language's list (for a language with no list, the configured words alone). Configured words are folded as the standard analyzer folds text (`The` is `the`) and are matched, like the defaults, after case folding and before stemming and accent folding.

Stop words are removed by the analyzer, so they are handled at **both ends** by the same code: a stop word is never indexed, and it is dropped from the query before any query object is built, also inside quoted phrases (`"the great gatsby"` is the phrase `great gatsby`, matching the text with and without the stop word). Positions: with `PreserveGaps` false (the default for configured lists) the neighbours of a removed word become adjacent; with true the gap stays in the positions (the unconfigured language lists keep their gaps, as before). A query made only of stop words has no terms and lowers to match-all, as an empty search returns every document. The analyzer name does not change with the settings: the settings belong to the index and must be the same when it is built and when it is searched. Changing them needs a reindex. See docs/synonyms.md, "Positions and gaps".

## Regenerating

    tools/UcdTables      dotnet run --project tools/UcdTables -- <UCD 17.0.0 directory> src/OneIndex/Analysis/Unicode
    tools/SnowballGen    dotnet run --project tools/SnowballGen -- tools/SnowballGen/algorithms src/OneIndex/Analysis/Snowball/Languages "Snowball v3.1.1"
    tools/generate-stopwords.py <directory of <language>.txt> src/OneIndex/Analysis/StopWordData.g.cs

The generated files record their inputs. The UCD files used are `WordBreakProperty.txt`, `SentenceBreakProperty.txt`, `emoji-data.txt`, `CaseFolding.txt`, `UnicodeData.txt`, `Scripts.txt` and `DerivedNormalizationProps.txt`; `tools/fetch-analysis-data.sh` fetches and checks them.

## Limits and safety

Analysis never throws on text: lone surrogates, control characters and megabyte runs of one character are valid input (an analysis fuzz target covers them). Term length is capped by the tokenizer (255 by default); the keyword analyzer is the one exception and emits the text as given, so a field that uses it is limited by the index's term-length limit. Working buffers grow to the longest term seen and stay.
