#!/usr/bin/env bash
# The vector rows of the benchmark suite: this library's graph against the two managed comparators (and, as information, hnswlib and
# faiss through tools/native/vector_rows.py) at 384 and 768 dimensions, single-threaded, M = 16, efConstruction = 100.
# One engine after another so each runs alone. Prints machine facts and the load before, between and after the runs.
#
#   tools/vector_bench_group.sh <embeddings dir> [native]
#
# <embeddings dir> holds small-384/ and base-768/ as written by tools/embeddings.py. With "native" the hnswlib and faiss rows follow
# (needs the oracle venv; they are information, not part of the gate).
set -u
export PATH="$HOME/dotnet10:$HOME/oneindex/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/dotnet10}" DOTNET_CLI_TELEMETRY_OPTOUT=1
sets=$1; native=${2:-}
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/u21/build}
grid=110,140,180,220,270,320,450,640,900,1300

echo "# machine: $(uname -sm), $( (lscpu 2>/dev/null | grep -m1 'Model name' | sed 's/.*: *//') || sysctl -n machdep.cpu.brand_string), $(nproc 2>/dev/null || sysctl -n hw.ncpu) cores"
echo "# dotnet: $(dotnet --version)"
echo "# load before: $(uptime | sed 's/.*load average[s]*: *//')"

dotnet build -c Release -m:2 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj >/dev/null || exit 1
dotnet build -c Release -m:2 -v quiet -clp:NoSummary -o "$out/vbc" bench/OneIndex.VectorBench.Curiosity/OneIndex.VectorBench.Curiosity.csproj >/dev/null || exit 1

run() { # label dll engine dir count
  echo "== $1"
  dotnet "$2" --engine "$3" --dir "$4" --count "$5" --grid "$grid" --passes 3 | grep -E '^#|^@|^oneindex|^hnsw|^curiosity'
  echo "# load after $1: $(uptime | sed 's/.*load average[s]*: *//')"
}
for spec in "384 small-384 150000" "768 base-768 75000"; do
  set -- $spec
  run "$1 oneindex"   "$out/vb/OneIndex.VectorBench.dll"  oneindex  "$sets/$2" "$3"
  run "$1 hnswnet"    "$out/vb/OneIndex.VectorBench.dll"  hnswnet   "$sets/$2" "$3"
  run "$1 curiosity"  "$out/vbc/OneIndex.VectorBench.Curiosity.dll" curiosity "$sets/$2" "$3"
done
if [ "$native" = native ]; then
  py="$HOME/oneindex/oracles/venv/bin/python"
  for spec in "384 small-384 150000" "768 base-768 75000"; do
    set -- $spec
    for lib in hnswlib faiss; do echo "== $1 $lib (information)"; "$py" tools/native/vector_rows.py "$sets/$2" "$3" $lib; done
  done
fi
echo "# load at end: $(uptime | sed 's/.*load average[s]*: *//')"
