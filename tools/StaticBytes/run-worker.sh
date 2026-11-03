#!/usr/bin/env bash
# Builds the browser fixture (30,000 abstracts by default) once and prints the bytes a static index fetches per query kind.
#
#   run-worker.sh <abstracts.jsonl> <work dir> [docs] [chunk bytes]
#
# Needs the .NET SDK on PATH. The fixture is reused when the work directory already has one for the same arguments.
set -euo pipefail
corpus=$1; work=$2; docs=${3:-30000}; chunk=${4:-16384}
root=$(cd "$(dirname "$0")/../.." && pwd)
export ONEINDEX_ARTIFACTS="$work/artifacts"
mkdir -p "$work"
cd "$root"
for p in BrowserFixture StaticBytes; do dotnet build tools/$p -c Release -m:4 -v quiet -clp:NoSummary; done
bf="$work/bf-$docs-$chunk"
if [ ! -d "$bf" ]; then
  dotnet "$ONEINDEX_ARTIFACTS/bin/BrowserFixture/release/BrowserFixture.dll" --out "$bf" --corpus "$corpus" --docs "$docs" --chunk "$chunk"
fi
ls "$bf"
dotnet "$ONEINDEX_ARTIFACTS/bin/StaticBytes/release/StaticBytes.dll" "$bf/static" --index "$bf/index" --hybrid-nprobe "${HYBRID_NPROBE:-16}"
