#!/usr/bin/env python3
"""Downloads BAAI/bge-small-en-v1.5 at a pinned revision, checks its hashes and writes the ONNX variants the converter turns into
OneModel files.

    prepare_bge_small.py <out dir>

Writes into <out dir>: model.onnx (fp32, as published), model.int8.onnx (dynamic int8 quantization of the MatMul weights, per
channel), vocab.txt, tokenizer.json, config.json, README.md (the model card) and LICENSE-bge-small-en-v1.5.txt.
Run it in the venv of tools/models/requirements.txt. The model is MIT licensed (model card: `license: mit`).
"""
import hashlib, os, sys

REPO = "BAAI/bge-small-en-v1.5"
REVISION = "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a"
SHA256 = {
    "onnx/model.onnx": "828e1496d7fabb79cfa4dcd84fa38625c0d3d21da474a00f08db0f559940cf35",
    "vocab.txt": "07eced375cec144d27c900241f3e339478dec958f92fddbc551f295c992038a3",
    "tokenizer.json": "d241a60d5e8f04cc1b2b3e9ef7a4921b27bf526d9f6050ab90f9267a1f9e5c66",
}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    from huggingface_hub import snapshot_download
    src = snapshot_download(REPO, revision=REVISION, local_dir=os.path.join(out, "hf"),
                            allow_patterns=["onnx/model.onnx", "vocab.txt", "tokenizer.json", "config.json", "README.md"])
    for name, expected in SHA256.items():
        actual = sha256(os.path.join(src, name))
        if actual != expected:
            sys.exit(f"{name}: sha256 {actual}, expected {expected}")
    link = lambda n, d: (os.path.exists(os.path.join(out, d)) and os.remove(os.path.join(out, d)), os.symlink(os.path.join(src, n), os.path.join(out, d)))
    for n, d in (("onnx/model.onnx", "model.onnx"), ("vocab.txt", "vocab.txt"), ("tokenizer.json", "tokenizer.json"),
                 ("config.json", "config.json"), ("README.md", "README.md")):
        link(n, d)

    # model.int8.onnx: weights stored as int8 with one scale per output channel, activations stay float32. The graph dequantizes with
    # DequantizeLinear on constants, which the runtime folds when it plans the model, so results do not depend on what else is in a batch.
    write_weight_only_int8(os.path.join(out, "model.onnx"), os.path.join(out, "model.int8.onnx"))

    # model.int8dyn.onnx: for comparison only. ONNX Runtime's dynamic quantization also quantizes activations per tensor, so a text's
    # vector then depends on the other texts in its batch; it is measured and not shipped.
    from onnxruntime.quantization import QuantType, quantize_dynamic
    quantize_dynamic(os.path.join(out, "model.onnx"), os.path.join(out, "model.int8dyn.onnx"), weight_type=QuantType.QInt8,
                     per_channel=True, op_types_to_quantize=["MatMul"])

    # The model card states `license: mit`; the MIT text is the one in the repository's LICENSE-MIT file of the model's authors,
    # which the Hugging Face repository does not carry, so the notice names the model card instead.
    with open(os.path.join(out, "LICENSE-bge-small-en-v1.5.txt"), "w") as f:
        f.write(NOTICE)
    print("model.onnx", sha256(os.path.join(out, "model.onnx")))
    for name in ("model.int8.onnx", "model.int8dyn.onnx"):
        print(name, sha256(os.path.join(out, name)))


def write_weight_only_int8(source, target):
    import numpy as np
    import onnx
    from onnx import TensorProto, helper, numpy_helper

    from onnx import version_converter
    model = onnx.load(source)
    if max((o.version for o in model.opset_import if o.domain in ("", "ai.onnx")), default=0) < 13:
        model = version_converter.convert_version(model, 13)
    graph = model.graph
    initializers = {i.name: i for i in graph.initializer}
    # (initializer name, axis along which each slice gets its own scale): MatMul weights are [K, N] with a scale per column N;
    # an embedding table is [V, D] with a scale per row.
    targets = {}
    for node in graph.node:
        if node.op_type == "MatMul" and node.input[1] in initializers and len(initializers[node.input[1]].dims) == 2:
            targets[node.input[1]] = 1
        if node.op_type == "Gather" and node.input[0] in initializers and initializers[node.input[0]].dims[0] > 10000:
            targets[node.input[0]] = 0
    new_nodes = []
    for name, axis in targets.items():
        w = numpy_helper.to_array(initializers[name]).astype(np.float32)
        reduce_axis = 0 if axis == 1 else 1
        scale = np.maximum(np.abs(w).max(axis=reduce_axis, keepdims=True), 1e-12) / 127.0
        q = np.clip(np.rint(w / scale), -127, 127).astype(np.int8)
        graph.initializer.remove(initializers[name])
        graph.initializer.extend([numpy_helper.from_array(q, name + "__q"), numpy_helper.from_array(np.squeeze(scale, axis=reduce_axis).astype(np.float32), name + "__scale")])
        # DequantizeLinear with a per-channel scale needs opset 13.
        new_nodes.append(helper.make_node("DequantizeLinear", [name + "__q", name + "__scale"], [name], axis=axis, name=name + "__dequant"))
    graph.node.extend([])
    nodes = list(graph.node)
    del graph.node[:]
    graph.node.extend(new_nodes + nodes)
    onnx.save(model, target)
    print("quantized", len(targets), "weight tensors to int8")


NOTICE = """BAAI/bge-small-en-v1.5

Source: https://huggingface.co/BAAI/bge-small-en-v1.5 at revision 5c38ec7c405ec4b44b94cc5a9bb96e735b38267a
Authors: Beijing Academy of Artificial Intelligence (BAAI)
Licence: MIT (model card metadata `license: mit`; the card's License section: "FlagEmbedding is licensed under the MIT License. The
released models can be used for commercial purposes free of charge.")

The weights in this package are the published weights, converted to the OneModel file format and, in the int8 variant, quantized
to 8-bit integers (dynamic quantization of the matrix-multiply weights). The vocabulary is the model's own vocab.txt.

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

if __name__ == "__main__":
    main()
