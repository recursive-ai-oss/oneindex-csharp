#!/usr/bin/env bash
# Reference encodings of BEIR NQ on a GPU, in resumable chunks (each step skips what is already written).
#
#   tools/models/nq_gpu.sh <seconds> dense|sparse|sample|rerank
#
# Everything lands under $ONEINDEX_DATA/u28 (default ~/oneindex/u28): venv, models (PyTorch weights at the pinned revisions),
# the encodings, and the sub-corpora of the stand-in proof. Needs the NQ set fetched by tools/fetch.sh. The budget bounds the whole call
# in seconds; a step that runs out exits with code 3 (run it again).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data="${ONEINDEX_DATA:-$HOME/oneindex}"
u="$data/u28"
py="$u/venv/bin/python"
nq="$data/corpus/beir/nq"
start=$(date +%s)
left() { echo $(( ${1} - ($(date +%s) - start) )); }
budget="${1:?seconds}"; step="${2:?step}"
case "$step" in
  dense)
    "$py" "$here/gpu_reference.py" dense --data "$nq" --model "$u/models/bge" --out "$u/enc/dense" --budget-seconds "$budget" ;;
  sparse)
    "$py" "$here/gpu_reference.py" sparse --data "$nq" --model "$data/u24/prepared/hf" --out "$u/enc/sparse" --budget-seconds "$budget" ;;
  sample)
    # The sub-corpora of the stand-in proof: 20,000 passages for the dense model, 8,000 for the sparse one, each with every judged passage.
    [ -d "$u/nq-sample" ] || "$py" "$here/make_sample.py" "$nq" "$u/nq-sample" 20000
    [ -d "$u/nq-sample8k" ] || "$py" "$here/make_sample.py" "$nq" "$u/nq-sample8k" 8000
    "$py" "$here/gpu_reference.py" dense --data "$u/nq-sample" --model "$u/models/bge" --out "$u/enc/sample-dense" --budget-seconds "$budget"
    "$py" "$here/gpu_reference.py" sparse --data "$u/nq-sample8k" --model "$data/u24/prepared/hf" --out "$u/enc/sample-sparse" --budget-seconds "$(left "$budget")" ;;
  rerank)
    "$py" "$here/gpu_reference.py" rerank --data "$nq" --model "$u/models/ce" --candidates "$u/work/candidates.jsonl" --out "$u/enc/rerank-scores.jsonl" --budget-seconds "$budget" ;;
  *) echo "unknown step" >&2; exit 2 ;;
esac
