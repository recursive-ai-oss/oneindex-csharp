#!/usr/bin/env python3
"""Reference token ids and scores for (query, passage) pairs, from the Hugging Face `tokenizers` package and ONNX Runtime (both run as
black boxes), for the parity checks of the OneIndex cross-encoder.

    reference_pairs.py <model dir (model.onnx, tokenizer.json)> <beir root> <out dir> [pairs per set] [pairs to score]

Writes into <out dir>:
  pairs.jsonl   {"q": query, "p": passage, "i": ids, "t": segment ids} for adversarial pairs and, per BEIR set found, pairs of a test
                query with its judged passages and with random passages. Encoded with the model's own tokenizer.json, truncation
                "longest_first" at 512 (what the sentence-transformers cross-encoder asks for).
  scored.jsonl  {"q": query, "p": passage} for the pairs that have a reference score (the adversarial ones, then evenly spaced others)
  reference.f32 the model's float32 logit of each pair in scored.jsonl, one pair per run, no padding, ONNX Runtime graph optimisations off.
"""
import json, os, random, sys
import numpy as np
import onnxruntime as ort
from tokenizers import Tokenizer

model_dir, beir_root, out_dir = sys.argv[1:4]
per_set = int(sys.argv[4]) if len(sys.argv) > 4 else 1500
score_count = int(sys.argv[5]) if len(sys.argv) > 5 else 600
os.makedirs(out_dir, exist_ok=True)
tok = Tokenizer.from_file(os.path.join(model_dir, "tokenizer.json"))
tok.enable_truncation(max_length=512, strategy="longest_first")

long_a = "alpha " * 400
long_b = "beta " * 400
adversarial = [
    ("", ""), ("query", ""), ("", "passage"), ("how do plants make food", "Photosynthesis is the process by which plants make food."),
    ("[SEP]", "[CLS] x [SEP]"), ("what is [MASK]?", "[PAD] [UNK]"), ("naïve café", "Ünïcödé ǅ İstanbul ß"), ("日本語", "한국어 中文𠀀"),
    ("x " * 255, "y " * 255), ("x " * 256, "y " * 256), ("x " * 300, "y"), ("x", "y " * 600), ("x " * 600, "y " * 600),
    (long_a, long_b), (long_a, "short"), ("short", long_b), ("x " * 508, "y"), ("x " * 509, "y"), ("x " * 255, "y " * 254), ("x " * 254, "y " * 255),
    ("x " * 100, "y " * 600), ("x " * 600, "y " * 100), ("a", "b " * 510), ("a", "b " * 509), ("a", "b " * 508),
    ("\x00\x01", "\ufffd\u200b"), ("emoji 😀", "flag 🇺🇸 family 👨‍👩‍👧"), ("é" * 300, "é" * 300),
]
rng = random.Random(11)
pairs = list(adversarial)
for name in sorted(os.listdir(beir_root)):
    d = os.path.join(beir_root, name)
    qf, cf = os.path.join(d, "queries.jsonl"), os.path.join(d, "corpus.jsonl")
    qrels = os.path.join(d, "qrels", "test.tsv")
    if not (os.path.exists(qf) and os.path.exists(cf) and os.path.exists(qrels)):
        continue
    queries = {}
    with open(qf, encoding="utf-8") as f:
        for l in f:
            j = json.loads(l)
            queries[j["_id"]] = j["text"]
    docs = {}
    with open(cf, encoding="utf-8") as f:
        for l in f:
            j = json.loads(l)
            docs[j["_id"]] = (j.get("title", "") + " " + j.get("text", "")).strip()
    ids = sorted(docs)
    judged = []
    with open(qrels, encoding="utf-8") as f:
        next(f)
        for l in f:
            qid, did, rel = l.rstrip("\n").split("\t")
            if qid in queries and did in docs:
                judged.append((qid, did))
    rng.shuffle(judged)
    chosen = []
    for qid, did in judged[: per_set // 2]:
        chosen.append((queries[qid], docs[did]))
    qids = sorted({q for q, _ in judged})
    while len(chosen) < per_set:
        chosen.append((queries[rng.choice(qids)], docs[rng.choice(ids)]))
    pairs.extend(chosen)

with open(os.path.join(out_dir, "pairs.jsonl"), "w", encoding="utf-8") as f:
    for q, p in pairs:
        e = tok.encode(q, p)
        f.write(json.dumps({"q": q, "p": p, "i": e.ids, "t": e.type_ids}, ensure_ascii=False) + "\n")

step = max(1, (len(pairs) - len(adversarial)) // max(1, score_count - len(adversarial)))
scored = adversarial + pairs[len(adversarial)::step][: score_count - len(adversarial)]
so = ort.SessionOptions()
so.graph_optimization_level = ort.GraphOptimizationLevel.ORT_DISABLE_ALL
sess = ort.InferenceSession(os.path.join(model_dir, "model.onnx"), so, providers=["CPUExecutionProvider"])
out = []
for q, p in scored:
    e = tok.encode(q, p)
    ids = np.array([e.ids], dtype=np.int64)
    types = np.array([e.type_ids], dtype=np.int64)
    out.append(float(sess.run(None, {"input_ids": ids, "attention_mask": np.ones_like(ids), "token_type_ids": types})[0][0, 0]))
np.array(out, dtype=np.float32).tofile(os.path.join(out_dir, "reference.f32"))
with open(os.path.join(out_dir, "scored.jsonl"), "w", encoding="utf-8") as f:
    for q, p in scored:
        f.write(json.dumps({"q": q, "p": p}, ensure_ascii=False) + "\n")
print(len(pairs), "pairs,", len(scored), "scored")
