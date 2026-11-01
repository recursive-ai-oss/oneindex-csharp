#!/usr/bin/env python3
"""Build the embedding sets the vector benchmarks and recall runs use. Nothing it writes is ever committed.

Base vectors:  the first --count abstracts of the Wikipedia abstract corpus (tools/wikipedia_abstracts.py), embedded as passages.
Queries:       the titles of the last --queries articles of that corpus (never among the base vectors while count <= total -
               queries), embedded with the model's query instruction, as a retrieval system would.
Models:        BAAI/bge-small-en-v1.5 (384 dimensions) and BAAI/bge-base-en-v1.5 (768 dimensions), MIT licensed, pinned to the
               revisions below. Text is cut to 128 tokens. Vectors have length 1.
Ground truth:  the exact top 100 base vectors of every query by dot product, computed here with a float32 matrix product that
               shares no code with OneIndex.

It works in resumable chunks: each run encodes shards of 5,000 passages until --budget seconds have passed, then stops; run it
again to continue. When every shard exists, `--finalize` joins them into base.f32 / queries.f32 (raw little-endian float32,
row-major), computes truth.i32 (raw int32, queries x 100) and writes manifest.json with the SHA-256 of each file.

    uv venv ~/oneindex/venvs/embeddings
    uv pip install --python ~/oneindex/venvs/embeddings/bin/python torch --index-url https://download.pytorch.org/whl/cpu
    uv pip install --python ~/oneindex/venvs/embeddings/bin/python sentence-transformers numpy
    ~/oneindex/venvs/embeddings/bin/python tools/embeddings.py --model small --count 302000 --budget 1500
    ~/oneindex/venvs/embeddings/bin/python tools/embeddings.py --model small --count 302000 --finalize
"""
import argparse
import hashlib
import json
import os
import sys
import time

import numpy as np

MODELS = {
    "small": ("BAAI/bge-small-en-v1.5", "5c38ec7c405ec4b44b94cc5a9bb96e735b38267a", 384),
    "base": ("BAAI/bge-base-en-v1.5", "a5beb1e3e68b9ab74eb54cfd186867f64f240e1a", 768),
}
QUERY_INSTRUCTION = "Represent this sentence for searching relevant passages: "
SHARD = 5000
TOP = 100


def sha256(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def read_rows(path: str):
    with open(path, encoding="utf-8") as f:
        for line in f:
            yield json.loads(line)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--model", choices=MODELS, required=True)
    ap.add_argument("--abstracts", default=os.path.expanduser("~/oneindex/corpus/wikipedia/abstracts-20231101.en-00000-00001.jsonl"))
    ap.add_argument("--out", default=os.path.expanduser("~/oneindex/corpus/embeddings"))
    ap.add_argument("--count", type=int, required=True, help="base vectors")
    ap.add_argument("--queries", type=int, default=1000)
    ap.add_argument("--budget", type=float, default=1500, help="seconds of encoding per run")
    ap.add_argument("--threads", type=int, default=8)
    ap.add_argument("--finalize", action="store_true")
    args = ap.parse_args()
    name, revision, dims = MODELS[args.model]
    out = os.path.join(args.out, f"{args.model}-{dims}")
    os.makedirs(out, exist_ok=True)

    rows = list(read_rows(args.abstracts))
    if args.count + args.queries > len(rows):
        print(f"the corpus has {len(rows)} abstracts; {args.count} + {args.queries} do not fit", file=sys.stderr)
        return 2
    passages = [r["text"] for r in rows[: args.count]]
    queries = [QUERY_INSTRUCTION + r["title"] for r in rows[len(rows) - args.queries:]]
    shards = [(f"base-{i // SHARD:05d}.npy", passages[i: i + SHARD]) for i in range(0, len(passages), SHARD)]
    shards.append(("queries.npy", queries))

    if not args.finalize:
        import torch
        from sentence_transformers import SentenceTransformer

        torch.set_num_threads(args.threads)
        model = SentenceTransformer(name, revision=revision, device="cpu")
        model.max_seq_length = 128
        started = time.time()
        for file, texts in shards:
            path = os.path.join(out, file)
            if os.path.exists(path):
                continue
            if time.time() - started > args.budget:
                print("budget used; run again to continue", file=sys.stderr)
                return 0
            t0 = time.time()
            vectors = model.encode(texts, batch_size=64, normalize_embeddings=True, convert_to_numpy=True).astype("<f4")
            np.save(path + ".part.npy", vectors)
            os.replace(path + ".part.npy", path)
            print(f"{file}: {len(texts)} in {time.time() - t0:.0f}s", file=sys.stderr)
        print("all shards written; run with --finalize", file=sys.stderr)
        return 0

    missing = [f for f, _ in shards if not os.path.exists(os.path.join(out, f))]
    if missing:
        print(f"{len(missing)} shards are missing, first {missing[0]}", file=sys.stderr)
        return 3
    base = np.concatenate([np.load(os.path.join(out, f)) for f, _ in shards[:-1]]).astype("<f4")
    q = np.load(os.path.join(out, "queries.npy")).astype("<f4")
    assert base.shape == (args.count, dims) and q.shape == (args.queries, dims)
    base.tofile(os.path.join(out, "base.f32"))
    q.tofile(os.path.join(out, "queries.f32"))
    truth = np.empty((len(q), TOP), dtype="<i4")
    for start in range(0, len(q), 100):
        sims = q[start: start + 100] @ base.T
        top = np.argpartition(-sims, TOP, axis=1)[:, :TOP]
        order = np.argsort(-np.take_along_axis(sims, top, axis=1), axis=1, kind="stable")
        truth[start: start + 100] = np.take_along_axis(top, order, axis=1)
    truth.tofile(os.path.join(out, "truth.i32"))
    manifest = {
        "model": name, "revision": revision, "dimensions": dims, "maxTokens": 128, "count": args.count, "queries": args.queries,
        "queryInstruction": QUERY_INSTRUCTION, "topK": TOP,
        "files": {f: sha256(os.path.join(out, f)) for f in ("base.f32", "queries.f32", "truth.i32")},
    }
    with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
    print(json.dumps(manifest, indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
