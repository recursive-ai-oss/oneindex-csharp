#!/usr/bin/env bash
# The filtered nearest-neighbour rows: recall@10 against the exact search over the passing documents, and queries per second, at
# selectivities 50%, 10%, 1% and 0.1%, for an uncorrelated and a correlated filter (bench/OneIndex.VectorBench/FilteredRun.cs).
#
#   tools/vector_filtered_rows.sh <embeddings dir> <count> <quant: none|8|4|1> <table efs> [sweep]
#
# <embeddings dir> is small-384/ or base-768/ as written by tools/embeddings.py. The table run prints every algorithm (the filtered
# walk with the exact-scan switch, the walk alone, the exact scan alone, naive post-filtering) at each ef in <table efs>
# (comma separated). With "sweep" it instead runs the filtered walk with the switch over a grid of ef, from which the queries per second
# at matched recall 0.90 and 0.95 are read per selectivity. Timings on a shared host are indicative only.
set -u
export PATH="$HOME/dotnet10:$HOME/oneindex/dotnet:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/dotnet10}" DOTNET_CLI_TELEMETRY_OPTOUT=1
dir=$1; count=$2; quant=$3; efs=$4; mode=${5:-table}
out=${ONEINDEX_BENCH_OUT:-$HOME/oneindex/u26/build}

echo "# machine: $(uname -sm), $( (lscpu 2>/dev/null | grep -m1 'Model name' | sed 's/.*: *//') || sysctl -n machdep.cpu.brand_string), $(nproc 2>/dev/null || sysctl -n hw.ncpu) cores"
echo "# dotnet: $(dotnet --version); load before: $(uptime | sed 's/.*load average[s]*: *//')"
dotnet build -c Release -m:2 -v quiet -clp:NoSummary -o "$out/vb" bench/OneIndex.VectorBench/OneIndex.VectorBench.csproj >/dev/null || exit 1
if [ "$mode" = sweep ]; then export FILTERED_ALGOS=acorn+switch; fi
dotnet "$out/vb/OneIndex.VectorBench.dll" --mode filtered --dir "$dir" --count "$count" --queries "${QUERIES:-500}" --quant "$quant" \
  --efs "$efs" --selectivities 0.5,0.1,0.01,0.001 --scratch "${TMPDIR:-$HOME/oneindex/u26}"
echo "# load after: $(uptime | sed 's/.*load average[s]*: *//')"
