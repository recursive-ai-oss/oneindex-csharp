#!/usr/bin/env bash
# The durability campaigns of the milestone, one phase per call so that each fits a time-limited job. Every phase runs several
# processes side by side (each with its own seeds and directory) and prints the summary lines; the exit code is non-zero on any failure.
#
#   tools/durability_phase.sh <phase> <work dir> [procs] [first seed]
#
# phases: sweep (fault injection at every storage operation), evolve-sweep (the same during a schema rebuild), kill (writer killed with SIGKILL),
# evolve-kill (the same during a schema rebuild), procs (one writer, four readers, killed at random), sparse-kill, embed-kill.
# Sizes per process come from the environment: SWEEP_WORKLOADS, SWEEP_CRASH_SEEDS (default 4 and 4), KILLS (default 1000), PROCS_KILLS (default 250).
set -u
phase=${1:?phase}; work=${2:?work dir}; procs=${3:-4}; seed0=${4:-1}
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; repo="$(dirname "$here")"
for d in "$HOME/dotnet10" "$HOME/oneindex/dotnet"; do [ -d "$d" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d" && break; done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 MSBUILDDISABLENODEREUSE=1
export ONEINDEX_ARTIFACTS="$work/artifacts" TMPDIR="$work/tmp"
mkdir -p "$work/tmp" "$work/logs"
dotnet build -m:4 "$repo/tests/Durability" -c Release -v quiet -clp:NoSummary || exit 1
dll="$work/artifacts/bin/Durability/release/Durability.dll"
cd "$work" || exit 1
echo "# $(date -u +%FT%TZ) phase $phase procs $procs seed $seed0 load: $(uptime | sed 's/.*load average[s]*: *//')"
pids=()
for ((i = 0; i < procs; i++)); do
  s=$((seed0 + i * 1000))
  d="$work/tmp/$phase-$i"; rm -rf "$d"
  case "$phase" in
    sweep) dotnet "$dll" sweep --first-seed "$((seed0 + i * 10))" --workloads "${SWEEP_WORKLOADS:-4}" --crash-seeds "${SWEEP_CRASH_SEEDS:-4}" ;;
    evolve-sweep) dotnet "$dll" evolve-sweep --first-seed "$((seed0 + i * 10))" --workloads "${SWEEP_WORKLOADS:-4}" --crash-seeds "${SWEEP_CRASH_SEEDS:-3}" ;;
    kill) dotnet "$dll" kill --dir "$d" --kills "${KILLS:-1000}" --seed "$s" --clear true ;;
    evolve-kill) dotnet "$dll" kill --dir "$d" --kills "${KILLS:-250}" --seed "$s" --evolve true --clear true ;;
    procs) dotnet "$dll" procs --dir "$d" --kills "${PROCS_KILLS:-250}" --seed "$s" ;;
    sparse-kill) dotnet "$dll" sparse-kill --dir "$d" --kills "${KILLS:-50}" --seed "$s" --clear true ;;
    embed-kill) dotnet "$dll" embed-kill --dir "$d" --kills "${KILLS:-50}" --seed "$s" --clear true ;;
    *) echo "unknown phase" >&2; exit 2 ;;
  esac > "$work/logs/$phase-$i.log" 2>&1 &
  pids+=($!)
done
rc=0
for p in "${pids[@]}"; do wait "$p" || rc=1; done
for ((i = 0; i < procs; i++)); do grep -E '^(sweep|evolve-sweep|kill|sparse-kill|embed-kill|procs|  )' "$work/logs/$phase-$i.log" | head -12; tail -2 "$work/logs/$phase-$i.log" | grep -vE '^(sweep|evolve-sweep|kill|sparse-kill|embed-kill)' ; done
echo "# $(date -u +%FT%TZ) exit $rc load: $(uptime | sed 's/.*load average[s]*: *//')"
rm -rf "$work"/tmp/"$phase"-*
exit $rc
