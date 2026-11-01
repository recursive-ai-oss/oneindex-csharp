#!/usr/bin/env bash
# Model preparation and measurement steps for the default embedder. Run from the repository root.
#
#   tools/models/gate.sh setup                      venv, download + hash-check the model, quantize, convert, build the tool
#   tools/models/gate.sh reference                  tokenizer ids and ONNX Runtime reference embeddings (needs the BEIR sets)
#   tools/models/gate.sh parity <variant>           our vectors against the reference; variant: model | model.int8 | ...
#   tools/models/gate.sh beir <set> <variant> [on|off]   dense retrieval on a BEIR set; resumable (exit 3 = run again)
#
# Everything lands under $ONEINDEX_DATA (default ~/oneindex): venvs/models, models/, work/models/. Needs `uv`, a .NET 10 SDK and
# the BEIR sets fetched by tools/fetch.sh. Nothing is committed or redistributed from there.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
data="${ONEINDEX_DATA:-$HOME/oneindex}"
venv="$data/venvs/models"
models="$data/models"
work="$data/work/models"
art="${ONEINDEX_ARTIFACTS:-$work/artifacts}"
export ONEINDEX_ARTIFACTS="$art"
mkdir -p "$models" "$work"
tool="$art/bin/OneIndex.Models.Tool/release/oneindex-models.dll"
threads="${ONEINDEX_THREADS:-8}"

build_tool() { dotnet build -c Release -m:4 -v quiet -clp:NoSummary "$root/tools/OneIndex.Models.Tool"; }

case "${1:?step}" in
  setup)
    [ -x "$venv/bin/python" ] || uv venv "$venv" --python 3.13
    uv pip install --python "$venv/bin/python" -r "$root/tools/models/requirements.txt"
    "$venv/bin/python" "$root/tools/models/prepare_bge_small.py" "$models/prepared"
    build_tool
    for v in model model.int8 model.int8dyn; do
      dotnet "$tool" convert "$models/prepared/$v.onnx" "$models/prepared/$v.onemodel"
    done ;;
  reference)
    "$venv/bin/python" "$root/tools/models/tokenizer_reference.py" "$models/prepared" "$data/corpus/beir" "$models/tokenizer-ref" 2000
    "$venv/bin/python" "$root/tools/models/reference_embeddings.py" "$models/prepared/model.onnx" "$models/prepared/tokenizer.json" \
      "$models/tokenizer-ref/samples.jsonl" "$models/parity-doc" 800
    "$venv/bin/python" "$root/tools/models/reference_embeddings.py" "$models/prepared/model.onnx" "$models/prepared/tokenizer.json" \
      "$models/tokenizer-ref/samples.jsonl" "$models/parity-query" 800 "Represent this sentence for searching relevant passages: " ;;
  parity)
    build_tool
    v="${2:?variant}"
    dotnet "$tool" parity --model "$models/prepared/$v.onemodel" --vocab "$models/prepared/vocab.txt" --texts "$models/parity-doc/texts.jsonl" \
      --reference "$models/parity-doc/reference.f32" --threads "$threads"
    dotnet "$tool" parity --model "$models/prepared/$v.onemodel" --vocab "$models/prepared/vocab.txt" --texts "$models/parity-query/texts.jsonl" \
      --reference "$models/parity-query/reference.f32" --role query --prefix "Represent this sentence for searching relevant passages: " --threads "$threads" ;;
  beir)
    build_tool
    set_name="${2:?set}"; v="${3:?variant}"; instr="${4:-on}"
    dotnet "$tool" beir --model "$models/prepared/$v.onemodel" --vocab "$models/prepared/vocab.txt" --data "$data/corpus/beir" --set "$set_name" \
      --work "$work/beir-$v-$instr" --instruction "$instr" --threads "$threads" --budget-seconds 1200 --run "$work/beir-$v-$instr/$set_name.run" ;;
  *) echo "unknown step" >&2; exit 2 ;;
esac
