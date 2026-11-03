#!/usr/bin/env bash
# One repetition of the text rows: OneIndex, Lucene.NET, then OneIndex again for the matched-semantics as-you-type typo row, each on
# a freshly built index of the whole abstract corpus, one after another so each runs alone. Writes JSON to <out>/<engine>-r<rep>.json.
#
#   tools/bench_text_run.sh <out dir> <rep> [docs]
#
# Data from $ONEINDEX_DATA (default ~/oneindex). Works on Linux and macOS; needs a .NET 10 SDK on PATH or at ~/dotnet10 or ~/oneindex/dotnet.
# Publishable only when the machine is otherwise idle (see docs/benchmarks.md); the result files record the load before, during and after.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(dirname "$here")"
out=${1:?out dir}; rep=${2:?rep}; docs=${3:-302567}
root="${ONEINDEX_DATA:-$HOME/oneindex}"
corpus="$root/corpus/wikipedia/abstracts-20231101.en-00000-00001.jsonl"
for d in "$HOME/dotnet10" "$HOME/oneindex/dotnet"; do [ -d "$d" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d" && break; done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEINDEX_ARTIFACTS="${ONEINDEX_ARTIFACTS:-$root/build}"
export TMPDIR="${TMPDIR:-$root/tmp}"; mkdir -p "$TMPDIR" "$out"
work="${ONEINDEX_WORK:-$root/work}/bench-text"

[ -x "$ONEINDEX_ARTIFACTS/bin/OneIndex.Harness.Cli/release/oneindex-harness" ] || dotnet build -c Release -m:2 "$repo/tools/OneIndex.Harness.Cli" -v quiet -clp:NoSummary
harness="$ONEINDEX_ARTIFACTS/bin/OneIndex.Harness.Cli/release/oneindex-harness"
echo "# $(date -u +%FT%TZ) rep $rep docs $docs load: $(uptime | sed 's/.*load average[s]*: *//')"
run() { # engine result-name [rows]
  rm -rf "$work"; mkdir -p "$work"
  "$harness" driver --engine "$1" --corpus "$corpus" --docs "$docs" --work "$work" --out "$out/$2-r$rep.json" --threads 1 ${3:+--rows "$3"}
  echo "# done $2 $(date -u +%T) load: $(uptime | sed 's/.*load average[s]*: *//')"
}
run oneindex oneindex
run lucene lucene
ONEINDEX_TYPO_MATCHED=1 run oneindex oneindex-matched TypoAsYouType
rm -rf "$work"
