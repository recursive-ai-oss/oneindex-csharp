# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text, vector and hybrid search in one in-process library, with no native code and no server.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

**Pre-alpha.** This preview contains the storage layer, the commit format and protocol, and the contracts for the engine; search itself is not built yet.

- Repository and documentation: https://github.com/recursive-ai-oss/oneindex-csharp
- Project page: https://recursiveai.org/oneindex/
- Licence: MIT OR Apache-2.0

## Bundled models (`RecursiveAI.OneIndex.Models`)

The models package carries two third-party models. We did not train either; the weights are redistributed as published, converted to the OneModel format and
quantized to int8. Each is governed by its weight licence, and its notice is packed under `models/`. Both can be replaced: `BgeSmallEmbedder.Open` and
`MiniLmReranker.Open` load weights from files, and `IEmbedder` / `IReranker` accept any implementation.

| Model | Role | Weight licence | Training data as the authors state it |
|---|---|---|---|
| BAAI/bge-small-en-v1.5 | default dense embedder | MIT | Not listed on the model card, which points to the released BGE training data (BAAI-MTP) |
| cross-encoder/ms-marco-MiniLM-L6-v2 | default reranker | Apache-2.0 | The MS MARCO passage ranking set. The data is not included; its terms are the data owner's |

See `THIRD-PARTY-NOTICES.md` for sources and revisions.
