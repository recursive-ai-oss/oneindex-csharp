#!/usr/bin/env bash
# Fetches the source dictionaries and evaluation treebanks into a cache directory (default $ONEINDEX_CACHE/corpus/dict) and
# checks their SHA-256. Nothing fetched here is ever committed. Usage: tools/dictionaries/fetch.sh [cache-dir]
set -euo pipefail
D="${1:-${ONEINDEX_CACHE:-$HOME/oneindex/cache}/corpus/dict}"
mkdir -p "$D"
cd "$D"
get() { # name url sha256
  if [ ! -f "$1" ]; then curl -fsSL -o "$1" "$2"; fi
  echo "$3  $1" | sha256sum -c - >/dev/null || { echo "checksum mismatch: $1" >&2; exit 1; }
}
ICU=https://raw.githubusercontent.com/unicode-org/icu/release-78.3/icu4c/source/data/brkitr/dictionaries
get thaidict.txt $ICU/thaidict.txt 3166abde40c0f44ab91c28f5ce96d7d1472cb7882e1c0bda0a72f8f69dba4274
get laodict.txt $ICU/laodict.txt 3c876934a3fa81031d2333525eafaca6a7c9f842e3b98f18c38880420afb5d36
get khmerdict.txt $ICU/khmerdict.txt 87bee2d17cd5148aa36957eb05409eefc124de8ad519b81b789298ef3e60b5d9
get burmesedict.txt $ICU/burmesedict.txt 61d8abc3d9102b2f9bf0c9f44db0d7ab89b18172d8cd26832e4c83174bd8673b
get mecab-ipadic-2.7.0-20070801.tar.gz 'https://sourceforge.net/projects/mecab/files/mecab-ipadic/2.7.0-20070801/mecab-ipadic-2.7.0-20070801.tar.gz/download' b62f527d881c504576baed9c6ef6561554658b175ce6ae0096a60307e49e3523
get mecab-ko-dic-2.1.1-20180720.tar.gz https://bitbucket.org/eunjeon/mecab-ko-dic/downloads/mecab-ko-dic-2.1.1-20180720.tar.gz fd62d3d6d8fa85145528065fabad4d7cb20f6b2201e71be4081a4e9701a5b330
get jieba-dict.txt https://raw.githubusercontent.com/fxsjy/jieba/master/jieba/dict.txt 7197c3211ddd98962b036cdf40324d1ea2bfaa12bd028e68faa70111a88e12a8
get jieba-LICENSE https://raw.githubusercontent.com/fxsjy/jieba/master/LICENSE "$(curl -fsSL https://raw.githubusercontent.com/fxsjy/jieba/master/LICENSE | sha256sum | cut -d' ' -f1)"
mkdir -p ipadic ko
[ -f ipadic/matrix.def ] || tar xzf mecab-ipadic-2.7.0-20070801.tar.gz -C ipadic --strip-components=1
[ -f ko/matrix.def ] || tar xzf mecab-ko-dic-2.1.1-20180720.tar.gz -C ko --strip-components=1
echo "fetched into $D"
