#!/usr/bin/env bash
# IVF against HNSW as per-field segment vector indexes, same vectors, single-threaded: recall@10, QPS, build time and index bytes
# at three settings each (HNSW ef 100/200/400, IVF nprobe 16/48/96).
#
#   tools/vector_ivf_rows.sh <embeddings dir>
set -u
export PATH="$HOME/dotnet10:$HOME/oneindex/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/dotnet10}" DOTNET_CLI_TELEMETRY_OPTOUT=1
sets=$1
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/bench-build}
scratch=${ONEINDEX_BENCH_SCRATCH:-$HOME/oneindex/bench-tmp}
mkdir -p "$scratch"
echo "# machine: $(uname -sm), $( (lscpu 2>/dev/null | grep -m1 'Model name' | sed 's/.*: *//') || sysctl -n machdep.cpu.brand_string), $(nproc 2>/dev/null || sysctl -n hw.ncpu) cores"
echo "# dotnet: $(dotnet --version)"
echo "# load before: $(uptime | sed 's/.*load average[s]*: *//')"
dotnet build -c Release -m:2 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj >/dev/null || exit 1
for spec in "384 small-384 150000" "768 base-768 75000"; do
  set -- $spec
  echo "== $1 hnsw"
  dotnet "$out/vb/OneIndex.VectorBench.dll" --engine oneindex-segment --dir "$sets/$2" --count "$3" --grid 100,200,400 --passes 3 --scratch "$scratch" | grep -E '^#|^@|^oneindex'
  echo "== $1 ivf"
  dotnet "$out/vb/OneIndex.VectorBench.dll" --engine oneindex-segment-ivf --dir "$sets/$2" --count "$3" --grid 16,48,96 --passes 3 --scratch "$scratch" | grep -E '^#|^@|^oneindex'
  echo "# load after $1: $(uptime | sed 's/.*load average[s]*: *//')"
done
