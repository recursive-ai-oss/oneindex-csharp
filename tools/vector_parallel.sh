#!/usr/bin/env bash
# Runs several vector bench configurations in parallel (recall only is meaningful, timings are not) and prints each one's rows.
# usage: tools/vector_parallel.sh <embedding-dir> <count> <label:extra args> ...
set -u
export PATH=~/dotnet10:$PATH DOTNET_ROOT=~/dotnet10 DOTNET_CLI_TELEMETRY_OPTOUT=1
dir=$1; count=$2; shift 2
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/bench-build}
dotnet build -c Release -m:4 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj || exit 1
pids=()
for spec in "$@"; do
  label=${spec%%:*}; extra=${spec#*:}
  # shellcheck disable=SC2086
  ( dotnet "$out/vb/OneIndex.VectorBench.dll" $extra --dir "$dir" --count "$count" --passes 1 2>&1 | grep -E "^#|^@|^oneindex|^hnsw" | sed "s/^/[$label] /" ) &
  pids+=($!)
done
# Stop at once if a benchmark takes the machine.
( while kill -0 "${pids[0]}" 2>/dev/null; do [ -e "$HOME/WORKER-BENCH.lock" ] && kill "${pids[@]}" 2>/dev/null; sleep 20; done ) &
wait "${pids[@]}"
