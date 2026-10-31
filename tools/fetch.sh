#!/usr/bin/env bash
# Fetch and pin the datasets and comparator tools the relevance and benchmark harnesses use.
#
#   tools/fetch.sh [group ...]     groups: beir ucd snowball wikipedia oracles   (default: all)
#
# Everything lands under $ONEINDEX_DATA (default ~/oneindex): corpus/ for data, oracles/ for comparator tools.
# The script is idempotent: a file whose SHA-256 (and MD5 where pinned) already matches is left alone; a mismatch after a
# download is an error and the file is removed. Nothing here is ever committed or redistributed.
# Only the BEIR sets listed in tools/pins.tsv are fetched; other BEIR sets are not used (their terms do not allow it).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="${ONEINDEX_DATA:-$HOME/oneindex}"
groups=("$@"); [ ${#groups[@]} -eq 0 ] && groups=(beir ucd snowball wikipedia oracles)
mkdir -p "$root/corpus" "$root/oracles"

sha256_of() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }
md5_of() { if command -v md5sum >/dev/null; then md5sum "$1" | cut -d' ' -f1; else md5 -q "$1"; fi; }
wants() { local g; for g in "${groups[@]}"; do [ "$g" = "$1" ] && return 0; done; return 1; }

verify() { # file sha256 md5
  [ -f "$1" ] || return 1
  [ "$(sha256_of "$1")" = "$2" ] || return 1
  [ "$3" = "-" ] || [ "$(md5_of "$1")" = "$3" ]
}

fetch_group() { # group
  local g url dest sha md5 path
  while IFS=$'\t' read -r g dest url sha md5; do
    case "$g" in ''|'#'*) continue ;; esac
    [ "$g" = "$1" ] || continue
    path="$root/$dest"; mkdir -p "$(dirname "$path")"
    if verify "$path" "$sha" "$md5"; then echo "ok      $dest  sha256 $sha"; continue; fi
    echo "fetch   $dest"
    curl -sfL --retry 3 -o "$path.part" "$url"
    mv "$path.part" "$path"
    if ! verify "$path" "$sha" "$md5"; then
      echo "HASH MISMATCH for $dest: got sha256 $(sha256_of "$path") md5 $(md5_of "$path"), expected $sha $md5" >&2
      rm -f "$path"; exit 1
    fi
    echo "ok      $dest  sha256 $sha (downloaded, verified)"
  done < "$here/pins.tsv"
}

if wants beir; then
  fetch_group beir
  for z in "$root"/corpus/beir/*.zip; do d="${z%.zip}"; [ -d "$d" ] || unzip -q "$z" -d "$root/corpus/beir"; done
fi
wants ucd && fetch_group ucd
if wants snowball; then
  fetch_group snowball
  [ -d "$root/corpus/snowball/snowball-data-main" ] || unzip -q "$root/corpus/snowball/snowball-data.zip" -d "$root/corpus/snowball"
fi

# The Python environment for the comparators and the metric cross-check: uv when present, plain venv and pip otherwise.
venv="$root/oracles/venv"
install_requirements() {
  if [ ! -x "$venv/bin/python" ]; then
    if command -v uv >/dev/null; then uv venv -q -p 3.13 "$venv"; else python3 -m venv "$venv"; fi
  fi
  if command -v uv >/dev/null; then VIRTUAL_ENV="$venv" uv pip install -q -r "$here/oracle-requirements.txt"
  else "$venv/bin/python" -m pip install -q -r "$here/oracle-requirements.txt"; fi
}

if wants wikipedia; then
  fetch_group wikipedia
  install_requirements   # pyarrow reads the parquet shards
  out="$root/corpus/wikipedia/abstracts-20231101.en-00000-00001.jsonl"
  if [ ! -f "$out" ]; then
    "$venv/bin/python" "$here/wikipedia_abstracts.py" --out "$out" "$root"/corpus/wikipedia/train-0000{0,1}-of-00041.parquet
  fi
  echo "abstracts  sha256 $(sha256_of "$out")  $(wc -l < "$out") lines"
  # The generator is deterministic, so this is the same on every machine.
  [ "$(sha256_of "$out")" = 1759e6517a7251ef3443b1419ec1f44f88c8bd3792310b4771fc2b5052c24893 ] || { echo "abstracts file differs from the pinned build" >&2; exit 1; }
fi

if wants oracles; then
  case "$(uname -s)-$(uname -m)" in
    Linux-x86_64)
      fetch_group sqlite-linux-x64
      s="$root/oracles/sqlite-3540100"
      [ -x "$s/sqlite3" ] || { mkdir -p "$s"; unzip -qo "$root/oracles/sqlite-tools-linux-x64-3540100.zip" -d "$s"; chmod +x "$s/sqlite3"; }
      echo "sqlite  $("$s/sqlite3" --version | cut -d' ' -f1-2)" ;;
    *) echo "skip    SQLite tools: no pinned build for $(uname -s)-$(uname -m); the system sqlite3 serves information-only rows" ;;
  esac
  install_requirements
  "$venv/bin/python" -c 'import tantivy, hnswlib, faiss, ir_measures, pytrec_eval; print("oracles import ok")'
  "$venv/bin/python" - <<'PY'
from importlib.metadata import version
for name in ("tantivy", "hnswlib", "faiss-cpu", "ir_measures", "pytrec_eval-terrier", "pyarrow"):
    print(f"{name}=={version(name)}")
PY
fi
