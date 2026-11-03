#!/usr/bin/env python3
"""Summarise the text-row result files of tools/bench_text_run.sh.

    tools/bench_summary.py <dir with oneindex-r1.json, lucene-r1.json, oneindex-matched-r1.json, ...>

Per row: the median over repetitions of each engine's median, the ratio (ours / Lucene.NET time; for the index row Lucene.NET / ours
throughput, so that below 1.0 is always better for us), peak resident set, index size and allocated bytes. Prints the geometric mean
of the ratios. The as-you-type typo row with the comparator's match set (oneindex-matched) gates; the documented-semantics row is information.
"""
import glob, json, math, os, re, statistics, sys

d = sys.argv[1]


def one_min(s):
    return float(re.findall(r"[0-9]+\.?[0-9]*", s)[0])


def load(prefix):
    # prefix-r<N>.json, exact prefix (oneindex- must not match oneindex-matched-)
    out = []
    for f in sorted(glob.glob(os.path.join(d, prefix + "-r[0-9]*.json"))):
        out.append(json.load(open(f)))
    return out


def rows(runs):
    table = {}
    for r in runs:
        for row in r["rows"]:
            table.setdefault(row["row"], []).append(row)
    return table


def med(rs, key):
    vals = [x[key] for x in rs if key in x]
    return statistics.median(vals) if vals else None


one, luc, mat = load("oneindex"), load("lucene"), load("oneindex-matched")
t1, t2, t3 = rows(one), rows(luc), rows(mat)
print(f"repetitions: oneindex {len(one)}, lucene {len(luc)}, matched {len(mat)}")
for name, runs in (("oneindex", one), ("lucene", luc)):
    if runs:
        m = runs[0]
        print(f"{name}: {m['engine']} {m['engineVersion']} | {m['machine'].get('cpu')} | {m['machine'].get('logicalCores')} cores | {m['machine'].get('runtime')} | docs {m['documents']}")
        print(f"  1-min load before {[one_min(r['loadBefore']) for r in runs]} after {[one_min(r['loadAfter']) for r in runs]}")
        print(f"  load during (max 1-min) {[max(one_min(x) for x in r['loadDuring']) for r in runs]}")

print("\n| Row | OneIndex | Lucene.NET | ratio (ours/theirs, <1 faster) | checksum equal | RSS MiB ours/theirs | index bytes ours/theirs | alloc B/op ours/theirs |")
print("|---|---|---|---|---|---|---|---|")
ratios = []
names = list(t2.keys())
for name in names:
    a, b = t1.get(name), t2.get(name)
    if name == "TypoAsYouType" and t3.get(name):
        a_doc = a
        a = t3[name]
    if not a or not b:
        print(f"| {name} | n/a | {'n/a' if not b else ''} | | | | | |")
        continue
    if not (a[0].keys() & {"docsPerSecond", "medianMs", "medianMsPerQuery"}) or not (b[0].keys() & {"docsPerSecond", "medianMs", "medianMsPerQuery"}):
        print(f"| {name} | no timing in the result ({a[0].get('error') or a[0].get('skipped') or a[0].get('note') or ''}) | | | | | | |")
        continue
    if name == "index":
        x, y = med(a, "docsPerSecond"), med(b, "docsPerSecond")
        r = y / x
        unit = "docs/s"
        alloc = (med(a, "allocBytesPerDoc"), med(b, "allocBytesPerDoc"))
    elif name == "commit":
        x, y = med(a, "medianMs"), med(b, "medianMs")
        r = x / y
        unit = "ms"
        alloc = (None, None)
    else:
        x, y = med(a, "medianMsPerQuery"), med(b, "medianMsPerQuery")
        r = x / y
        unit = "ms/q"
        alloc = (med(a, "allocBytesPerQuery"), med(b, "allocBytesPerQuery"))
    ratios.append((name, r))
    ca, cb = a[0].get("matchChecksum"), b[0].get("matchChecksum")
    eq = "" if ca is None else ("yes" if ca == cb else f"no ({ca} vs {cb})")
    mem = f"{med(a, 'peakRssMiB'):.0f} / {med(b, 'peakRssMiB'):.0f}" if med(a, "peakRssMiB") and med(b, "peakRssMiB") else ""
    sz = f"{med(a, 'indexBytes'):,.0f} / {med(b, 'indexBytes'):,.0f}" if med(a, "indexBytes") and med(b, "indexBytes") else ""
    al = f"{alloc[0]:,.0f} / {alloc[1]:,.0f}" if alloc[0] is not None and alloc[1] is not None else ""
    flag = " **>1.25**" if r > 1.25 else ""
    print(f"| {name} | {x:,.3f} {unit} | {y:,.3f} {unit} | {r:.2f}{flag} | {eq} | {mem} | {sz} | {al} |")
    if name == "TypoAsYouType":
        print(f"| TypoAsYouType (documented semantics, information) | {med(a_doc, 'medianMsPerQuery'):.3f} ms/q | {y:.3f} ms/q | {med(a_doc, 'medianMsPerQuery') / y:.2f} | not comparable | | | |")

if ratios:
    g = math.exp(sum(math.log(r) for _, r in ratios) / len(ratios))
    print(f"\ngeomean over {len(ratios)} rows: {g:.3f}; worst: {max(ratios, key=lambda p: p[1])[0]} {max(r for _, r in ratios):.2f}")
