#!/usr/bin/env python3
"""Information-only tantivy-py rows over the Wikipedia abstracts, same queries as the harness (`oneindex-harness queries`).

  tantivy_rows.py <abstracts.jsonl> <queries.json> <docs> <work dir> <out.json> [reps]

Tantivy's default tokenizer lower-cases and splits on non-alphanumerics: the closest match to the matched Standard analysis
that tantivy offers (no stop words, no stemming). Queries run single-threaded and sequentially, top 10, searcher already
warm. Rows without a counterpart (facets/stats/sort over our derived columns) use tantivy's own fast fields and collectors
only where tantivy-py exposes them; the rest are skipped and say so.
"""
import json
import os
import resource
import shutil
import statistics
import sys
import time

import tantivy


def main():
    corpus, queries_path, n_docs, work, out = sys.argv[1:6]
    reps = int(sys.argv[6]) if len(sys.argv) > 6 else 3
    n_docs = int(n_docs)
    if os.path.exists(work):
        shutil.rmtree(work)
    os.makedirs(work)

    sb = tantivy.SchemaBuilder()
    sb.add_text_field("text", stored=False, tokenizer_name="default")
    sb.add_text_field("id", stored=True, tokenizer_name="raw")
    sb.add_integer_field("len", stored=False, indexed=True, fast=True)
    sb.add_text_field("cat", stored=False, tokenizer_name="raw", fast=True)
    schema = sb.build()
    index = tantivy.Index(schema, path=work)
    writer = index.writer(heap_size=256_000_000, num_threads=1)

    rows = []
    t0 = time.perf_counter()
    count = 0
    with open(corpus, encoding="utf-8") as f:
        for line in f:
            if count >= n_docs:
                break
            d = json.loads(line)
            writer.add_document(tantivy.Document(id=d["id"], text=d["text"], len=d["len"], cat=d["cat"]))
            count += 1
    add = time.perf_counter() - t0
    writer.commit()
    total = time.perf_counter() - t0
    size = sum(os.path.getsize(os.path.join(work, x)) for x in os.listdir(work))
    rows.append({"row": "index", "docs": count, "docsPerSecond": count / total, "addSeconds": add,
                 "indexBytes": size, "peakRssMiB": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024})
    index.reload()
    searcher = index.searcher()

    def terms(ts):
        return tantivy.Query.boolean_query([(tantivy.Occur.Should, tantivy.Query.term_query(schema, "text", t)) for t in ts])

    builders = {
        "Terms1": lambda q: terms(q["terms"]),
        "Terms2": lambda q: terms(q["terms"]),
        "Terms5": lambda q: terms(q["terms"]),
        "Phrase": lambda q: tantivy.Query.phrase_query(schema, "text", q["terms"]),
        "Prefix": lambda q: tantivy.Query.regex_query(schema, "text", q["prefix"] + ".*"),
        "AsYouType": lambda q: tantivy.Query.boolean_query(
            [(tantivy.Occur.Must, tantivy.Query.term_query(schema, "text", t)) for t in q["required"]]
            + [(tantivy.Occur.Must, tantivy.Query.regex_query(schema, "text", q["prefix"] + ".*"))]),
        "Fuzzy1": lambda q: tantivy.Query.fuzzy_term_query(schema, "text", q["term"], distance=1),
        "Fuzzy2": lambda q: tantivy.Query.fuzzy_term_query(schema, "text", q["term"], distance=2),
        "Filtered": lambda q: tantivy.Query.boolean_query([
            (tantivy.Occur.Must, terms(q["terms"])),
            (tantivy.Occur.Must, tantivy.Query.range_query(schema, "len", tantivy.FieldType.Integer, q["min"], q["max"])),
            (tantivy.Occur.Must, tantivy.Query.boolean_query(
                [(tantivy.Occur.Should, tantivy.Query.term_query(schema, "cat", c)) for c in q["categories"]]))]),
    }
    queries = json.load(open(queries_path, encoding="utf-8"))
    for row, build in builders.items():
        built = [build(q) for q in queries[row]]
        for q in built:
            searcher.search(q, 10)  # warm-up
        times = []
        for _ in range(reps):
            t = time.perf_counter()
            for q in built:
                searcher.search(q, 10)
            times.append(time.perf_counter() - t)
        med = statistics.median(times)
        rows.append({"row": row, "queries": len(built), "medianMsPerQuery": 1000 * med / len(built),
                     "peakRssMiB": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, "indexBytes": size})
    for row in ("Facets", "Stats", "Sort"):
        rows.append({"row": row, "skipped": "tantivy-py has no equivalent collector in this harness"})
    json.dump({"engine": "tantivy-py", "engineVersion": tantivy.__version__, "documents": count, "rows": rows},
              open(out, "w"), indent=2)


if __name__ == "__main__":
    main()
