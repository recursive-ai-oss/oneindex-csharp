#!/usr/bin/env bash
# Runs the vector bench with several builder settings over a prefix of an embedding set and prints the rows that reach each recall target.
# usage: tools/vector_explore.sh <embedding-dir> <count> <label:extra args> ...
set -u
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
dir=$1; count=$2; shift 2
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/bench-build}
dotnet build -c Release -m:4 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj || exit 1
for spec in "$@"; do
  label=${spec%%:*}; extra=${spec#*:}
  echo "== $label ($extra)"
  # shellcheck disable=SC2086
  dotnet "$out/vb/OneIndex.VectorBench.dll" --dir "$dir" --count "$count" --passes 1 $extra | grep -E "^#|^@|^oneindex|^hnsw"
done
