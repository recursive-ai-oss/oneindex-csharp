#!/usr/bin/env python3
"""Reference sentence embeddings from ONNX Runtime (run as a black box), for the parity check of the OneIndex embedder.

    reference_embeddings.py <model.onnx> <tokenizer.json> <samples.jsonl> <out dir> [count] [prefix]

Takes `count` texts from the tokenizer reference samples (every adversarial text, then evenly spaced others), writes
<out dir>/texts.jsonl and <out dir>/reference.f32: one row per text, the first token's hidden state scaled to unit length, computed one
text at a time in float32 with the model's own tokenizer.json (no padding).
"""
import json, os, sys
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

model, tok_path, samples, out = sys.argv[1:5]
count = int(sys.argv[5]) if len(sys.argv) > 5 else 1000
prefix = sys.argv[6] if len(sys.argv) > 6 else ""
os.makedirs(out, exist_ok=True)
tok = Tokenizer.from_file(tok_path)
tok.enable_truncation(max_length=512)
lines = [json.loads(l)["t"] for l in open(samples, encoding="utf-8")]
step = max(1, len(lines) // count)
texts = lines[:60] + lines[60::step][: count - 60]
so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
sess = ort.InferenceSession(model, so, providers=["CPUExecutionProvider"])
vectors = []
for t in texts:
    e = tok.encode(prefix + t)
    ids = np.array([e.ids], dtype=np.int64)
    h = sess.run(["last_hidden_state"], {"input_ids": ids, "attention_mask": np.ones_like(ids), "token_type_ids": np.zeros_like(ids)})[0][0, 0]
    vectors.append(h / max(np.linalg.norm(h), 1e-12))
np.stack(vectors).astype(np.float32).tofile(os.path.join(out, "reference.f32"))
with open(os.path.join(out, "texts.jsonl"), "w", encoding="utf-8") as f:
    for t in texts:
        f.write(json.dumps({"t": t}, ensure_ascii=False) + "\n")
print(len(texts), "texts")
