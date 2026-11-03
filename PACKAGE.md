# OneIndex

**An embedded search engine for .NET: SQLite for search.** Full-text, vector and hybrid search in one in-process library, with no native code and no server.

OneIndex is unaffiliated with other products named OneIndex, OneSearch or similar.

**Preview.** Full-text search (BM25F, phrase, prefix, fuzzy, typo tolerance, as-you-type), filters, facets, aggregations, geo, sorting, LINQ, user-text and query-JSON surfaces, and the first vector and hybrid search features. APIs and file formats may change between previews. What works, the measured results and the known gaps: https://github.com/recursive-ai-oss/oneindex-csharp#what-exists

## Bundled models (`RecursiveAI.OneIndex.Models`)

The models package carries third-party model weights under their own permissive licences. This project did not train them, and each one can be replaced with weights of your choosing loaded from a path.

- **BAAI/bge-small-en-v1.5** (default English embedder). MIT. Its authors state that the English models were trained first on unlabeled data including Wikipedia, CC-net, StackExchange, Reddit, S2ORC and the sentence-transformers training data, then on labeled NLI, FEVER, NQ, HotpotQA, Quora, StackExchange Duplicates and MEDI data (Xiao et al., "C-Pack", arXiv:2309.07597). Details and licence texts: THIRD-PARTY-NOTICES.md in the repository.
- **opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill** (default learned sparse encoder). Apache-2.0. Its model card states these training sets: MS MARCO, eli5_question_answer, squad_pairs, WikiAnswers, yahoo_answers_title_question, gooaq_pairs, stackexchange_duplicate_questions_body_body, wikihow, S2ORC_title_abstract, stackexchange_duplicate_questions_title-body_title-body, yahoo_answers_question_answer, searchQA_top5_snippets, stackexchange_duplicate_questions_title_title, yahoo_answers_title_answer, fever, fiqa, hotpotqa, nfcorpus and scifact. The weights are distributed under the model's Apache-2.0 licence; check the terms of those sets if your use needs them. Weights can also be supplied from files (`NeuralSparseEncoder`, `OpenSearchSparseEncoder.Open`).
- **cross-encoder/ms-marco-MiniLM-L6-v2** (default reranker). Apache-2.0. Its model card states that it was trained on the MS MARCO passage ranking set; the data is not included and its terms are the data owner's, not conferred on the weights by this project.

## Bundled language data

- **`RecursiveAI.OneIndex.Models.Multilingual`** carries **intfloat/multilingual-e5-small** (multilingual embedder, 384 dimensions, int8, about 120 MB). MIT. Its authors state a two-stage training on about one billion weakly supervised pairs and then labeled data, among them MS MARCO, NQ and Mr. TyDi / MIRACL (arXiv:2402.05672); the weights are governed by their MIT licence, the data terms are the data owners' and are not conferred by this project. A path-loaded model works too (`MultilingualE5Embedder.Open`).
- **`RecursiveAI.OneIndex.Analysis.Asian`** packs segmentation dictionaries: IPADIC (Japanese; the NAIST and ICOT permission notice), mecab-ko-dic (Korean; Apache-2.0; its costs were trained on the Sejong corpus according to its README), the jieba word list (Chinese; MIT) and the ICU Thai, Lao, Khmer and Burmese word lists (Unicode License v3, with the Lao and Burmese authors' BSD notices).
- **`RecursiveAI.OneIndex`** packs decompounding word lists for German, Swedish, Danish, Norwegian, Finnish and Icelandic from the Leipzig Corpora Collection, Wortschatz Leipzig, University of Leipzig (https://wortschatz-leipzig.de, CC BY 4.0; Goldhahn, Eckart and Quasthoff, LREC 2012), and the OpenTaal Dutch word list (Revised BSD; https://github.com/OpenTaal/opentaal-wordlist) with Leipzig counts.

- Repository and documentation: https://github.com/recursive-ai-oss/oneindex-csharp
- Project page: https://recursiveai.org/oneindex/
- Licence: MIT OR Apache-2.0

The licence texts and sources are in THIRD-PARTY-NOTICES.md.
