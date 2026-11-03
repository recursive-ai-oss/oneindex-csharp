#!/usr/bin/env bash
# Preparation and measurement steps for the multilingual embedder (multilingual-e5-small). Run from the repository root.
#
#   tools/models/e5_gate.sh setup               download + hash-check the model, quantize, convert (needs the venv of tools/models/requirements.txt)
#   tools/models/e5_gate.sh reference <texts>   ONNX Runtime reference embeddings of 800 texts (JSON strings, one per line; tools/models/spm_sample.py)
#   tools/models/e5_gate.sh parity <variant>    our vectors against the reference; variant: model | model.int8
#   tools/models/e5_gate.sh beir <set> <variant>   dense retrieval on a BEIR set; resumable (exit 3 = run again)
#
# Everything lands under $ONEINDEX_DATA (default ~/oneindex): venvs/models, models/e5, work/e5. Nothing is committed from there.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
data="${ONEINDEX_DATA:-$HOME/oneindex}"
venv="$data/venvs/models"
models="$data/models/e5"
work="$data/work/e5"
art="${ONEINDEX_ARTIFACTS:-$data/artifacts}"
export ONEINDEX_ARTIFACTS="$art"
mkdir -p "$models" "$work"
tool="$art/bin/OneIndex.Models.Tool/release/oneindex-models.dll"
threads="${ONEINDEX_THREADS:-8}"
build_tool() { dotnet build -c Release -m:4 -v quiet -clp:NoSummary "$root/tools/OneIndex.Models.Tool"; }

case "${1:?step}" in
  setup)
    [ -x "$venv/bin/python" ] || uv venv "$venv" --python 3.13
    uv pip install --python "$venv/bin/python" -r "$root/tools/models/requirements.txt"
    "$venv/bin/python" "$root/tools/models/prepare_e5_small.py" "$models"
    build_tool
    for v in model model.int8; do
      dotnet "$tool" convert "$models/$v.onnx" "$models/$v.onemodel"
    done ;;
  reference)
    "$venv/bin/python" "$root/tools/models/e5_reference.py" "$models/model.onnx" "$models/tokenizer.json" "${2:?texts file}" "$models/parity" 800 ;;
  parity)
    build_tool
    v="${2:?variant}"
    dotnet "$tool" parity --model "$models/$v.onemodel" --spm "$models/sentencepiece.bpe.model" --texts "$models/parity/texts.jsonl" \
      --reference "$models/parity/reference.f32" --threads "$threads" ;;
  beir)
    build_tool
    set_name="${2:?set}"; v="${3:?variant}"
    dotnet "$tool" beir --model "$models/$v.onemodel" --spm "$models/sentencepiece.bpe.model" --data "$data/corpus/beir" --set "$set_name" \
      --work "$work/beir-$v" --threads "$threads" --budget-seconds 1200 --run "$work/beir-$v/$set_name.run" ;;
  *) echo "unknown step" >&2; exit 2 ;;
esac
