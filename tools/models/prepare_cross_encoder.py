#!/usr/bin/env python3
"""Downloads cross-encoder/ms-marco-MiniLM-L6-v2 at a pinned revision, checks its hashes and writes the ONNX variants the converter
turns into OneModel files.

    prepare_cross_encoder.py <out dir>

Writes into <out dir>: model.onnx (fp32, as published), model.int8.onnx (weight-only int8 as for the embedder), vocab.txt,
tokenizer.json, config.json, README.md and LICENSE-ms-marco-MiniLM-L6-v2.txt. Run it in the venv of tools/models/requirements.txt.
The model card states `license: apache-2.0`.
"""
import os, sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from prepare_bge_small import sha256, write_weight_only_int8  # noqa: E402

REPO = "cross-encoder/ms-marco-MiniLM-L6-v2"
REVISION = "233902d25c440f23af6f7d6e94d2946bac0bee0a"
SHA256 = {
    "onnx/model.onnx": "5d3e70fd0c9ff14b9b5169a51e957b7a9c74897afd0a35ce4bd318150c1d4d4a",
    "vocab.txt": "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
    "tokenizer.json": "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
}


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    from huggingface_hub import snapshot_download
    src = snapshot_download(REPO, revision=REVISION, local_dir=os.path.join(out, "hf"),
                            allow_patterns=["onnx/model.onnx", "vocab.txt", "tokenizer.json", "tokenizer_config.json", "config.json", "README.md"])
    for name, expected in SHA256.items():
        actual = sha256(os.path.join(src, name))
        if actual != expected:
            sys.exit(f"{name}: sha256 {actual}, expected {expected}")
    for n, d in (("onnx/model.onnx", "model.onnx"), ("vocab.txt", "vocab.txt"), ("tokenizer.json", "tokenizer.json"),
                 ("config.json", "config.json"), ("README.md", "README.md")):
        target = os.path.join(out, d)
        if os.path.lexists(target):
            os.remove(target)
        os.symlink(os.path.join(src, n), target)
    write_weight_only_int8(os.path.join(out, "model.onnx"), os.path.join(out, "model.int8.onnx"))
    with open(os.path.join(out, "LICENSE-ms-marco-MiniLM-L6-v2.txt"), "w") as f:
        f.write(NOTICE)
    for name in ("model.onnx", "model.int8.onnx"):
        print(name, sha256(os.path.join(out, name)))


NOTICE = """cross-encoder/ms-marco-MiniLM-L6-v2

Source: https://huggingface.co/cross-encoder/ms-marco-MiniLM-L6-v2 at revision 233902d25c440f23af6f7d6e94d2946bac0bee0a
Authors: Nils Reimers and the Sentence-Transformers contributors (UKP Lab); the MiniLM architecture is by Wang et al. (Microsoft)
Licence: Apache License, Version 2.0 (model card metadata `license: apache-2.0`)
Training data (provenance only): the MS MARCO passage ranking training set, as stated on the model card.

The weights in this package are the published weights, converted to the OneModel file format and quantized to 8-bit integers (weight-only
quantization of the matrix-multiply weights). The vocabulary is the model's own vocab.txt (the uncased BERT vocabulary).

Licensed under the Apache License, Version 2.0 (the "License"); you may not use these files except in compliance with the License. You may
obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific language governing permissions
and limitations under the License.
"""

if __name__ == "__main__":
    main()
