# Analysis contract

An `Analyzer` turns text into terms. It is a tokenizer followed by filters, built once and reused.

## Token

`TokenSource` is a pull source: `Reset(text)`, then `MoveNext()` until it returns false. One mutable `Token` object is shared by the whole chain; a tokenizer fills it, each filter edits it in place or drops it. Once the term buffer has grown to the longest term, a chain allocates nothing per token (the tests measure this).

- **Term text** is UTF-16. .NET strings are UTF-16, so offsets index the original string directly (highlighting, snippets), case and accent folding edit characters in place, and the single conversion to UTF-8 happens where a term enters the term dictionary.
- **Offsets** are `[StartOffset, EndOffset)` in characters of the original text.
- **Positions**: the first token's position is its increment minus 1, so an increment of 1 gives position 0. An increment of 0 stacks a token on the previous position (synonyms, a stemmed twin). A filter that removes a token adds its increment to the next token's, so a phrase across a removed stop word keeps its gap.
- **Type** classifies a token (word, number, ideographic, synonym); **marks** carry flags such as *keyword* (do not stem).

## Analyzer and registry

An analyzer instance is not thread-safe; each thread asks `AnalyzerRegistry` for its own. The name is what a schema stores. The registry is a plain `switch` in code, not discovery by reflection. Two analyzers exist so far: `whitespace-lowercase` (a reference implementation for tests) and `keyword` (the whole text as one term). The default analyzer name for text fields, `standard`, is reserved for the Unicode-aware analyzer that follows.
