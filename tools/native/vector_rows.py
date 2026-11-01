#!/usr/bin/env python3
"""Information rows: hnswlib and faiss HNSW on an embedding set (single thread, M and efConstruction matched).

usage: vector_rows.py <set dir> <count> {hnswlib|faiss}
Prints the same lines as bench/OneIndex.VectorBench. Run inside the oracle venv; the libraries are only executed.
"""
import json, resource, statistics, sys, time
import numpy as np

EF_GRID = [10, 14, 20, 28, 40, 56, 80, 110, 140, 180, 220, 270, 320, 380, 450, 540, 640, 760, 900, 1100, 1300, 1800]
K = 10


def main():
    d, count, which = sys.argv[1], int(sys.argv[2]), sys.argv[3]
    man = json.load(open(f"{d}/manifest.json"))
    dims = man["dimensions"]
    data = np.fromfile(f"{d}/base.f32", dtype=np.float32, count=count * dims).reshape(count, dims)
    queries = np.fromfile(f"{d}/queries.f32", dtype=np.float32).reshape(-1, dims)
    if count < man["count"]:
        truth = np.argsort(-(queries @ data.T), axis=1)[:, :K]
    else:
        truth = np.fromfile(f"{d}/truth.i32", dtype=np.int32).reshape(len(queries), 100)[:, :K]
    nq = len(queries)

    t0 = time.perf_counter()
    if which == "hnswlib":
        import hnswlib
        idx = hnswlib.Index(space="ip", dim=dims)
        idx.init_index(max_elements=count, M=16, ef_construction=100, random_seed=1)
        idx.set_num_threads(1)
        idx.add_items(data, np.arange(count))
        search = lambda q, ef: (idx.set_ef(ef), idx.knn_query(q, k=K)[0])[1]
    else:
        import faiss
        faiss.omp_set_num_threads(1)
        idx = faiss.IndexHNSWFlat(dims, 16, faiss.METRIC_INNER_PRODUCT)
        idx.hnsw.efConstruction = 100
        idx.add(data)

        def search(q, ef):
            idx.hnsw.efSearch = ef
            return idx.search(q, K)[1]
    build = time.perf_counter() - t0
    rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss >> 10
    print(f"# engine {which} n={count} d={dims} M=16 efConstruction=100 build {build:.1f}s peak-RSS {rss} MB")
    results = []
    for ef in EF_GRID:
        search(queries[:100], ef)
        times, recall = [], 0.0
        for p in range(3):
            t = time.perf_counter()
            res = np.vstack([search(queries[i:i + 1], ef) for i in range(nq)])
            times.append(time.perf_counter() - t)
            if p == 0:
                recall = sum(len(set(res[i]) & set(truth[i])) for i in range(nq)) / (nq * K)
        qps = nq / statistics.median(times)
        results.append((ef, recall, qps))
        print(f"{which},{dims},{count},ef={ef},recall@10={recall:.4f},qps={qps:.0f}")
        if recall >= 0.9995:
            break
    for target in (0.90, 0.95, 0.99):
        hit = next((r for r in results if r[1] >= target), None)
        print(f"@{target:.2f}: " + (f"ef={hit[0]} recall={hit[1]:.4f} qps={hit[2]:.0f}" if hit else "not reached"))


main()
