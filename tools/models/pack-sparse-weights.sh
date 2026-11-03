#!/usr/bin/env bash
# Copies the converted sparse model next to the build output so that the next build of src/OneIndex.Models embeds it.
#
#   tools/models/pack-sparse-weights.sh [variant]     variant: int8 (default) | fp32
#
# Run tools/models/prepare_neural_sparse.py and the converter first. The weights are never committed.
set -euo pipefail
data="${ONEINDEX_DATA:-$HOME/oneindex}"
variant="${1:-int8}"
src="$data/models/prepared-sparse"
case "$variant" in int8) file=model.int8.onemodel ;; fp32) file=model.onemodel ;; *) echo "unknown variant" >&2; exit 2 ;; esac
out="${OneIndexSparseWeights:-${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS or OneIndexSparseWeights}/model-weights/neural-sparse}"
mkdir -p "$out"
cp "$src/$file" "$out/model.onemodel"
cp "$src/vocab.txt" "$src/idf.json" "$out/"
cp "$src/hf/LICENSE" "$out/LICENSE-neural-sparse.txt" 2>/dev/null || printf 'Apache License 2.0, see https://www.apache.org/licenses/LICENSE-2.0\n' > "$out/LICENSE-neural-sparse.txt"
sha256sum "$out/model.onemodel" "$out/vocab.txt" "$out/idf.json"
