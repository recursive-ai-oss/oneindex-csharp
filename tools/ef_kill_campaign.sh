#!/usr/bin/env bash
# Builds the Entity Framework kill harness and runs LANES campaigns of CYCLES cycles in parallel, each in its own directory.
# EF_RETAIN_BASE (default 5) and EF_WINDOW (default 10000) set the retention rows of the application processes (base plus 0-59) and their late-row window.
# Retention below the window is a stress setting: a transaction open across it can lose its log row, so a campaign that is meant to pass uses a base at or above the window.
# A mismatch of index and database found by a check is compared again after a second catch-up (RECHECK lines; the summary counts first-pass mismatches,
# those cleared by the recheck, and those confirmed). A lost acknowledged commit or a failed open is never rechecked.
# usage: tools/ef_kill_campaign.sh <work dir> <first seed> <lanes> <cycles>
# With EF_PG set to a PostgreSQL connection string without a database (for example "Host=/path/to/socket;Username=postgres") the lanes run on
# PostgreSQL, one database per lane, with EF_WRITERS concurrent writers in each application process (default 2); otherwise on SQLite.
set -u
WORK=${1:?work dir}; SEED=${2:-1}; LANES=${3:-4}; CYCLES=${4:-250}
HERE=$(cd "$(dirname "$0")/.." && pwd)
for d in "$HOME/dotnet10" "$HOME/oneindex/dotnet"; do [ -d "$d" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d" && break; done
export ONEINDEX_ARTIFACTS=$WORK/artifacts TMPDIR=$WORK/tmp
mkdir -p "$WORK/tmp" "$WORK/logs"
dotnet build -m:4 "$HERE/tests/EfDurability" -c Release -v quiet -clp:NoSummary || exit 1
DLL=$(find "$WORK/artifacts/bin/EfDurability" -name EfDurability.dll | head -1)
pids=()
for ((i = 0; i < LANES; i++)); do
  s=$((SEED + i))
  rm -rf "$WORK/tmp/lane$s"
  dotnet "$DLL" kill --dir "$WORK/tmp/lane$s" --cycles "$CYCLES" --seed "$s" --pg "${EF_PG:-}" --writers "${EF_WRITERS:-2}" --hook "${EF_HOOK:-55}" --hold "${EF_HOLD:-0}" --retain-base "${EF_RETAIN_BASE:-5}" --window "${EF_WINDOW:-10000}" > "$WORK/logs/lane$s.log" 2>&1 &
  pids+=($!)
done
rc=0
for p in "${pids[@]}"; do wait "$p" || rc=1; done
cat "$WORK"/logs/lane*.log | grep -E '^(cycles|FAIL|RECHECK|cycle|gap handling|  stage)' | head -60
exit $rc
