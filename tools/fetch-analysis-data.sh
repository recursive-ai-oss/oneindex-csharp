#!/usr/bin/env bash
# Fetches the Unicode and Snowball files the analysis generators and conformance tests read, and checks their SHA-256.
#
#   tools/fetch-analysis-data.sh <cache directory>
#
# Creates <cache>/ucd (the UCD 17.0.0 files; point ONEINDEX_UCD_DIR at it), <cache>/snowball-data-main (point
# ONEINDEX_SNOWBALL_DATA at it) and <cache>/stop (the stop word lists tools/generate-stopwords.py reads). Nothing is committed.
set -euo pipefail
cache="${1:?usage: fetch-analysis-data.sh <cache directory>}"
mkdir -p "$cache/ucd" "$cache/stop"
base=https://www.unicode.org/Public/17.0.0/ucd

fetch() { # <path under the UCD> <sha256>
  local name
  name="$(basename "$1")"
  [[ -f "$cache/ucd/$name" ]] || curl -fsSL "$base/$1" -o "$cache/ucd/$name"
  echo "$2  $cache/ucd/$name" | sha256sum -c --quiet -
}

fetch CaseFolding.txt ff8d8fefbf123574205085d6714c36149eb946d717a0c585c27f0f4ef58c4183
fetch emoji/emoji-data.txt 2cb2bb9455cda83e8481541ecf5b6dfda66a3bb89efa3fa7c5297eccf607b72b
fetch Scripts.txt 9f5e50d3abaee7d6ce09480f325c706f485ae3240912527e651954d2d6b035bf
fetch auxiliary/SentenceBreakProperty.txt 871c0c985ad95125e25b302414065a10839d068970bceb383ecec138f22a0a18
fetch UnicodeData.txt 2e1efc1dcb59c575eedf5ccae60f95229f706ee6d031835247d843c11d96470c
fetch auxiliary/WordBreakProperty.txt 72274cac1e6b919507db35655c3e175aa27274668a1ece95c28d2069f2ad9852
fetch DerivedNormalizationProps.txt 71fd6a206a2c0cdd41feb6b7f656aa31091db45e9cedc926985d718397f9e488
fetch NormalizationTest.txt 5019ffd530751a741900c849c0e010332f142a3612234639bd200b82138a87db
fetch auxiliary/WordBreakTest.txt 1de23a75f37904abc7d206239ee8d34f8fdf0fb4ab32a7174dfbabbde25419b2
fetch auxiliary/SentenceBreakTest.txt 12cb47d028ded0c1cb8a28558f95479cbcd24559c46977015c82f3b50a1cc6e4

# Snowball's published vocabularies (fetch-only; licences are per language directory).
zip=5bacb3c4f822d167707d87e08e807f65c27ac517543ef57fe4a2bfcf5bfcb7ed
if [[ ! -d "$cache/snowball-data-main" ]]; then
  curl -fsSL https://codeload.github.com/snowballstem/snowball-data/zip/refs/heads/main -o "$cache/snowball-data.zip"
  echo "$zip  $cache/snowball-data.zip" | sha256sum -c --quiet -
  unzip -q "$cache/snowball-data.zip" -d "$cache"
fi

# Stop word lists: https://snowballstem.org/algorithms/<language>/stop.txt
for l in danish dutch english finnish french german hungarian indonesian irish italian norwegian portuguese russian sesotho spanish swedish; do
  [[ -f "$cache/stop/$l.txt" ]] || curl -fsSL "https://snowballstem.org/algorithms/$l/stop.txt" -o "$cache/stop/$l.txt"
done
echo "ucd: $cache/ucd  snowball-data: $cache/snowball-data-main  stop lists: $cache/stop"
