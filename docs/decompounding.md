# Decompounding

German, Dutch, the Scandinavian languages, Finnish and Icelandic write compounds as one word ("Haustür", "kirjakauppa"). A search for
"Tür" should find "Haustür", and one for "Haustür" should find "Tür". `CompoundSplitter` cuts a compound into the words it is made of, and
`DecompoundFilter` indexes the compound and its parts at one position, so either form matches.

## Method

Corpus-statistics splitting after Koehn and Knight, "Empirical Methods for Compound Splitting" (EACL 2003), section 3:

1. A word list with counts (`CompoundVocabulary`). A plain list is accepted too (every count 1).
2. Every way to cover the word with list words is a candidate. Parts may be joined by a linking element of the language ("Liebe-s-brief",
   `CompoundOptions.LinkingElements`); the linking element belongs to neither part.
3. Among candidates with the fewest parts (at most `MaxParts`, default 4), the one with the best geometric mean of the parts' counts wins.
   Preferring fewer parts is a deviation from the paper (which maximises the geometric mean over all part counts): on our samples the plain
   geometric mean cuts words into short, very frequent fragments ("mittagessen" into "mit", "tag", "essen"; "sommerferie" into
   "som", "mer", "ferie"). `PreferFewerParts = false` restores the paper's rule.
4. With counts, a word is not split when its own count is at least the geometric mean of the parts' counts (the paper's rule: a frequent
   compound is a word in its own right). This also means that frequent compounds ("Kühlschrank") are not split.
5. Parts have at least 3 characters (`MinPartLength`); words shorter than 6 are not tried (`MinWordLength`).

The splitter does not repair stems (it does not know that "skol-" is "skola" or that Icelandic "bóka-" is "bók"); a compound whose first
part is such a form is left whole. The filter never drops the compound: a wrong split adds a few extra terms and does not lose the original.

## Use

```csharp
CompoundSplitter splitter = CompoundSplitter.ForLanguage("de");          // packed list and the language's linking elements
var parts = new List<(int Start, int Length)>();
bool split = splitter.TrySplit("Haustür", parts);                        // parts: "Haus", "tür"

var analyzer = new LanguageAnalyzer("de", keepUnstemmed: false, decompound: true);   // registered as "de-compound"
```

The registered names are `<code>-compound` and `<code>-compound-both` for `de`, `nl`, `sv`, `da`, `no`, `fi` and `is`. The chain is the
language analyzer's with the decompounder after case folding, so each part is stop-filtered and stemmed like any other word. Lists of
your own load with `CompoundVocabulary.Read` (`word` or `word TAB count` per line).

## Packed word lists

Built by `tools/decompounding/fetch.sh` and `tools/decompounding/build_lists.py`, embedded in `RecursiveAI.OneIndex` as gzip text
(the build without them still compiles; `CompoundVocabulary.ForLanguage` then reports the missing list).

| Language | Source | Licence | Words | Packed size |
|---|---|---|---|---|
| de | Leipzig Corpora Collection, German news 2024, 300K sentences | CC BY 4.0 | 69,346 | 326,516 B |
| sv | Leipzig, Swedish news 2023 | CC BY 4.0 | 62,652 | 291,204 B |
| da | Leipzig, Danish news 2022 | CC BY 4.0 | 55,227 | 259,160 B |
| no | Leipzig, Norwegian news 2023 | CC BY 4.0 | 57,288 | 263,959 B |
| fi | Leipzig, Finnish news 2022 | CC BY 4.0 | 97,120 | 437,407 B |
| is | Leipzig, Icelandic news crawl 2019 | CC BY 4.0 | 74,178 | 348,348 B |
| nl | OpenTaal word list 2.20.23 with counts from the Leipzig Dutch news 2023 corpus | Revised BSD (OpenTaal's choice of Revised BSD and/or CC BY 3.0); CC BY 4.0 | 391,648 | 1,413,732 B |

Total 3.3 MB. Leipzig lists keep words of 3 to 40 letters seen at least 3 times in the 300,000 sentences, case folded (the lists are
frequency lists of running text, so they contain names, some typos and inflected forms; the counts are what lets the splitter prefer
real words). The licence of the downloadable Leipzig corpora is stated on https://wortschatz-leipzig.de/en/usage ("The text corpora offered for
download are made available under the Creative Commons licence CC BY"); attribution is in `THIRD-PARTY-NOTICES.md` and the package readme.
Wikipedia-derived corpora (CC BY-SA) are not used.

## Evaluation

No gold-standard splitting set with a licence that allows this use was found (GermaNet's splits and similar sets are restricted), so
the evaluation uses small hand-written samples committed in `testdata/decompounding/<language>.tsv`: 26 to 40 compounds with accepted
splits and 12 to 20 plain words that must stay whole per language. They were written for this purpose, are not tuned on, and are
small: read the numbers as a smoke measurement, not a benchmark. Numbers are in
`DecompoundingTests.PackedListsSplitTheSample`, which fails below floors set under them.

| Language | Compounds | Split | Exact (accepted split) | Plain words | Falsely split |
|---|---|---|---|---|---|
| de | 40 | 35 (0.875) | 33 (0.825) | 20 | 0 |
| nl | 29 | 28 (0.966) | 27 (0.931) | 15 | 3 (0.200) |
| sv | 26 | 22 (0.846) | 22 (0.846) | 14 | 0 |
| da | 26 | 23 (0.885) | 23 (0.885) | 13 | 1 (0.077) |
| no | 26 | 24 (0.923) | 22 (0.846) | 12 | 1 (0.083) |
| fi | 27 | 24 (0.889) | 24 (0.889) | 14 | 0 |
| is | 26 | 20 (0.769) | 16 (0.615) | 12 | 1 (0.083) |

Misses fall into three groups: frequent compounds kept whole by the frequency rule (Kühlschrank, Flughafen, Wohnzimmer, Lufthavn), first
parts that are not words as written (Swedish skol-bok, Icelandic bóka-búð, Finnish consonant gradation), and wrong cuts into short
fragments (eisenbahn into "eis" and "bahn"; the Dutch plain words vriendelijk, aardappel and venster, and the Danish and Norwegian
"frihed"/"frihet" are split because the suffix or a short piece is itself a frequent word).

### Settling the defaults

On the seven samples together (mean over languages; lists with the counts of the 300K-sentence corpora):

| MinPartLength | Fewest parts | Frequency rule | Count floor | Split | Exact | False split |
|---|---|---|---|---|---|---|
| 3 | no | off | 1 | 0.918 | 0.820 | 0.167 |
| 3 | no | on | 1 | 0.884 | 0.794 | 0.063 |
| **3** | **yes** | **on** | **1** | **0.879** | **0.834** | **0.063** |
| 4 | yes | on | 1 | 0.627 | 0.602 | 0.019 |
| 3 | yes | on | 8 | 0.794 | 0.758 | 0.051 |

Part length 4 loses a third of the compounds (tür, hof, bus, rad ...). A count floor of 8 (the lists already hold words seen at least 3 times) loses coverage and gains little. Requiring more
occurrences for 3-letter parts trades recall for false splits one for one. The bold row is the default (the packed lists keep words seen at least 3 times).

## Not included

Hunspell dictionaries (mostly GPL, LGPL or MPL) are not used. A trained splitter, truncation or umlaut repair, and per-language part-of-speech
checks are not implemented. The other languages with compounding (Afrikaans, Hungarian, Estonian ...) have no packed list; load one with
`CompoundVocabulary.Read` and use `CompoundOptions` directly.
