# Asian-language analysis (`RecursiveAI.OneIndex.Analysis.Asian`)

Japanese, Chinese, Korean, Thai, Lao, Khmer and Myanmar write words without spaces (Korean with spaces, but with agglutinated endings).
The package segments them with packed dictionaries and plugs into the analyzer pipeline of core (`docs/analysis.md`). It references
only core and the BCL.

```csharp
AsianAnalyzers.Register();                       // once at start-up: makes "ja", "zh", "ko", "th", "lo", "km", "my" known by name
[FullText(Analyzer = "ja")] public string Body { get; set; }
```

Registration is explicit (`AnalyzerRegistry.Register(name, factory)`, added to core for this package); nothing is discovered by reflection.
A schema stores the analyzer name, so an index opens only after the call. The analyzers are `Analyzer` subclasses (`JapaneseAnalyzer`,
`ChineseAnalyzer`, `KoreanAnalyzer`, `WordBreakAnalyzer.Thai()` ...); each is its tokenizer, NFKC and case folding.

Common to every tokenizer: the text is cut into spans of the target script, which a dictionary segmenter splits; everything else
(Latin words, digits, other scripts) goes to the UAX #29 word tokenizer, so "iPhone 15" inside Japanese text stays whole. Tokens keep
the offsets of the original text. Function words that are dropped leave a position gap, so phrase queries do not match across them.

## Dictionaries and sizes

Dictionaries are data in one container format, `OIDICT` version 1 (magic, version, sections, CRC-32C footer, `DictionaryLimits` on
every length; malformed files raise `DictionaryFormatException`, `TryLoad` beside it). They are built by `tools/dictionaries/fetch.sh`
and `build.sh` from the sources below and embedded when present at build time. Any dictionary can also be loaded from a file
(`LatticeLexicon.Load`, `WordList.Load`).

| Language | Segmenter | Dictionary | Licence | Packed size |
|---|---|---|---|---|
| ja | lattice search (`LatticeSegmenter`), search mode | IPADIC 2.7.0-20070801 (392,126 words, connection matrix 1316 x 1316) | NAIST/ICOT permissive custom licence, notice packed | 4,186,442 B |
| ko | lattice search, normal mode | mecab-ko-dic 2.1.1-20180720 (501,293 words after dropping the person, actor and address name classes; matrix 3822 x 2693) | Apache-2.0 | 7,242,507 B |
| zh | most probable path (`UnigramSegmenter`), search mode | jieba `dict.txt` word frequencies | MIT | 2,023,493 B |
| th | maximal matching (`MaximalMatchSegmenter`) | ICU `thaidict.txt` | Unicode License v3 | 133,499 B |
| lo | maximal matching | ICU `laodict.txt` | Unicode License v3, with BSD-2 text | 162,031 B |
| km | maximal matching | ICU `khmerdict.txt` | Unicode License v3 | 449,281 B |
| my | maximal matching | ICU `burmesedict.txt` | Unicode License v3, with BSD-2 text | 227,505 B |

Total about 14.4 MB (under the 50 MB at which a separate package would be proposed). Notices are in `THIRD-PARTY-NOTICES.md`.

## Segmenters

