# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text, vector and hybrid search in one in-process library, with no native code and no server.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

**Preview.** Full-text search (BM25F, phrase, prefix, fuzzy, typo tolerance, as-you-type), filters, facets, aggregations, geo, sorting, LINQ, user-text and query-JSON surfaces, and the first vector and hybrid search features. APIs and file formats may change between previews. What works, the measured results and the known gaps: https://github.com/recursive-ai-oss/oneindex-csharp#what-exists

## Bundled models (`RecursiveAI.OneIndex.Models`)

The models package carries third-party model weights under their own permissive licences. This project did not train them, and each one can be replaced with weights of your choosing loaded from a path.

- **BAAI/bge-small-en-v1.5** (default English embedder). MIT. Its authors state that the English models were trained first on unlabeled data including Wikipedia, CC-net, StackExchange, Reddit, S2ORC and the sentence-transformers training data, then on labeled NLI, FEVER, NQ, HotpotQA, Quora, StackExchange Duplicates and MEDI data (Xiao et al., "C-Pack", arXiv:2309.07597). Details and licence texts: THIRD-PARTY-NOTICES.md in the repository.

- Repository and documentation: https://github.com/recursive-ai-oss/oneindex-csharp
- Project page: https://recursiveai.org/oneindex/
- Licence: MIT OR Apache-2.0
