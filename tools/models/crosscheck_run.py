#!/usr/bin/env python3
"""nDCG@10 of a TREC run file by pytrec_eval, to check the number the model tool prints.

    crosscheck_run.py <beir dataset dir> <run file>
"""
import csv, os, sys
import pytrec_eval

qrels = {}
with open(os.path.join(sys.argv[1], "qrels", "test.tsv"), newline="", encoding="utf-8") as f:
    rows = csv.reader(f, delimiter="\t")
    next(rows)
    for q, d, s in rows:
        qrels.setdefault(q, {})[d] = int(s)
run = {}
for line in open(sys.argv[2], encoding="utf-8"):
    q, _, d, _, score, _ = line.split()
    run.setdefault(q, {})[d] = float(score)
result = pytrec_eval.RelevanceEvaluator(qrels, {"ndcg_cut_10"}).evaluate(run)
values = [v["ndcg_cut_10"] for v in result.values()]
print(f"pytrec_eval ndcg_cut_10 {sum(values) / len(values):.4f} over {len(values)} queries")
