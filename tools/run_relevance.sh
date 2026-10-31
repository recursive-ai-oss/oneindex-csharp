#!/usr/bin/env bash
# Build the harness, run the BEIR relevance runner for the named datasets with the chosen engine, and cross-check every run
# file against pytrec_eval.
#
#   tools/run_relevance.sh <engine> <comma-separated sets> [threads]     e.g. tools/run_relevance.sh lucene scifact,arguana,scidocs 8
#
# Data comes from $ONEINDEX_DATA (default ~/oneindex; fetched by tools/fetch.sh); results go to $ONEINDEX_RESULTS
# (default $ONEINDEX_DATA/results/relevance). Needs a .NET 10 SDK on PATH (or at ~/dotnet10).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
engine=$1; sets=$2; threads=${3:-4}
root="${ONEINDEX_DATA:-$HOME/oneindex}"
results="${ONEINDEX_RESULTS:-$root/results/relevance}"
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEINDEX_ARTIFACTS="${ONEINDEX_ARTIFACTS:-$root/build}"
export TMPDIR="${TMPDIR:-$root/tmp}"; mkdir -p "$TMPDIR" "$results" "$root/work"

dotnet --version
dotnet build -c Release "$repo/tools/OneIndex.Harness.Cli" -v quiet -clp:NoSummary
harness="$ONEINDEX_ARTIFACTS/bin/OneIndex.Harness.Cli/release/oneindex-harness"
"$harness" beir --engine "$engine" --data "$root/corpus/beir" --sets "$sets" --out "$results" --work "$root/work" --threads "$threads"

status=0
for s in ${sets//,/ }; do
  echo "== pytrec_eval cross-check: $s"
  "$root/oracles/venv/bin/python" "$here/crosscheck_ndcg.py" "$root/corpus/beir/$s" "$results/$s/$engine/summary.json" || status=1
done
exit $status
