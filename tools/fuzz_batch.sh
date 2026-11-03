#!/usr/bin/env bash
# Run fuzz targets one after another, each for a fixed time, with its own work directory so
# batches can run side by side.
#
#   tools/fuzz_batch.sh <seconds> <target>...
#
# Environment: FUZZ_HOME (default ~/oneindex/fuzz) holds one folder per target (build, corpus, findings, log);
# ONEINDEX_FUZZ_TOOLS must point at the libFuzzer host and the instrumenter (see fuzz/README.md).
# Prints one summary line per target: executions, exec/s, features, findings.
set -uo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
seconds="${1:?usage: fuzz_batch.sh <seconds> <target>...}"
shift
home="${FUZZ_HOME:-$HOME/oneindex/fuzz}"
status=0
for t in "$@"; do
  work="$home/$t"
  mkdir -p "$work"
  ONEINDEX_FUZZ_WORK="$work" "$root/fuzz/run.sh" "$t" "$seconds" >"$work/run.log" 2>&1
  code=$?
  last="$(grep -E '^#[0-9]+' "$work/run.log" | tail -1)"
  stats="$(grep -E 'stat::number_of_executed_units|stat::average_exec_per_sec' "$work/run.log" | tr '\n' ' ')"
  n="$(find "$work/findings/$t" -type f 2>/dev/null | wc -l)"
  echo "$t exit=$code findings=$n | $stats | $last"
  [[ $code -eq 0 && $n -eq 0 ]] || status=1
done
exit $status
