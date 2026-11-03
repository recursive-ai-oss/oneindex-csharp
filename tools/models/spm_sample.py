"""Builds the multilingual tokenizer sample (JSON strings, one per line) and the reference ids from the released `sentencepiece`
wheel (black box). The sample mixes: sentences of four Universal Dependencies treebanks (ja, zh, ko, th), phrases of random words
from the Snowball vocabularies (34 languages), and hand-written edge cases (empty, white space, control characters, combining
sequences, compatibility forms, emoji, unknown characters, very long runs). Seeded, so it is reproducible.

usage: spm_sample.py <model-file> <ud-dir> <snowball-data-dir> <out-prefix> [tokenizer.json]   (writes <prefix>.texts.jsonl and <prefix>.ids.jsonl)
"""
import json
import os
import random
import sys

import sentencepiece as spm

model, ud_dir, snow_dir, prefix = sys.argv[1:5]
tokenizer_json = sys.argv[5] if len(sys.argv) > 5 else None
rng = random.Random(20261103)
texts = []

for name in sorted(os.listdir(ud_dir)):
    if not name.endswith(".conllu"):
        continue
    sent, toks = None, []
    for line in open(os.path.join(ud_dir, name), encoding="utf-8"):
        line = line.rstrip("\n")
        if line.startswith("# text = "):
            texts.append(line[len("# text = "):])

for lang in sorted(os.listdir(snow_dir)):
    path = os.path.join(snow_dir, lang, "voc.txt")
    if not os.path.isfile(path):
        continue
    words = [w.strip() for w in open(path, encoding="utf-8", errors="replace") if w.strip()]
    if not words:
        continue
    for _ in range(60):
        k = rng.randint(1, 8)
        texts.append(" ".join(rng.choice(words) for _ in range(k)))

edge = [
    "", " ", "   ", "\t\n", "a", " a", "a ", "  a   b  ", "a\u00a0b", "\u3000\u3000x", "x\u200by", "\u200d", "\ufeff", "a\u00adb",
    "e\u0301", "\u0041\u030a", "\u212b", "\uff76\uff9e", "\uff71\uff72\uff73", "\ufb01", "\u2460", "\u00bd", "\u33a1", "x\u00b2",
    "\U0001F600", "\U0001F468\u200d\U0001F469\u200d\U0001F467", "\U0001F1E6\U0001F1E7", "\U00010FFF", "a\U00010FFF\U00010FFEb",
    "\u0378", "x\u0378\u0379y", "\uffff", "\ue000", "\u2581", "\u2581\u2581a", "a\u2581b", "\x00\x01\x02a\x7f", "line1\r\nline2\rline3",
    "\u1100\u1161\u11a8", "\uac00\u11a8", "\u0e32\u0e4d", "\u0627\u0653", "\u1e9b\u0323", "\u0958", "\u0f43", "\ufdfa", "\u3392",
    "hello world", "Hello, World!", "query: how much protein should a female eat", "passage: As a general guideline",
    "a" * 3000, " ".join(["word"] * 600), "\u4e2d" * 700, "\u00e9" * 900 + "\u0301" * 50,
    "URL https://example.org/path?q=1&r=%E2%9C%93#frag", "e-mail: a.b+c@example.co.uk", "1,234.56 \u20ac 99%", "C:\\Users\\x\\file.txt",
]
texts.extend(edge)
# Random character soup over many scripts.
ranges = [(0x20, 0x7e), (0xa0, 0x24f), (0x370, 0x3ff), (0x400, 0x4ff), (0x530, 0x58f), (0x590, 0x5ff), (0x600, 0x6ff), (0x900, 0x97f),
          (0xe00, 0xe7f), (0x1100, 0x11ff), (0x2000, 0x206f), (0x2190, 0x23ff), (0x3040, 0x30ff), (0x3400, 0x4dbf), (0x4e00, 0x9fff),
          (0xac00, 0xd7a3), (0xff00, 0xffef), (0x1f300, 0x1f64f), (0x20000, 0x2a6df)]
for _ in range(600):
    texts.append("".join(chr(rng.randint(*rng.choice(ranges))) for _ in range(rng.randint(1, 40))))

# Soup of bases, accents, kana, half-width forms and Hangul jamo, where the normalizer's sequence rules matter.
bases = [chr(c) for c in list(range(0x41, 0x5b)) + list(range(0x61, 0x7b)) + list(range(0xc0, 0x100)) + [0x391, 0x3b1, 0x410, 0x430, 0x627, 0x928, 0xff76, 0xff71, 0x30ab, 0x304b, 0x1100, 0x1161, 0x11a8, 0xac00, 0x1e9b]]
marks = [chr(c) for c in list(range(0x300, 0x370)) + [0x3099, 0x309a, 0xff9e, 0xff9f, 0x93c, 0x653, 0x654, 0x655, 0xe4d, 0x323, 0x307]]
for _ in range(1500):
    texts.append("".join(rng.choice(bases) if rng.random() < 0.4 else rng.choice(marks) for _ in range(rng.randint(1, 12))))

with open(prefix + ".texts.jsonl", "w", encoding="utf-8") as f:
    for t in texts:
        f.write(json.dumps(t, ensure_ascii=True) + "\n")
sp = spm.SentencePieceProcessor(model_file=model)
normalizer = spm.SentencePieceNormalizer(model_file=model, add_dummy_prefix=True, escape_whitespaces=True, remove_extra_whitespaces=True)
with open(prefix + ".norm.jsonl", "w", encoding="utf-8") as f:
    for t in texts:
        f.write(json.dumps(normalizer.normalize(t), ensure_ascii=True) + "\n")
with open(prefix + ".ids.jsonl", "w") as f:
    for t in texts:
        f.write(json.dumps(sp.encode(t)) + "\n")
if tokenizer_json:
    # The model's own fast tokenizer (Hugging Face `tokenizers`, run as a black box): ids with the sentence boundaries, in the model's numbering.
    from tokenizers import Tokenizer
    hf = Tokenizer.from_file(tokenizer_json)
    with open(prefix + ".hf.jsonl", "w") as f:
        for t in texts:
            f.write(json.dumps(hf.encode(t).ids) + "\n")
print(len(texts), "texts")
