# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text, vector and hybrid search in one in-process library, with no native code and no server.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

**Pre-alpha.** This preview contains the storage layer, the commit format and protocol, and the contracts for the engine; search itself is not built yet.

- Repository and documentation: https://github.com/recursive-ai-oss/oneindex-csharp
- Project page: https://recursiveai.org/oneindex/
- Licence: MIT OR Apache-2.0

## Model provenance (`RecursiveAI.OneIndex.Models`)

- Dense embedder: BAAI/bge-small-en-v1.5, MIT licence.
- Learned sparse encoder: opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill, Apache-2.0 licence. We did not train it. Its model card states these training sets: MS MARCO, eli5_question_answer, squad_pairs, WikiAnswers, yahoo_answers_title_question, gooaq_pairs, stackexchange_duplicate_questions_body_body, wikihow, S2ORC_title_abstract, stackexchange_duplicate_questions_title-body_title-body, yahoo_answers_question_answer, searchQA_top5_snippets, stackexchange_duplicate_questions_title_title, yahoo_answers_title_answer, fever, fiqa, hotpotqa, nfcorpus and scifact. The weights are distributed under the model's Apache-2.0 licence; check the terms of those sets if your use needs them. Weights can also be supplied from files (`NeuralSparseEncoder`, `OpenSearchSparseEncoder.Open`).

The licence texts and sources are in THIRD-PARTY-NOTICES.md.
