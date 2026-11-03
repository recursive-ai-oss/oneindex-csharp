#!/usr/bin/env bash
# Downloads the packed model weights for a release build from the repository's release assets, checks their SHA-256 against
# tools/models/weights.sha256, and unpacks them where the .Models projects look ($ONEINDEX_ARTIFACTS/model-weights).
#
#   tools/models/fetch-release-weights.sh [release tag]     (default: the tag in tools/models/weights.tag)
#
# Needs the GitHub CLI and GH_TOKEN. The weights are never committed.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
tag="${1:-$(tr -d '[:space:]' < "$here/weights.tag")}"
want=$(awk '{print $1}' "$here/weights.sha256")
artifacts="${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS}"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
gh release download "$tag" --repo "${GITHUB_REPOSITORY:?set GITHUB_REPOSITORY}" --pattern model-weights.tar --dir "$tmp"
got=$(sha256sum "$tmp/model-weights.tar" | awk '{print $1}')
if [ "$got" != "$want" ]; then
  echo "model-weights.tar has SHA-256 $got, expected $want" >&2
  exit 1
fi
mkdir -p "$artifacts"
tar -C "$artifacts" -xf "$tmp/model-weights.tar"
for d in bge-small-en-v1.5 ms-marco-MiniLM-L6-v2 neural-sparse multilingual-e5-small; do
  test -s "$artifacts/model-weights/$d/model.onemodel" || { echo "missing $d in the bundle" >&2; exit 1; }
done
echo "weights unpacked into $artifacts/model-weights"
