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

## Segmentation and stop-word dictionaries

Stop words: see the Snowball section above. No segmentation dictionaries yet.

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
- Provenance: we did not train this model; the weights are redistributed as published, converted and quantized. Training data as the authors state it: the model card does not list the
  sets; it points to the released BGE training data (BAAI-MTP, "the massive training data of BGE"), whose composition is described by its publisher.

### cross-encoder/ms-marco-MiniLM-L6-v2 (package `RecursiveAI.OneIndex.Models`)

The default English reranker packs the weights and vocabulary of cross-encoder/ms-marco-MiniLM-L6-v2 (Nils Reimers and the Sentence-Transformers
contributors; MiniLM architecture by Wang et al.), converted to the OneModel file format with the matrix-multiply weights and the word-embedding table
stored as 8-bit integers. The model was trained on the MS MARCO passage ranking set; no part of that data is included.

- Source: https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2, revision 233902d25c440f23af6f7d6e94d2946bac0bee0a
- Licence: Apache License 2.0 (the model card's metadata states `license: apache-2.0`).
- The notice packed with the weights is `models/ms-marco-MiniLM-L6-v2/LICENSE-ms-marco-MiniLM-L6-v2.txt` in the package.
- Provenance: we did not train this model; the weights are redistributed as published, converted and quantized. Training data as the authors state it: the MS MARCO passage ranking set. The weights are governed by their Apache-2.0 licence; the MS MARCO data terms are the data owner's and apply to that data, which is not included. Weights stay loadable from files (`MiniLmReranker.Open`), so an application can use another model or none.

The tests and tools also run the Hugging Face `tokenizers` and ONNX Runtime packages as reference implementations (fetched into a cache,
not part of the repository or any package).

## Build-time tooling

The source generator is compiled against Microsoft.CodeAnalysis (MIT), which is used at build time only and is not part of any package's dependencies.