- **Lattice (ja, ko):** every dictionary word starting at each position is a node, unknown-word nodes come from the character classes of
  the dictionary, and the path with the lowest sum of word costs and connection costs (left/right context ids) is found by Viterbi
  search, as in Kudo, Yamamoto and Matsumoto, "Applying Conditional Random Fields to Japanese Morphological Analysis" (EMNLP 2004).
  The dictionaries' costs are used as published. Japanese search mode adds a length penalty to long Han and Katakana words (after the
  published description of search-mode segmentation: threshold 2 and penalty 3000 for Han, threshold 7 and penalty 10000 for Katakana
  [Agent default, untuned beyond the F1 check below]) so compounds are split. Japanese tokens are the base form of inflected words;
  particles, auxiliary verbs and symbols are dropped (part-of-speech prefixes 助詞, 助動詞, 記号). Korean yields the content morphemes (a
  verb's stem, not its endings) and a compound noun is the compound followed by its parts at the same position; the classes dropped are
  the endings, particles, suffixes and symbols (tags beginning with E, J, SC, SE, SF, SP, SSO, SSC, SY, VCP, VCN, XPN, XSA, XSN, XSV).
- **Unigram (zh):** the segmentation maximising the product of word probabilities from the frequency list (dynamic programming over the
  word graph); unknown characters stand alone (single characters). Search mode also emits the dictionary parts of long words at the same
  position.
- **Maximal matching (th, lo, km, my):** dynamic programming from the end of the text picks the path with the fewest characters left
  unmatched, then the fewest words, then the longer first word. A boundary never falls before a combining mark or a spacing vowel sign,
  and unmatched characters are grouped into one token.

## Evaluation

Word-span precision, recall and F1 (a predicted word counts when its span equals a gold span) against Universal Dependencies
treebanks, which are evaluation data only: fetched on demand, never committed, CC BY-SA licensed (Thai-PUD: CC BY-SA 3.0). Run with
`oneindex-dictionaries eval <lang> <dictionary> <treebank.conllu>`.

| Language | Treebank (test set) | Sentences / gold words | Precision | Recall | F1 |
|---|---|---|---|---|---|
| ja, normal mode | UD Japanese-GSD | 543 / 13,034 | 0.9375 | 0.9071 | 0.9221 |
| ja, search mode | UD Japanese-GSD | 543 / 13,034 | 0.9387 | 0.9138 | 0.9261 |
| zh | UD Chinese-GSDSimp | 500 / 12,010 | 0.8120 | 0.7722 | 0.7916 |
| th | UD Thai-PUD | 1,000 / 22,330 | 0.7097 | 0.8175 | 0.7598 |
| ko, lo, km, my | none admissible | | | | |

Differences to explain:

- **Japanese:** the treebank follows UniDic short-unit words and IPADIC's units are longer (compound nouns, noun plus suffix), so some
  of the gap is the two standards disagreeing, not errors.
- **Chinese:** jieba's list has longer words than the treebank (企业界, 博物馆), so recall is lower than precision. The traditional-character
  treebank (Chinese-GSD) scores F1 0.5747 because the list is simplified-Chinese; traditional text is not covered by this dictionary.
- **Thai:** the segmenter predicts more words than the treebank has (precision below recall): the ICU list is a break dictionary with many
  short entries, so names and compounds the treebank keeps whole are cut. It is not a gold standard of words.
- **Korean:** the Korean treebank's tokens are eojeol (space-delimited units), not morphemes, so it cannot score a morphological
  segmenter; no morpheme-level gold with a usable licence was found. Korean is checked by tests on stems and compounds (for example the
  inflected 갔습니다 gives the stem 가). **Lao, Khmer and Myanmar:** no gold-segmented set was found. The
  shared segmenter is unit-tested on a synthetic list (and shared with Thai, which is scored above); the three packed analyzers get a smoke
  test (no tokens from blank text) and no accuracy figure. Treat them as dictionary breaking, not as evaluated segmentation.

## Limits and safety

Dictionary readers validate the magic, version, every section length and the checksum before using an offset; sizes and counts are
bounded by `DictionaryLimits`; a hostile file raises `DictionaryFormatException`. The `dictionary` and `asian` fuzz targets cover the
readers, the segmenters and the analyzers (docs in `fuzz/README.md`). Segmenters allocate per call only their lattice buffers, reused by the
tokenizer between texts. The package works in the browser: nothing in it depends on threads, native code or reflection.

## Not included

Readings (furigana), UniDic's short-unit and long-unit forms, user dictionaries (load another `.oidict` for that), traditional-Chinese
words, Cantonese, Vietnamese (written with spaces) and language detection (the schema names the analyzer).
