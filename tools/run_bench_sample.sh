#!/usr/bin/env bash
# Indicative benchmark pass on a Wikipedia abstract sample: the corpus driver for one engine, then the native information rows.
#
#   tools/run_bench_sample.sh <engine> <docs> [threads]
#
# Indicative only: whoever starts this decides whether the machine is quiet. Publishable rows are bench-class runs described in
# docs/benchmarks.md. Results go to $ONEINDEX_RESULTS (default $ONEINDEX_DATA/results/bench).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
engine=$1; docs=$2; threads=${3:-1}
root="${ONEINDEX_DATA:-$HOME/oneindex}"
results="${ONEINDEX_RESULTS:-$root/results/bench}"
corpus="$root/corpus/wikipedia/abstracts-20231101.en-00000-00001.jsonl"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEINDEX_ARTIFACTS="${ONEINDEX_ARTIFACTS:-$root/build}"
export TMPDIR="${TMPDIR:-$root/tmp}"; mkdir -p "$TMPDIR" "$results" "$root/work"

dotnet build -c Release "$repo/tools/OneIndex.Harness.Cli" -v quiet -clp:NoSummary
harness="$ONEINDEX_ARTIFACTS/bin/OneIndex.Harness.Cli/release/oneindex-harness"
"$harness" queries --corpus "$corpus" --docs "$docs" --out "$results/queries.json"
"$harness" driver --engine "$engine" --corpus "$corpus" --docs "$docs" --work "$root/work" --out "$results/$engine-$docs.json" --threads "$threads"
if [ "${NATIVE:-0}" = 1 ]; then
  "$root/oracles/venv/bin/python" "$here/native/tantivy_rows.py" "$corpus" "$results/queries.json" "$docs" "$root/work/tantivy" "$results/tantivy-$docs.json"
  "$here/native/fts5_rows.sh" "$corpus" "$results/queries.json" "$docs" "$root/work/fts5" "$results/fts5-$docs.tsv"
fi
