# Third-party notices

OneIndex's own code is licensed `MIT OR Apache-2.0`. Material from other projects that ships with a package, or is used by the tests, is listed here with its licence.

The sections below are filled in as each kind of material enters the repository.

## Unicode Character Database

The tables in `src/OneIndex/Analysis/Unicode/UnicodeTables.g.cs` (word and sentence break properties, Extended_Pictographic, general categories, scripts, case folding, decompositions, combining classes and composition pairs) are generated from the Unicode Character Database 17.0.0, and `testdata/ucd/` holds two of its conformance files unchanged. The Unicode data files are distributed under the Unicode License v3:

    COPYRIGHT AND PERMISSION NOTICE

    Copyright © 1991-2026 Unicode, Inc.

    NOTICE TO USER: Carefully read the following legal agreement. BY
    DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA FILES, AND/OR
    SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE
    TERMS AND CONDITIONS OF THIS AGREEMENT. IF YOU DO NOT AGREE, DO NOT
    DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

    Permission is hereby granted, free of charge, to any person obtaining a
    copy of data files and any associated documentation (the "Data Files") or
    software and any associated documentation (the "Software") to deal in the
    Data Files or Software without restriction, including without limitation
    the rights to use, copy, modify, merge, publish, distribute, and/or sell
    copies of the Data Files or Software, and to permit persons to whom the
    Data Files or Software are furnished to do so, provided that either (a)
    this copyright and permission notice appear with all copies of the Data
    Files or Software, or (b) this copyright and permission notice appear in
    associated Documentation.

    THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY
    KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF
    THIRD PARTY RIGHTS.

    IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE
    BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT OR CONSEQUENTIAL DAMAGES,
    OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
    WHETHER IN AN ACTION OF CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION,
    ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA
    FILES OR SOFTWARE.

    Except as contained in this notice, the name of a copyright holder shall
    not be used in advertising or otherwise to promote the sale, use or other
    dealings in these Data Files or Software without prior written
    authorization of the copyright holder.

## Snowball stemming algorithms

