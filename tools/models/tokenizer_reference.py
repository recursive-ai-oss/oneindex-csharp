#!/usr/bin/env python3
"""Writes reference token ids from the Hugging Face `tokenizers` package (run as a black box) for the WordPiece conformance tests.

    tokenizer_reference.py <model dir with tokenizer.json> <beir root> <out dir> [passages per set]

Outputs under <out dir>:
  scalars.tsv   one line per Unicode scalar value: hex code point, a tab, the ids of  "Ab<c>Cd"  and the ids of  "<c>"  (tab separated,
                ids space separated). Surrogates are skipped (not representable in UTF-8).
  samples.jsonl {"t": text, "i": [ids]} for the adversarial list, every query and a sample of passages of each BEIR set found.
All encodings use the model's own tokenizer.json with truncation at 512 ids and special tokens added.
"""
import json, os, sys, random
from tokenizers import Tokenizer

model_dir, beir_root, out_dir = sys.argv[1:4]
per_set = int(sys.argv[4]) if len(sys.argv) > 4 else 2000
os.makedirs(out_dir, exist_ok=True)
tok = Tokenizer.from_file(os.path.join(model_dir, "tokenizer.json"))
tok.enable_truncation(max_length=512)

def enc(texts):
    return [e.ids for e in tok.encode_batch(texts)]

with open(os.path.join(out_dir, "scalars.tsv"), "w", encoding="utf-8") as f:
    cps = [c for c in range(0x110000) if not 0xD800 <= c <= 0xDFFF]
    for i in range(0, len(cps), 20000):
        chunk = cps[i:i + 20000]
        a = enc(["Ab%sCd" % chr(c) for c in chunk])
        b = enc([chr(c) for c in chunk])
        for c, x, y in zip(chunk, a, b):
            f.write("%X\t%s\t%s\n" % (c, " ".join(map(str, x)), " ".join(map(str, y))))

adversarial = [
    "", " ", "\t\n\r", "Hello, World!", "HELLO", "naïve café Ünïcödé", "İstanbul İ ǅ ß ẞ Σ ΑΣ ΑΣ. ΑΣΑ", "e\u0301 a\u0308\u0323 \u0301\u0301",
    "日本語のテキスト 한국어 中文文本𠀀𪛖", "emoji 😀👍🏽 family 👨‍👩‍👧 flag 🇺🇸", "a\x00b\x01c\x7fd\u200be\ufeff f\ufffd g\u00adh",
    "x" * 99, "x" * 100, "x" * 101, "é" * 100, "é" * 101, "日" * 700, "a " * 700, "word " * 600, "[CLS] [SEP] [PAD] [UNK] [MASK] [cls] [SEP]x[SEP]",
    "[ CLS ] [CLS][CLS] [MASK]ed", "$100 + 5 = 105; a^b | c~d `e` <f> {g} _h_ -i- ’quoted’ “double” «x» ¿que? ¡hola!",
    "a\u2028b\u2029c\u00a0d\u3000e\u200af\u1680g\u0085h", "supercalifragilisticexpialidocious unaffable ##hash ##", "don't can't U.S.A. 3.14 1,000 e-mail",
    "\U0001F600" * 300, "ａｂｃ ＡＢＣ １２３ ﬁ ﬂ ㎏ ½", "Ωmega ǆ ǈ ǌ Ǆ", "ǅ ᾈ ﬃ", "zalgo z̷̢̛̖a̶l̸g̴o̷", "\u0e01\u0e32\u0e23 \u0e17\u0e14\u0e2a\u0e2d\u0e1a", "ﷺ ﷻ ꙮ",
]
rng = random.Random(7)
with open(os.path.join(out_dir, "samples.jsonl"), "w", encoding="utf-8") as f:
    def emit(texts):
        for t, ids in zip(texts, enc(texts)):
            f.write(json.dumps({"t": t, "i": ids}, ensure_ascii=False) + "\n")
    emit(adversarial)
    # random strings over interesting alphabets
    alpha = list("abcXYZ019 .,;-'\"\t\n") + [chr(c) for c in (0xe9, 0x301, 0x130, 0x3a3, 0x4e2d, 0x3042, 0xac00, 0x200b, 0x1f600, 0x2028, 0xa0, 0x7f, 0x0, 0xfffd, 0x1d7d8)]
    emit(["".join(rng.choice(alpha) for _ in range(rng.randint(0, 60))) for _ in range(3000)])
    for name in sorted(os.listdir(beir_root)):
        d = os.path.join(beir_root, name)
        qf, cf = os.path.join(d, "queries.jsonl"), os.path.join(d, "corpus.jsonl")
        if not (os.path.exists(qf) and os.path.exists(cf)):
            continue
        with open(qf, encoding="utf-8") as q:
            emit([json.loads(l)["text"] for l in q])
        with open(cf, encoding="utf-8") as c:
            docs = [json.loads(l) for l in c]
        rng.shuffle(docs)
        emit([(x.get("title", "") + " " + x.get("text", "")).strip() for x in docs[:per_set]])
print("done")
