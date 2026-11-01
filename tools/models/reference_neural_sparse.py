#!/usr/bin/env python3
"""Reference sparse vectors from the published sentence-transformers runtime (run only, black box).

    reference_neural_sparse.py <model dir with hf/> <texts.jsonl> <out.jsonl> [role]

texts.jsonl: one JSON string per line. out.jsonl: one {token: weight} object per line, for role document (default) or query.
"""
import json, sys


def main():
    model_dir, texts_path, out_path = sys.argv[1:4]
    role = sys.argv[4] if len(sys.argv) > 4 else "document"
    from sentence_transformers import SparseEncoder
    model = SparseEncoder(model_dir + "/hf", trust_remote_code=False)
    texts = [(lambda o: o["t"] if isinstance(o, dict) else o)(json.loads(l)) for l in open(texts_path)]
    encode = model.encode_query if role == "query" else model.encode_document
    emb = encode(texts, convert_to_tensor=True)
    with open(out_path, "w") as f:
        for row in model.decode(emb, top_k=None):
            f.write(json.dumps({t: float(w) for t, w in row}) + "\n")


if __name__ == "__main__":
    main()
