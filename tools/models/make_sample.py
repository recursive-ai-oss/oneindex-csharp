#!/usr/bin/env python3
"""Writes a random sub-corpus of a BEIR set as a BEIR directory: every passage judged relevant for a test query, plus random
passages up to the requested size, all queries and the test qrels. Seeded; the corpus order follows the original file.

    make_sample.py <beir set dir> <out dir> <passages> [seed]
"""
import json, os, random, sys

src, out, size = sys.argv[1], sys.argv[2], int(sys.argv[3])
seed = int(sys.argv[4]) if len(sys.argv) > 4 else 7
os.makedirs(os.path.join(out, "qrels"), exist_ok=True)
qrels = open(os.path.join(src, "qrels", "test.tsv"), encoding="utf-8").read().splitlines()
positives = {l.split("\t")[1] for l in qrels[1:] if len(l.split("\t")) >= 3 and int(l.split("\t")[2]) > 0}
ids = [json.loads(l)["_id"] for l in open(os.path.join(src, "corpus.jsonl"), encoding="utf-8")]
rng = random.Random(seed)
rest = [i for i in ids if i not in positives]
chosen = set(positives & set(ids)) | set(rng.sample(rest, max(0, size - len(positives))))
with open(os.path.join(src, "corpus.jsonl"), encoding="utf-8") as f, open(os.path.join(out, "corpus.jsonl"), "w", encoding="utf-8") as o:
    for line in f:
        if json.loads(line)["_id"] in chosen:
            o.write(line)
for name in ("queries.jsonl",):
    with open(os.path.join(src, name), encoding="utf-8") as f, open(os.path.join(out, name), "w", encoding="utf-8") as o:
        o.write(f.read())
with open(os.path.join(out, "qrels", "test.tsv"), "w", encoding="utf-8") as o:
    o.write("\n".join(qrels) + "\n")
print(f"{len(chosen)} passages ({len(positives)} judged relevant)")