The stemmers in `src/OneIndex/Analysis/Snowball/Languages/` are generated, by a compiler written for this repository, from the Snowball algorithm scripts (`tools/SnowballGen/algorithms/*.sbl`, Snowball release v3.1.1). The English stemmer is the Porter2 algorithm. The stop word lists in `StopWordData.g.cs` are the lists published on the Snowball website. Both are covered by the Snowball project's BSD 3-clause licence (<https://snowballstem.org/license.html>), whose text for the distribution is:

    Copyright (c) 2001, Dr Martin Porter
    Copyright (c) 2004,2005, Richard Boulton
    Copyright (c) 2013, Yoshiki Shibukawa
    Copyright (c) 2006-2025, Olly Betts
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions
    are met:

      1. Redistributions of source code must retain the above copyright notice,
         this list of conditions and the following disclaimer.
      2. Redistributions in binary form must reproduce the above copyright notice,
         this list of conditions and the following disclaimer in the documentation
         and/or other materials provided with the distribution.
      3. Neither the name of the Snowball project nor the names of its contributors
         may be used to endorse or promote products derived from this software
         without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
    ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
    WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
    ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
    (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
    ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
    SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The tests also read the Snowball project's published vocabularies (snowball-data). They are fetched into a cache and are not part of the repository or any package; their licences are per language directory (BSD 3-clause for the original lists, CC BY-SA for lists derived from Wikipedia, and GPL-3.0 for the Arabic list).

## Segmentation dictionaries and word lists

Stop words: see the Snowball section above. The packages `RecursiveAI.OneIndex.Analysis.Asian` and `RecursiveAI.OneIndex` pack the data below, converted to compact binary forms by the scripts under `tools/dictionaries` and `tools/decompounding`. The words and statistics are used unchanged apart from format conversion and filtering (the filters are described in `docs/analysis-asian.md` and `docs/decompounding.md`). OneIndex did not create or train any of them.

### IPADIC 2.7.0-20070801 (Japanese; package `RecursiveAI.OneIndex.Analysis.Asian`)

Source: https://sourceforge.net/projects/mecab/files/mecab-ipadic/ (the NAIST dictionary distributed with MeCab). Licence: the NAIST and ICOT permission notice, reproduced here as it requires:

    Copyright 2000, 2001, 2002, 2003 Nara Institute of Science
    and Technology.  All Rights Reserved.

    Use, reproduction, and distribution of this software is permitted.
    Any copy of this software, whether in its original form or modified,
    must include both the above copyright notice and the following
    paragraphs.

    Nara Institute of Science and Technology (NAIST),
    the copyright holders, disclaims all warranties with regard to this
    software, including all implied warranties of merchantability and
    fitness, in no event shall NAIST be liable for
    any special, indirect or consequential damages or any damages
    whatsoever resulting from loss of use, data or profits, whether in an
    action of contract, negligence or other tortuous action, arising out
    of or in connection with the use or performance of this software.

    A large portion of the dictionary entries
    originate from ICOT Free Software.  The following conditions for ICOT
    Free Software applies to the current dictionary as well.

    Each User may also freely distribute the Program, whether in its
    original form or modified, to any third party or parties, PROVIDED
    that the provisions of Section 3 ("NO WARRANTY") will ALWAYS appear
    on, or be attached to, the Program, which is distributed substantially
    in the same form as set out herein and that such intended
    distribution, if actually made, will neither violate or otherwise
    contravene any of the laws and regulations of the countries having
    jurisdiction over the User or the intended distribution itself.

    NO WARRANTY

    The program was produced on an experimental basis in the course of the
    research and development conducted during the project and is provided
    to users as so produced on an experimental basis.  Accordingly, the
    program is provided without any warranty whatsoever, whether express,
    implied, statutory or otherwise.  The term "warranty" used herein
    includes, but is not limited to, any warranty of the quality,
    performance, merchantability and fitness for a particular purpose of
    the program and the nonexistence of any infringement or violation of
    any right of any third party.

    Each user of the program will agree and understand, and be deemed to
    have agreed and understood, that there is no warranty whatsoever for
    the program and, accordingly, the entire risk arising from or
    otherwise connected with the program is assumed by the user.

    Therefore, neither ICOT, the copyright holder, or any other
    organization that participated in or was otherwise related to the
    development of the program and their respective officials, directors,
    officers and other employees shall be held liable for any and all
    damages, including, without limitation, general, special, incidental
    and consequential damages, arising out of or otherwise in connection
    with the use or inability to use the program or any product, material
    or result produced or otherwise obtained by using the program,
    regardless of whether they have been advised of, or otherwise had
    knowledge of, the possibility of such damages at any time during the
    project or thereafter.  Each user will be deemed to have agreed to the
    foregoing by his or her commencement of use of the program.  The term
    "use" as used herein includes, but is not limited to, the use,
    modification, copying and distribution of the program and the
    production of secondary products from the program.

    In the case where the program, whether in its original form or
    modified, was distributed or delivered to or received by a user from
    any person, organization or entity other than ICOT, unless it makes or
    grants independently of ICOT any specific warranty to the user in
    writing, such person, organization or entity, will also be exempted
    from and not be held liable to the user for any such damages as noted
    above as far as the program is concerned.

### mecab-ko-dic 2.1.1-20180720 (Korean; package `RecursiveAI.OneIndex.Analysis.Asian`)

Source: https://bitbucket.org/eunjeon/mecab-ko-dic. Licence: Apache License 2.0 (the `COPYING` file of the release; the text is the same as `LICENSE-APACHE` in this repository), and the release's README states that the software may be used and redistributed under it. The person-name, actor-name and address-name entry classes are not packed. Provenance: its README says the dictionary's costs were trained on the 21st Century Sejong corpus; the terms of that corpus are not conferred or checked here.

### jieba dictionary (Chinese; package `RecursiveAI.OneIndex.Analysis.Asian`)

Source: https://github.com/fxsjy/jieba, `jieba/dict.txt` (word frequencies). Licence: MIT, as stated for the repository:

    The MIT License (MIT)

    Copyright (c) 2013 Sun Junyi

    Permission is hereby granted, free of charge, to any person obtaining a copy of
    this software and associated documentation files (the "Software"), to deal in
    the Software without restriction, including without limitation the rights to
    use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
    the Software, and to permit persons to whom the Software is furnished to do so,
    subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all
    copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
    FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
    COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
    IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
    CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Provenance note: the project does not state where the frequencies in `dict.txt` come from.

### ICU dictionaries (Thai, Lao, Khmer, Myanmar; package `RecursiveAI.OneIndex.Analysis.Asian`)

Source: ICU release-78.3, `icu4c/source/data/brkitr/dictionaries/` (`thaidict.txt`, `laodict.txt`, `khmerdict.txt`, `burmesedict.txt`), https://github.com/unicode-org/icu. Each file's header says: "License & terms of use: http://www.unicode.org/copyright.html", that is the Unicode License v3 reproduced in the Unicode Character Database section above (Copyright (C) 2016 and later Unicode, Inc. and others; Copyright (c) International Business Machines Corporation and others). The Lao and Burmese lists carry additional notices of their authors:

Lao word list header:

    Copyright (C) 2016 and later: Unicode, Inc. and others.
    License & terms of use: http://www.unicode.org/copyright.html
    Copyright (c) 2015 International Business Machines Corporation
    and others. All Rights Reserved.

    Lao Word List from Brian Eugene Wilson, Robert Martin Campbell;
    with special thanks to Erik Mundall, Arlyta Keosamone, and Bualong Nyoukmai.

    This word list was created in order to aid in word wrapping issues.

    Project:    https://github.com/rober42539/lao-dictionary
    Dictionary: https://github.com/rober42539/lao-dictionary/laodict.txt
    License:    https://github.com/rober42539/lao-dictionary/LICENSE.txt
    Readme:     https://github.com/rober42539/lao-dictionary/README.md

    This file is derived from the above dictionary version of Nov 22, 2020
      @4aa4567ebc3f86a09fbc5a6948ea05ea411707e1.
    Modified the header.
    --------------------------------------------------------------------------------
    Copyright (C) 2013 Brian Eugene Wilson, Robert Martin Campbell.
    All rights reserved.

    Redistribution and use in source and binary forms, with or without modification,
    are permitted provided that the following conditions are met:

    	Redistributions of source code must retain the above copyright notice, this
    	list of conditions and the following disclaimer. Redistributions in binary
    	form must reproduce the above copyright notice, this list of conditions and
    	the following disclaimer in the documentation and/or other materials
    	provided with the distribution.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
    ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
    WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
    ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
    (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
    ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
    SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
    --------------------------------------------------------------------------------

Burmese word list header:

    Copyright (C) 2016 and later: Unicode, Inc. and others.
    License & terms of use: http://www.unicode.org/copyright.html
    Copyright (c) 2015 International Business Machines Corporation
    and others. All Rights Reserved.

    burmese-word-list.txt from LeRoy Benjamin Sharon,
    with special thanks to Robert Martin Campbell.

    This list was created to aid in word wrapping, word segmentation, and spell
    checking. It aims to be an exhaustive list of all words in the Burmese language.

    Sources include:
    - The my-l10n project, hosted at https://code.google.com/p/my-l10n/
    - http://sourceforge.net/projects/ncsoft/files/Myanmar-English%20Dictionary/

    This list is part of a project hosted at:
      github.com/kanyawtech/myanmar-karen-word-lists

    Also for more information and additional projects, see:
      kanyawtech.org

    This file is derived from the above dictionary, but:
    modified header; deleted parenthetical annotations and final space; deleted
    duplicate entries; deleted entries with medial or final hyphens, entries
    containing text in Latin/Arabic/NKo script, entries containing medial space,
    equals sign, comma, underscore, colon, exclamation point, semicolon, ellipsis,
    asterisk, solidus, space, or
    \u0361, \u0fff, \u0004, \u0001, \u200b, \u05A1, \u001E, etc.
    --------------------------------------------------------------------------------
    Copyright (c) 2013, LeRoy Benjamin Sharon
    All rights reserved.

    Redistribution and use in source and binary forms, with or without modification,
    are permitted provided that the following conditions are met:

      Redistributions of source code must retain the above copyright notice, this
      list of conditions and the following disclaimer.

      Redistributions in binary form must reproduce the above copyright notice, this
      list of conditions and the following disclaimer in the documentation and/or
      other materials provided with the distribution.

      Neither the name Myanmar Karen Word Lists, nor the names of its
      contributors may be used to endorse or promote products derived from
      this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
    ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED

### Decompounding word lists (package `RecursiveAI.OneIndex`)

**Leipzig Corpora Collection** (German, Swedish, Danish, Norwegian, Finnish and Icelandic lists; the Dutch list takes its counts from the same source). Word counts are taken from the word lists of these downloadable news corpora of the Wortschatz Leipzig project, University of Leipzig: deu_news_2024_300K, swe_news_2023_300K, dan_news_2022_300K, nor_news_2023_300K, fin_news_2022_300K, isl_newscrawl_2019_300K and nld_news_2023_300K, from https://wortschatz-leipzig.de/en/download. Licence: Creative Commons Attribution 4.0 International (CC BY 4.0, https://creativecommons.org/licenses/by/4.0/); the project's terms of usage (https://wortschatz-leipzig.de/en/usage) state: "The text corpora offered for download are made available under the Creative Commons licence CC BY". Attribution: D. Goldhahn, T. Eckart & U. Quasthoff, "Building Large Monolingual Dictionaries at the Leipzig Corpora Collection: From 100 to 200 Languages", Proceedings of the 8th International Language Resources and Evaluation Conference (LREC 2012). Changes: the lists are reduced to case-folded words of letters seen at least three times, and stored as word and count lines.

**OpenTaal Dutch word list 2.20.23** (https://github.com/OpenTaal/opentaal-wordlist, revision b250510dda431785f962019167d1415198ff3905): the Dutch vocabulary, used under the Revised BSD License, one of the two licences the project offers ("Revised BSD License and/or CC BY 3.0"). Copyright: (c) 2020 OpenTaal (Simon Brouwer, Sander van Geloven); (c) 2006-2011 OpenTaal (Ruud Baars, Simon Brouwer); (c) 2001-2005 Simon Brouwer and others; (c) 1996 Nederlandstalige TeX Gebruikersgroep.

    # Revised BSD License (3-clause license)

    Copyright (c) 2020, OpenTaal
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

    * Redistributions of source code must retain the above copyright notice, this
      list of conditions and the following disclaimer.

    * Redistributions in binary form must reproduce the above copyright notice,
      this list of conditions and the following disclaimer in the documentation
      and/or other materials provided with the distribution.

    * Neither the name of the copyright holder nor the names of its
      contributors may be used to endorse or promote products derived from
      this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
    AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
    IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
    FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
    DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
    CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
    OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Datasets used by tests and benchmarks

None yet. Datasets are fetched by scripts into a local cache and are never committed.

## Model weights

### BAAI/bge-small-en-v1.5 (package `RecursiveAI.OneIndex.Models`)

The default English embedder packs the weights and vocabulary of BAAI/bge-small-en-v1.5 (Beijing Academy of Artificial Intelligence),
converted to the OneModel file format with the matrix-multiply weights and the word-embedding table stored as 8-bit integers.

- Source: https://huggingface.co/BAAI/bge-small-en-v1.5, revision 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a
- Licence: MIT (the model card's metadata states `license: mit`, and its License section: "The released models can be used for commercial
  purposes free of charge.")
- The licence text packed with the weights is `models/bge-small-en-v1.5/LICENSE-bge-small-en-v1.5.txt` in the package.
- Provenance: this project did not train the model. Its authors state that the English models were first fine-tuned on unlabeled
  data including Wikipedia, CC-net, StackExchange, Reddit, S2ORC and the sentence-transformers training data, then on labeled NLI,
  FEVER, NQ, HotpotQA, Quora, StackExchange Duplicates and MEDI data (Xiao et al., "C-Pack", arXiv:2309.07597).
  The weights are distributed under the model's MIT licence; the licences of the training sets are not conferred on the weights by
  this project and are not verified here. Anyone who needs weights with a different training history can load their own by path.

The tests and tools also run the Hugging Face `tokenizers` and ONNX Runtime packages as reference implementations (fetched into a cache,
not part of the repository or any package).

### opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill (package `RecursiveAI.OneIndex.Models`)

The default English learned sparse encoder packs the weights, vocabulary and query token weights of this model (OpenSearch project),
converted to the OneModel file format with the matrix-multiply weights and the word-embedding table stored as 8-bit integers.

- Source: https://huggingface.co/opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill, revision babf71f3c48695e2e53a978208e8aba48335e3c0
- Licence: Apache License 2.0 (the model card's metadata states `license: apache-2.0`, and its License section: "This project is licensed under the Apache v2.0 License"). The licence text is packed as `models/neural-sparse/LICENSE-neural-sparse.txt`.
- Provenance: this project did not train the model. The model card states: "The training datasets includes MS MARCO, eli5_question_answer,
  squad_pairs, WikiAnswers, yahoo_answers_title_question, gooaq_pairs, stackexchange_duplicate_questions_body_body, wikihow,
  S2ORC_title_abstract, stackexchange_duplicate_questions_title-body_title-body, yahoo_answers_question_answer, searchQA_top5_snippets,
  stackexchange_duplicate_questions_title_title, yahoo_answers_title_answer, fever, fiqa, hotpotqa, nfcorpus, scifact." The weights are
  distributed under the model's Apache-2.0 licence; the licences of the training sets are not conferred on the weights by this project and
  are not verified here. Anyone who needs weights with a different training history can load their own with `NeuralSparseEncoder`.

### cross-encoder/ms-marco-MiniLM-L6-v2 (package `RecursiveAI.OneIndex.Models`)

The default English reranker packs the weights and vocabulary of cross-encoder/ms-marco-MiniLM-L6-v2 (Nils Reimers and the Sentence-Transformers
contributors; MiniLM architecture by Wang et al.), converted to the OneModel file format with the matrix-multiply weights and the word-embedding table
stored as 8-bit integers. The model was trained on the MS MARCO passage ranking set; no part of that data is included.

- Source: https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2, revision 233902d25c440f23af6f7d6e94d2946bac0bee0a
- Licence: Apache License 2.0 (the model card's metadata states `license: apache-2.0`).
- The notice packed with the weights is `models/ms-marco-MiniLM-L6-v2/LICENSE-ms-marco-MiniLM-L6-v2.txt` in the package.
- Provenance: we did not train this model; the weights are redistributed as published, converted and quantized. Training data as the authors state it: the MS MARCO passage ranking set. The weights are governed by their Apache-2.0 licence; the MS MARCO data terms are the data owner's and apply to that data, which is not included. Weights stay loadable from files (`MiniLmReranker.Open`), so an application can use another model or none.

### intfloat/multilingual-e5-small (package `RecursiveAI.OneIndex.Models.Multilingual`)

The multilingual embedder packs the weights and SentencePiece model of this model (Liang Wang, Nan Yang, Xiaolong Huang, Linjun Yang, Rangan Majumder and Furu Wei, Microsoft; "Multilingual E5 Text Embeddings: A Technical Report", arXiv:2402.05672), converted to the OneModel file format with the matrix-multiply weights and the word-embedding table stored as 8-bit integers.

- Source: https://huggingface.co/intfloat/multilingual-e5-small, revision 614241f622f53c4eeff9890bdc4f31cfecc418b3
- Licence: MIT (the model card's metadata states `license: mit`). The licence text is packed as `models/multilingual-e5-small/LICENSE-multilingual-e5-small.txt`.
- Provenance: this project did not train or fine-tune the model. The authors state that it is initialized from multilingual MiniLM and trained in two stages, contrastive pre-training on about one billion weakly supervised text pairs and then fine-tuning on labeled data; the sources include MS MARCO, NQ and Mr. TyDi / MIRACL (see the technical report for the full list). The weights are governed by the model's MIT licence; the terms of the training data are the data owners' and are not conferred on the weights by this project. The weights stay loadable from files (`MultilingualE5Embedder.Open`), so an application can use another model or none. The SentencePiece model is the published `sentencepiece.bpe.model` (a unigram model inherited from XLM-RoBERTa).

## Build-time tooling

The source generator is compiled against Microsoft.CodeAnalysis (MIT), which is used at build time only and is not part of any package's dependencies.
