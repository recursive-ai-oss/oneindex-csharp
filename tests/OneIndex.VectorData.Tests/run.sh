#!/usr/bin/env bash
# Runs the contract tests and the shared conformance suite, leaving out the documented unsupported cases.
# Usage: run.sh <path to OneIndex.VectorData.Tests.dll>
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
args=()
while IFS= read -r line; do
  case "$line" in ''|'#'*) ;; *) args+=(-method- "$line") ;; esac
done < "$here/known-unsupported.txt"
exec dotnet "$1" "${args[@]}"
