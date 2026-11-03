#!/usr/bin/env python3
"""Throughput ratios at matched recall from the logs of tools/vector_bench_group.sh.

    tools/vector_ratios.py <log> [<log> ...]

For each dimension: our queries per second divided by the faster managed comparator's at the same recall, with each engine's
curve interpolated linearly between grid points (queries per second against recall) and the comparison taken at the target recall
only where both curves reach it; "best both reach" uses the highest recall both curves reach. Above 1.0 is faster than the comparator.
Also prints the build times and the ratio of ours to Hnsw.Net's.
"""
import re, sys, collections

pt = re.compile(r"^(oneindex|hnswnet|curiosity),(\d+),(\d+),ef=(\d+),recall@10=([0-9.]+),qps=([0-9.]+)")
eng = re.compile(r"^# engine (\w+) n=(\d+) d=(\d+) M=\d+ efConstruction=\d+ build ([0-9.]+)s .*peak-RSS (\d+) MB index-bytes (-?\d+)")
curves = collections.defaultdict(list)
builds = {}
for path in sys.argv[1:]:
    for line in open(path):
        m = pt.match(line)
        if m:
            curves[(m.group(1), int(m.group(2)))].append((float(m.group(5)), float(m.group(6))))
        m = eng.match(line)
        if m:
            builds[(m.group(1), int(m.group(3)))] = (float(m.group(4)), int(m.group(5)), int(m.group(6)))


def qps_at(curve, target):
    c = sorted(curve)
    if target > c[-1][0] + 1e-12 or target < c[0][0] - 1e-12:
        return None
    for (r0, q0), (r1, q1) in zip(c, c[1:]):
        if r0 <= target <= r1:
            return q0 if r1 == r0 else q0 + (q1 - q0) * (target - r0) / (r1 - r0)
    return c[-1][1]


for dim in sorted({d for _, d in curves}):
    ours = curves[("oneindex", dim)]
    others = [e for e in ("hnswnet", "curiosity") if curves.get((e, dim))]
    print(f"## {dim} dimensions")
    for t in (0.90, 0.95, 0.99):
        mine = qps_at(ours, t)
        theirs = {e: qps_at(curves[(e, dim)], t) for e in others}
        reach = {e: q for e, q in theirs.items() if q is not None}
        if mine is None or not reach:
            best = max([r for r, _ in ours if all(r <= max(x for x, _ in curves[(e, dim)]) for e in ("hnswnet",) if curves.get((e, dim)))], default=None)
            print(f"  @{t:.2f}: not reached by ours or the comparator (ours max recall {max(r for r, _ in ours):.4f}, hnswnet max {max(r for r, _ in curves[('hnswnet', dim)]):.4f})")
            continue
        e = max(reach, key=reach.get)
        print(f"  @{t:.2f}: ours {mine:.0f} qps, fastest comparator {e} {reach[e]:.0f} qps -> {mine / reach[e]:.2f}x (" + ", ".join(f"{k} {v:.0f}" if v else f"{k} not reached" for k, v in theirs.items()) + ")")
    r = min(max(r for r, _ in ours), *(max(r for r, _ in curves[(e, dim)]) for e in others if e == "hnswnet"))
    mine = qps_at(ours, r - 1e-9)
    h = qps_at(curves[("hnswnet", dim)], r - 1e-9)
    print(f"  best both reach (ours vs hnswnet) recall {r:.4f}: {mine:.0f} vs {h:.0f} qps -> {mine / h:.2f}x")
    for e in ("oneindex", "hnswnet", "curiosity"):
        if (e, dim) in builds:
            b, rss, size = builds[(e, dim)]
            print(f"  build {e}: {b:.1f} s, peak RSS {rss} MB, index bytes {size}")
    if ("oneindex", dim) in builds and ("hnswnet", dim) in builds:
        print(f"  build ratio ours/hnswnet {builds[('oneindex', dim)][0] / builds[('hnswnet', dim)][0]:.2f}; ours/curiosity {builds[('oneindex', dim)][0] / builds[('curiosity', dim)][0]:.2f}")
