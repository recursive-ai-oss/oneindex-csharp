#!/usr/bin/env bash
# Dense retrieval and the default hybrid on one BEIR set with the shipped int8 weights: embeds the corpus in resumable chunks
# (gate.sh beir; exit code 3 means the time budget ran out, run it again), then runs BM25, exact dense and hybrid through the
# engine's query objects (the model tool's `hybrid` command) and cross-checks the run files with pytrec_eval.
#
#   tools/models/milestone_dense.sh <set>
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
set_name=${1:?set}
[ -d "$HOME/dotnet10" ] && export PATH="$HOME/dotnet10:$PATH" DOTNET_ROOT="$HOME/dotnet10"
export ONEINDEX_ARTIFACTS="${ONEINDEX_ARTIFACTS:-$HOME/oneindex/work/models/artifacts}" ONEINDEX_THREADS="${ONEINDEX_THREADS:-8}"
data="${ONEINDEX_DATA:-$HOME/oneindex}"; models="$data/models"; work="$data/work/models"
bash "$here/gate.sh" beir "$set_name" model.int8 on
tool="$ONEINDEX_ARTIFACTS/bin/OneIndex.Models.Tool/release/oneindex-models.dll"
out="${ONEINDEX_RESULTS:-$data/results/models}/$set_name"
dotnet "$tool" hybrid --model "$models/prepared/model.int8.onemodel" --vocab "$models/prepared/vocab.txt" --data "$data/corpus/beir" --set "$set_name" \
  --work "$work/beir-model.int8-on" --instruction on --threads "$ONEINDEX_THREADS" --out "$out"
ls "$out"
