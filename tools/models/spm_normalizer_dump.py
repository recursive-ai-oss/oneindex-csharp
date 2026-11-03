"""Dumps the character rules of a SentencePiece model's normalizer by running the released `sentencepiece` wheel as a black box.

A rule is a source sequence of code points and the text it becomes; the normalizer applies the longest rule that matches at the
current position, and copies a character no rule matches. The dump has:
  * every single code point whose normalized form differs from itself, and
  * every multi-code-point sequence the normalizer rewrites as a unit (canonical compositions such as e + U+0301, half-width kana with
    a voicing mark, Hangul jamo). Candidate sequences come from the Unicode decomposition data of the Python runtime (the sources of
    each composite, with every character that normalizes to a part), and each candidate is kept only if the normalizer's output for it
    differs from the output of its characters one by one. Hangul jamo sequences are algorithmic and are left to the reader.

Each line: the source code points in hex separated by spaces, a tab, the target code points in hex separated by spaces (possibly none).

usage: spm_normalizer_dump.py <model-file> <out.tsv>
"""
import itertools
import sys
import unicodedata

import sentencepiece as spm

norm = spm.SentencePieceNormalizer(
    model_file=sys.argv[1], add_dummy_prefix=False, escape_whitespaces=False, remove_extra_whitespaces=False)


def n(s):
    return norm.normalize(s)


def hangul(cp):
    return 0x1100 <= cp <= 0x11FF or 0xAC00 <= cp <= 0xD7A3 or 0xA960 <= cp <= 0xA97F or 0xD7B0 <= cp <= 0xD7FF


single = {}
for cp in range(0x110000):
    if 0xD800 <= cp <= 0xDFFF:
        continue
    t = n(chr(cp))
    if t != chr(cp):
        single[cp] = t

# Preimages: which code points normalize to a given single code point (for composing half-width and compatibility forms).
pre = {}
for cp, t in single.items():
    if len(t) == 1:
        pre.setdefault(ord(t), []).append(cp)


def sources_of(cp):
    return [cp] + pre.get(cp, [])


def full_decomposition(cp):
    d = unicodedata.decomposition(chr(cp))
    if not d or d.startswith("<"):
        return [cp]
    out = []
    for h in d.split():
        out.extend(full_decomposition(int(h, 16)))
    return out


candidates = set()
for cp in range(0x110000):
    if 0xD800 <= cp <= 0xDFFF or hangul(cp):
        continue
    d = unicodedata.decomposition(chr(cp))
    if not d or d.startswith("<"):
        continue
    one_step = [int(h, 16) for h in d.split()]
    full = full_decomposition(cp)
    for parts in {tuple(one_step), tuple(full)}:
        if len(parts) < 2 or len(parts) > 4:
            continue
        for combo in itertools.product(*[sources_of(p) for p in parts]):
            candidates.add(combo)

sequences = {}
for combo in sorted(candidates):
    s = "".join(chr(c) for c in combo)
    t = n(s)
    if t != "".join(n(chr(c)) for c in combo):
        sequences[combo] = t

with open(sys.argv[2], "w", encoding="utf-8") as out:
    for cp in sorted(single):
        out.write("%04X\t%s\n" % (cp, " ".join("%04X" % ord(c) for c in single[cp])))
    for combo in sorted(sequences):
        out.write("%s\t%s\n" % (" ".join("%04X" % c for c in combo), " ".join("%04X" % ord(c) for c in sequences[combo])))
print(len(single), "single code points;", len(sequences), "sequences")
