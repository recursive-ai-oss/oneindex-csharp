#!/usr/bin/env bash
# The native rows of the benchmark suite, information only: tantivy-py and SQLite FTS5 over the abstracts (same queries as the text rows),
# or hnswlib and faiss HNSW over the embedding sets. Each runs alone, one after another, and prints the machine, load and results.
#
#   tools/bench_native_run.sh text <out dir> [docs]
#   tools/bench_native_run.sh vectors <embeddings dir>
#
# Needs the oracle venv (tantivy-py, hnswlib, faiss-cpu, numpy) and the pinned sqlite3 under $ONEINDEX_DATA/oracles (tools/fetch.sh).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
what=${1:?text|vectors}; arg=${2:?dir}; docs=${3:-302567}
root="${ONEINDEX_DATA:-$HOME/oneindex}"
py="$root/oracles/venv/bin/python"
echo "# machine: $(uname -sm), $(lscpu | grep -m1 'Model name' | sed 's/.*: *//'), $(nproc) cores; load before: $(uptime | sed 's/.*load average[s]*: *//')"
echo "# tantivy-py $("$py" -c 'import tantivy;print(tantivy.__version__)' 2>/dev/null || true); sqlite3 $("$root/oracles/sqlite-3540100/sqlite3" --version | cut -d' ' -f1)"
case "$what" in
  text)
    out=$arg; mkdir -p "$out" "$root/work"
    for d in "$HOME/dotnet10"; do [ -d "$d" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d"; done
    export ONEINDEX_ARTIFACTS="${ONEINDEX_ARTIFACTS:-$root/build}"
    harness="$ONEINDEX_ARTIFACTS/bin/OneIndex.Harness.Cli/release/oneindex-harness"
    corpus="$root/corpus/wikipedia/abstracts-20231101.en-00000-00001.jsonl"
    "$harness" queries --corpus "$corpus" --docs "$docs" --out "$out/queries.json" >/dev/null
    "$py" "$here/native/tantivy_rows.py" "$corpus" "$out/queries.json" "$docs" "$root/work/tantivy" "$out/tantivy-$docs.json"
    echo "# load after tantivy: $(uptime | sed 's/.*load average[s]*: *//')"
    "$here/native/fts5_rows.sh" "$corpus" "$out/queries.json" "$docs" "$root/work/fts5" "$out/fts5-$docs.tsv"
    echo "# load after fts5: $(uptime | sed 's/.*load average[s]*: *//')"
    cat "$out/fts5-$docs.tsv"
    ;;
  vectors)
    for spec in "384 small-384 150000" "768 base-768 75000"; do
      set -- $spec
      for lib in hnswlib faiss; do echo "== $1 $lib (information)"; "$py" "$here/native/vector_rows.py" "$arg/$2" "$3" $lib; done
    done
    echo "# load at end: $(uptime | sed 's/.*load average[s]*: *//')"
    ;;
esac
