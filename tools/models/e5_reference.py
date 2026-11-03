#!/usr/bin/env python3
"""Reference sentence embeddings of multilingual-e5-small from ONNX Runtime (run as a black box), for the parity check of the OneIndex
embedder: the text is prefixed with `passage: `, tokenized with SentencePiece through the model's own fast tokenizer.json, run in float32
one text at a time, mean-pooled over the tokens and scaled to unit length.

    e5_reference.py <model.onnx> <tokenizer.json> <texts.jsonl (JSON strings, one per line)> <out dir> [count]

Writes <out dir>/texts.jsonl ({"t": text} per line) and <out dir>/reference.f32.
"""
import json, os, sys
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

model, tok_path, samples, out = sys.argv[1:5]
count = int(sys.argv[5]) if len(sys.argv) > 5 else 800
os.makedirs(out, exist_ok=True)
tok = Tokenizer.from_file(tok_path)
tok.enable_truncation(max_length=512)
lines = [json.loads(l) for l in open(samples, encoding="utf-8")]
lines = [t for t in lines if t.strip()]
step = max(1, len(lines) // count)
texts = lines[::step][:count]
so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
sess = ort.InferenceSession(model, so, providers=["CPUExecutionProvider"])
vectors = []
for t in texts:
    ids = np.array([tok.encode("passage: " + t).ids], dtype=np.int64)
    h = sess.run(["last_hidden_state"], {"input_ids": ids, "attention_mask": np.ones_like(ids), "token_type_ids": np.zeros_like(ids)})[0][0]
    v = h.mean(axis=0)
    vectors.append(v / max(np.linalg.norm(v), 1e-12))
np.stack(vectors).astype(np.float32).tofile(os.path.join(out, "reference.f32"))
with open(os.path.join(out, "texts.jsonl"), "w", encoding="utf-8") as f:
    for t in texts:
        f.write(json.dumps({"t": t}, ensure_ascii=False) + "\n")
print(len(texts), "texts")
