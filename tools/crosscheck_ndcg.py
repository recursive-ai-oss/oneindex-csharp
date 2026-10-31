#!/usr/bin/env python3
"""Check the harness's nDCG@10 against pytrec_eval on the very same run files.

  crosscheck_ndcg.py <beir dataset dir> <summary.json>

For every run named in the summary it reads runs/<name>.run and runs/<name>.beir.run next to it, evaluates them with
pytrec_eval (ndcg_cut_10, the evaluator BEIR itself uses) and compares with the harness's numbers. Exits 1 when any pair
differs by more than 5e-5 (agreement to four decimals).
"""
import csv
import json
import os
import sys

import pytrec_eval


def read_qrels(path):
    qrels = {}
    with open(path, newline="", encoding="utf-8") as f:
        rows = csv.reader(f, delimiter="\t")
        next(rows)  # header
        for q, d, s in rows:
            qrels.setdefault(q, {})[d] = int(s)
    return qrels


def read_run(path):
    run = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            p = line.split(" ")
            run.setdefault(p[0], {})[p[2]] = float(p[4])
    return run


def main():
    dataset, summary_path = sys.argv[1:3]
    qrels = read_qrels(os.path.join(dataset, "qrels", "test.tsv"))
    summary = json.load(open(summary_path, encoding="utf-8"))
    runs_dir = os.path.join(os.path.dirname(summary_path), "runs")
    evaluator = pytrec_eval.RelevanceEvaluator(qrels, {"ndcg_cut_10"})
    worst = 0.0
    print(f"{'run':58} {'ours':>8} {'pytrec':>8} | {'ours(beir)':>10} {'pytrec(beir)':>12}")
    for r in summary["runs"]:
        row = []
        for suffix, ours in ((".run", r["Ndcg10"]), (".beir.run", r["Ndcg10Beir"])):
            scores = evaluator.evaluate(read_run(os.path.join(runs_dir, r["Run"] + suffix)))
            theirs = sum(v["ndcg_cut_10"] for v in scores.values()) / len(scores)
            worst = max(worst, abs(ours - theirs))
            row += [ours, theirs]
        print(f"{r['Run']:58} {row[0]:8.4f} {row[1]:8.4f} | {row[2]:10.4f} {row[3]:12.4f}")
    print(f"max abs difference {worst:.2e}")
    return 0 if worst <= 5e-5 else 1


if __name__ == "__main__":
    sys.exit(main())
