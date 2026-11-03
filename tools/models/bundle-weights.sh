#!/usr/bin/env bash
# Builds the release bundle of packed model weights: one tar of the four directories the .Models projects embed, byte-for-byte
# reproducible from the converted models (sorted names, fixed owner and times). Prints its SHA-256.
#
#   tools/models/bundle-weights.sh <output.tar>
#
# Run tools/models/gate.sh setup (and the prepare_*.py scripts) first; the weights are never committed. Upload the tar as a release asset
# named model-weights.tar and put its SHA-256 in tools/models/weights.sha256, which the release workflow checks.
set -euo pipefail
out="${1:?usage: bundle-weights.sh <output.tar>}"
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
stage=$(mktemp -d "${TMPDIR:-/var/tmp}/oneindex-weights.XXXXXX")
trap 'rm -rf "$stage"' EXIT
root="$stage/model-weights"
OneIndexModelWeights="$root/bge-small-en-v1.5" "$here/pack-weights.sh" int8 >/dev/null
OneIndexRerankerWeights="$root/ms-marco-MiniLM-L6-v2" "$here/pack-weights.sh" reranker >/dev/null
OneIndexE5Weights="$root/multilingual-e5-small" "$here/pack-weights.sh" e5 >/dev/null
OneIndexSparseWeights="$root/neural-sparse" "$here/pack-sparse-weights.sh" int8 >/dev/null
tar --sort=name --mtime='2000-01-01 00:00:00Z' --owner=0 --group=0 --numeric-owner -C "$stage" -cf "$out" model-weights
sha256sum "$out"
