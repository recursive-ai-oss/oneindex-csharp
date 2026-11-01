#!/usr/bin/env bash
# Copies the converted default model next to the build output so that the next build of src/OneIndex.Models embeds it.
#
#   tools/models/pack-weights.sh [variant]        variant: int8 (default) | fp32
#
# Run `tools/models/gate.sh setup` first. The weights are never committed; this only puts them where OneIndex.Models.csproj looks
# ($(ONEINDEX_ARTIFACTS)/model-weights/bge-small-en-v1.5, or OneIndexModelWeights).
set -euo pipefail
data="${ONEINDEX_DATA:-$HOME/oneindex}"
variant="${1:-int8}"
src="$data/models/prepared"
case "$variant" in int8) file=model.int8.onemodel ;; fp32) file=model.onemodel ;; *) echo "unknown variant" >&2; exit 2 ;; esac
out="${OneIndexModelWeights:-${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS or OneIndexModelWeights}/model-weights/bge-small-en-v1.5}"
mkdir -p "$out"
cp "$src/$file" "$out/model.onemodel"
cp "$src/vocab.txt" "$src/LICENSE-bge-small-en-v1.5.txt" "$out/"
sha256sum "$out/model.onemodel" "$out/vocab.txt"
