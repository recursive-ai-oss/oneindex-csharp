#!/usr/bin/env python3
"""Downloads intfloat/multilingual-e5-small at a pinned revision, checks its hashes and writes the ONNX variants the converter turns into
OneModel files.

    prepare_e5_small.py <out dir>

Writes into <out dir>: model.onnx (fp32, as published), model.int8.onnx (weights as int8, one scale per output channel; the 250,000-row
embedding table per row), sentencepiece.bpe.model, tokenizer.json, config.json, README.md and LICENSE-multilingual-e5-small.txt.
Run it in the venv of tools/models/requirements.txt. The model card states `license: mit`.
"""
import hashlib, os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prepare_bge_small import sha256, write_weight_only_int8

REPO = "intfloat/multilingual-e5-small"
REVISION = "614241f622f53c4eeff9890bdc4f31cfecc418b3"
SHA256 = {
    "onnx/model.onnx": "ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665",
    "sentencepiece.bpe.model": "cfc8146abe2a0488e9e2a0c56de7952f7c11ab059eca145a0a727afce0db2865",
    "tokenizer.json": "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39",
}

NOTICE = """intfloat/multilingual-e5-small

Source: https://huggingface.co/intfloat/multilingual-e5-small at revision 614241f622f53c4eeff9890bdc4f31cfecc418b3
Authors: Liang Wang, Nan Yang, Xiaolong Huang, Linjun Yang, Rangan Majumder, Furu Wei (Microsoft), "Multilingual E5 Text Embeddings:
A Technical Report", arXiv:2402.05672
Licence: MIT (model card metadata `license: mit`)

The weights in this package are the published weights, converted to the OneModel file format and quantized to 8-bit integers (the
matrix-multiply weights and the embedding table). The SentencePiece model is the published sentencepiece.bpe.model (a unigram model
inherited from XLM-RoBERTa). We did not train or fine-tune this model.

Provenance: the authors state that the model is initialized from multilingual MiniLM and trained in two stages: contrastive
pre-training on about 1 billion weakly supervised text pairs, then fine-tuning on labeled data. The card lists the sources (among
them MS MARCO, NQ, Mr. TyDi / MIRACL, and others). See the technical report for the full list.

MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files
(the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do
so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
"""


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    from huggingface_hub import snapshot_download
    src = snapshot_download(REPO, revision=REVISION, local_dir=os.path.join(out, "hf"),
                            allow_patterns=["onnx/model.onnx", "sentencepiece.bpe.model", "tokenizer.json", "config.json", "README.md"])
    for name, expected in SHA256.items():
        actual = sha256(os.path.join(src, name))
        if actual != expected:
            sys.exit(f"{name}: sha256 {actual}, expected {expected}")
    for n, d in (("onnx/model.onnx", "model.onnx"), ("sentencepiece.bpe.model", "sentencepiece.bpe.model"), ("tokenizer.json", "tokenizer.json"),
                 ("config.json", "config.json"), ("README.md", "README.md")):
        dst = os.path.join(out, d)
        if os.path.lexists(dst):
            os.remove(dst)
        os.symlink(os.path.join(src, n), dst)
    write_weight_only_int8(os.path.join(out, "model.onnx"), os.path.join(out, "model.int8.onnx"))
    with open(os.path.join(out, "LICENSE-multilingual-e5-small.txt"), "w") as f:
        f.write(NOTICE)
    for name in ("model.onnx", "model.int8.onnx"):
        print(name, sha256(os.path.join(out, name)))


if __name__ == "__main__":
    main()
