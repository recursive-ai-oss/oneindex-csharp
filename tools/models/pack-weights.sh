#!/usr/bin/env bash
# Copies the converted default model next to the build output so that the next build of src/OneIndex.Models embeds it.
#
#   tools/models/pack-weights.sh [variant]        variant: int8 (default) | fp32 | reranker (the cross-encoder, int8) | e5 (multilingual-e5-small, int8, for OneIndex.Models.Multilingual)
#
# Run `tools/models/gate.sh setup` first. The weights are never committed; this only puts them where OneIndex.Models.csproj looks
# ($(ONEINDEX_ARTIFACTS)/model-weights/bge-small-en-v1.5, or OneIndexModelWeights).
set -euo pipefail
data="${ONEINDEX_DATA:-$HOME/oneindex}"
variant="${1:-int8}"
if [ "$variant" = reranker ]; then
  src="$data/models/ce-prepared"
  out="${OneIndexRerankerWeights:-${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS or OneIndexRerankerWeights}/model-weights/ms-marco-MiniLM-L6-v2}"
  mkdir -p "$out"
  cp "$src/model.int8.onemodel" "$out/model.onemodel"
  cp "$src/vocab.txt" "$src/LICENSE-ms-marco-MiniLM-L6-v2.txt" "$out/"
  sha256sum "$out/model.onemodel" "$out/vocab.txt"
  exit 0
fi
if [ "$variant" = e5 ]; then
  # multilingual-e5-small, int8: packed by OneIndex.Models.Multilingual.csproj (OneIndexE5Weights).
  src="$data/models/e5"
  out="${OneIndexE5Weights:-${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS or OneIndexE5Weights}/model-weights/multilingual-e5-small}"
  mkdir -p "$out"
  cp "$src/model.int8.onemodel" "$out/model.onemodel"
  cp "$src/sentencepiece.bpe.model" "$src/LICENSE-multilingual-e5-small.txt" "$out/"
  sha256sum "$out/model.onemodel" "$out/sentencepiece.bpe.model"
  exit 0
fi
src="$data/models/prepared"
case "$variant" in int8) file=model.int8.onemodel ;; fp32) file=model.onemodel ;; *) echo "unknown variant" >&2; exit 2 ;; esac
out="${OneIndexModelWeights:-${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS or OneIndexModelWeights}/model-weights/bge-small-en-v1.5}"
mkdir -p "$out"
cp "$src/$file" "$out/model.onemodel"
cp "$src/vocab.txt" "$src/LICENSE-bge-small-en-v1.5.txt" "$out/"
sha256sum "$out/model.onemodel" "$out/vocab.txt"
