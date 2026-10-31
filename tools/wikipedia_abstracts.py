#!/usr/bin/env python3
"""Build the Wikipedia abstract corpus the text benchmarks run on.

Each article of the wikimedia/wikipedia parquet shards contributes one JSON line: its first paragraph (the abstract), its
title, and two columns derived from them so that filter, facet, sort and aggregation rows have something real to work on:
  len  the abstract's length in characters (numeric column)
  cat  the title's first letter, upper-cased, or '#' when it is not a letter (keyword column; a naturally skewed facet)
The output depends only on the input shards and this script, so its SHA-256 is stable. Articles whose abstract is shorter
than 30 characters are skipped.
"""
import argparse
import json
import os
import sys

import pyarrow.parquet as pq


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", required=True)
    ap.add_argument("shards", nargs="+")
    args = ap.parse_args()
    n = 0
    with open(args.out + ".part", "w", encoding="utf-8", newline="\n") as out:
        for shard in args.shards:
            pf = pq.ParquetFile(shard)
            for batch in pf.iter_batches(columns=["id", "title", "text"], batch_size=4096):
                for row in batch.to_pylist():
                    text = row["text"] or ""
                    abstract = next((p.strip() for p in text.split("\n\n") if p.strip()), "")
                    if len(abstract) < 30:
                        continue
                    title = row["title"] or ""
                    first = title[:1].upper()
                    cat = first if first.isalpha() else "#"
                    out.write(json.dumps({"id": row["id"], "title": title, "text": abstract,
                                          "len": len(abstract), "cat": cat}, ensure_ascii=False) + "\n")
                    n += 1
    os.replace(args.out + ".part", args.out)
    print(f"wrote {n} abstracts to {args.out}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
