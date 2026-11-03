#!/usr/bin/env bash
# Builds the packed dictionaries (ja ko zh th lo km my .oidict) from the fetched sources into $ONEINDEX_ARTIFACTS/dictionaries,
# where the Asian package's build embeds them. Usage: tools/dictionaries/build.sh [cache-dir]
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
D="${1:-${ONEINDEX_CACHE:-$HOME/oneindex/cache}/corpus/dict}"
OUT="${ONEINDEX_ARTIFACTS:?set ONEINDEX_ARTIFACTS}/dictionaries"
mkdir -p "$OUT"
dotnet build -c Release -v quiet -clp:NoSummary "$HERE/../OneIndex.Dictionaries.Tool"
T="dotnet $ONEINDEX_ARTIFACTS/bin/OneIndex.Dictionaries.Tool/release/oneindex-dictionaries.dll"
$T build-mecab ja "$D/ipadic" "$OUT/ja.oidict"
$T build-mecab ko "$D/ko" "$OUT/ko.oidict"
$T build-jieba "$D/jieba-dict.txt" "$OUT/zh.oidict"
$T build-wordlist "$D/thaidict.txt" "$OUT/th.oidict"
$T build-wordlist "$D/laodict.txt" "$OUT/lo.oidict"
$T build-wordlist "$D/khmerdict.txt" "$OUT/km.oidict"
$T build-wordlist "$D/burmesedict.txt" "$OUT/my.oidict"
ls -l "$OUT"
