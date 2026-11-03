#!/usr/bin/env python3
"""Builds the decompounding word lists packed in RecursiveAI.OneIndex from the fetched sources.

    build_lists.py <cache dir> <out dir> [min count]

For de, sv, da, no, fi and is the list is the Leipzig news word list (word, count) with case folded: counts of case variants add up; only
words made of letters, 3 to 40 characters, seen at least <min count> times (default 3) are kept. For nl the vocabulary is the OpenTaal word
list (letters only, folded), with each word's count taken from the Leipzig Dutch news list (1 when absent), so the list is clean and still has
frequencies. Output: <lang>.tsv.gz, "word TAB count" lines ordered by descending count then word, preceded by # provenance lines.
The output is deterministic (gzip header time and name fixed).
"""
import gzip, io, os, sys, unicodedata

SOURCES = {
    "de": ("deu_news_2024_300K", "Leipzig Corpora Collection, German news 2024, 300K sentences"),
    "sv": ("swe_news_2023_300K", "Leipzig Corpora Collection, Swedish news 2023, 300K sentences"),
    "da": ("dan_news_2022_300K", "Leipzig Corpora Collection, Danish news 2022, 300K sentences"),
    "no": ("nor_news_2023_300K", "Leipzig Corpora Collection, Norwegian news 2023, 300K sentences"),
    "fi": ("fin_news_2022_300K", "Leipzig Corpora Collection, Finnish news 2022, 300K sentences"),
    "is": ("isl_newscrawl_2019_300K", "Leipzig Corpora Collection, Icelandic news crawl 2019, 300K sentences"),
    "nl": ("nld_news_2023_300K", "Leipzig Corpora Collection, Dutch news 2023, 300K sentences, restricted to the OpenTaal word list"),
}


def ok(word):
    return 3 <= len(word) <= 40 and word.isalpha()


def fold(word):
    return unicodedata.normalize("NFC", word).lower()


def leipzig(cache, name):
    counts = {}
    with open(f"{cache}/{name}/{name}-words.txt", encoding="utf-8") as f:
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) != 3:
                continue
            w = fold(parts[1])
            if ok(w):
                counts[w] = counts.get(w, 0) + int(parts[2])
    return counts


def write(path, header, counts):
    rows = sorted(counts.items(), key=lambda kv: (-kv[1], kv[0]))
    buf = io.StringIO()
    for h in header:
        buf.write("# " + h + "\n")
    for w, c in rows:
        buf.write(f"{w}\t{c}\n")
    with open(path, "wb") as raw:
        with gzip.GzipFile(filename="", mode="wb", fileobj=raw, mtime=0, compresslevel=9) as gz:
            gz.write(buf.getvalue().encode("utf-8"))
    return len(rows)


def main():
    cache, out = sys.argv[1], sys.argv[2]
    minc = int(sys.argv[3]) if len(sys.argv) > 3 else 3
    os.makedirs(out, exist_ok=True)
    for lang, (name, desc) in SOURCES.items():
        counts = leipzig(cache, name)
        header = [desc + " (CC BY 4.0, https://wortschatz-leipzig.de)"]
        if lang == "nl":
            header.append("OpenTaal Dutch word list 2.20.23 (Revised BSD or CC BY 3.0, https://github.com/OpenTaal/opentaal-wordlist)")
            vocab = {}
            with open(f"{cache}/opentaal/wordlist.txt", encoding="utf-8") as f:
                for line in f:
                    w = fold(line.strip())
                    if ok(w):
                        vocab[w] = max(counts.get(w, 0), 1)
            kept = vocab
        else:
            kept = {w: c for w, c in counts.items() if c >= minc}
            header.append(f"words seen at least {minc} times, letters only, case folded")
        n = write(f"{out}/{lang}.tsv.gz", header, kept)
        print(lang, n, os.path.getsize(f"{out}/{lang}.tsv.gz"))


main()
