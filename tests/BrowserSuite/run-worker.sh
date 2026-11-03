#!/usr/bin/env bash
# Runs the whole browser comparison on one machine: builds the fixture index from an abstracts file, publishes the browser app,
# runs the Chromium suite, then measures Pagefind on the same abstracts.
#
#   run-worker.sh <abstracts.jsonl> <docs> <work dir> <node/npm prefix with playwright-core and pagefind> <chrome>
#
# Needs: the .NET SDK with the wasm-tools workload on PATH, node on PATH.
set -euo pipefail
corpus=$1; docs=$2; work=$3; pw=$4; chrome=${5:-/usr/bin/google-chrome}
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
mkdir -p "$work"
export ONEINDEX_ARTIFACTS="$work/artifacts"

cd "$root"
dotnet build tools/BrowserFixture -c Release -m:4 -v quiet -clp:NoSummary
dotnet "$ONEINDEX_ARTIFACTS/bin/BrowserFixture/release/BrowserFixture.dll" --out "$work/bf" --corpus "$corpus" --docs "$docs"
dotnet publish tests/BrowserSuite/BrowserApp -c Release -m:4 -v quiet -clp:NoSummary 2>&1 | tee "$work/publish.log" | tail -20
app="$ONEINDEX_ARTIFACTS/bin/BrowserApp/release_browser-wasm/AppBundle"
du -sb "$app" | cut -f1 > "$work/app-bytes.txt"
node - "$app" > "$work/app-sizes.json" <<'JS'
const fs = require('fs'), path = require('path'), zlib = require('zlib')
const dir = process.argv[2]
let raw = 0, gz = 0, br = 0
for (const f of fs.readdirSync(dir, { recursive: true })) {
  const p = path.join(dir, f)
  if (!fs.statSync(p).isFile() || /\.(pdb|map)$/.test(p)) continue
  const b = fs.readFileSync(p)
  raw += b.length; gz += zlib.gzipSync(b, { level: 9 }).length
  br += zlib.brotliCompressSync(b, { params: { [zlib.constants.BROTLI_PARAM_QUALITY]: 11 } }).length
}
console.log(JSON.stringify({ rawBytes: raw, gzipBytes: gz, brotliBytes: br }))
JS
cat "$work/app-sizes.json"
node "$here/run.mjs" --app "$app" --fixture "$work/bf" --playwright "$pw" --chrome "$chrome" --report "$work/browser-report.json" | tail -60
node "$here/pagefind.mjs" --corpus "$corpus" --docs "$docs" --queries "$work/bf/static/queries.json" --pagefind "$pw" --work "$work/pagefind" --chrome "$chrome" --playwright "$pw" --report "$work/pagefind-report.json" | head -40
