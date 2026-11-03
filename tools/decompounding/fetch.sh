#!/usr/bin/env bash
# Fetches the sources of the decompounding word lists into $ONEINDEX_CACHE/corpus/leipzig (never committed).
#   Leipzig Corpora Collection news corpora (300,000 sentences each): the download page states the corpora are made available
#   under CC BY (https://wortschatz-leipzig.de/en/usage); only the word lists inside are used.
#   OpenTaal Dutch word list: "Revised BSD License and/or CC BY 3.0" (LICENSE.txt of https://github.com/OpenTaal/opentaal-wordlist).
set -euo pipefail
cache="${ONEINDEX_CACHE:-$HOME/oneindex/cache}/corpus/leipzig"
mkdir -p "$cache/opentaal"
cd "$cache"
base=https://downloads.wortschatz-leipzig.de/corpora
while read -r name sha; do
  [ -f "$name.tar.gz" ] || curl -sfLO "$base/$name.tar.gz"
  echo "$sha  $name.tar.gz" | sha256sum -c --quiet -
  [ -f "$name/$name-words.txt" ] || tar xzf "$name.tar.gz" --wildcards "*-words.txt"
done <<'LIST'
deu_news_2024_300K 9483168103f47a41380f0c164012c979f788af161867c1645249d3b4c5cbb6a8
swe_news_2023_300K e7c777651a432df0e9f108c9b7f452e22c6cfe9d6572ba00d1891a9baa69db85
dan_news_2022_300K 3bf693f48369ec1cb25f2ae0c9765b4c0393abe3ac9d8540dfc52f6936aff208
nor_news_2023_300K d6c8c522b0a71350ed3f7a62e8df9f7081e7e96871086c891785789899313a6d
fin_news_2022_300K 476e56183516f1e10d01c1daf673833f5f1293adb6ad515f14b1054d6f6705f9
isl_newscrawl_2019_300K d8ded5eea427e3a09fa7132fb65d288a9ee055d4571bea8fcdc23878afc9e00e
nld_news_2023_300K 63fe2f00683829ac025129ed77fc07687a035155a4276b0edf1a16ac5300ed2b
LIST
rev=b250510dda431785f962019167d1415198ff3905
for f in LICENSE.txt wordlist.txt datetimeversion.txt; do
  [ -f "opentaal/$f" ] || curl -sfL "https://raw.githubusercontent.com/OpenTaal/opentaal-wordlist/$rev/$f" -o "opentaal/$f"
done
echo "12e5fb5e3c73840b583b30016926d1f63a75e9bf1652a3a6634b2ba7c49ad7be  opentaal/wordlist.txt" | sha256sum -c --quiet -
echo fetched
