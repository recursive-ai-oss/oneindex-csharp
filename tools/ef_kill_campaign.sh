#!/usr/bin/env bash
# Builds the Entity Framework kill harness and runs LANES campaigns of CYCLES cycles in parallel, each in its own directory.
# usage: tools/ef_kill_campaign.sh <work dir> <first seed> <lanes> <cycles>
# With EF_PG set to a PostgreSQL connection string without a database (for example "Host=/path/to/socket;Username=postgres") the lanes run on
# PostgreSQL, one database per lane, with EF_WRITERS concurrent writers in each application process (default 2); otherwise on SQLite.
set -u
WORK=${1:?work dir}; SEED=${2:-1}; LANES=${3:-4}; CYCLES=${4:-250}
HERE=$(cd "$(dirname "$0")/.." && pwd)
export ONEINDEX_ARTIFACTS=$WORK/artifacts TMPDIR=$WORK/tmp
mkdir -p "$WORK/tmp" "$WORK/logs"
dotnet build -m:4 "$HERE/tests/EfDurability" -c Release -v quiet -clp:NoSummary || exit 1
DLL=$(find "$WORK/artifacts/bin/EfDurability" -name EfDurability.dll | head -1)
pids=()
for ((i = 0; i < LANES; i++)); do
  s=$((SEED + i))
  rm -rf "$WORK/tmp/lane$s"
  dotnet "$DLL" kill --dir "$WORK/tmp/lane$s" --cycles "$CYCLES" --seed "$s" --pg "${EF_PG:-}" --writers "${EF_WRITERS:-2}" --hook "${EF_HOOK:-55}" --hold "${EF_HOLD:-0}" > "$WORK/logs/lane$s.log" 2>&1 &
  pids+=($!)
done
rc=0
for p in "${pids[@]}"; do wait "$p" || rc=1; done
cat "$WORK"/logs/lane*.log | grep -E '^(cycles|FAIL|cycle|gap handling|  stage)' | head -60
exit $rc
