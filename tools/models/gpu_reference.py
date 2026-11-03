#!/usr/bin/env python3
"""Reference encodings of a BEIR corpus from the published model runtimes (PyTorch and sentence-transformers, run as black boxes),
on a GPU when there is one. Resumable: every shard is written to a ".part" file and renamed, a finished shard is skipped, and
--budget-seconds stops cleanly between shards (exit code 3 = run again).

    gpu_reference.py dense  --data <beir set dir> --model <bge-small hf dir> --out <dir> [--budget-seconds N]
        <out>/dense-NNNNN.f32   float32 [docs x 384], the unit vector of each passage (title, a space, text), documents without a prefix,
                                20,000 passages per shard in corpus order; <out>/queries.f32 for the queries that have judgements, with
                                the retrieval instruction. First-token (CLS) hidden state scaled to unit length, at most 512 tokens.
    gpu_reference.py sparse --data <beir set dir> --model <sparse model hf dir> --out <dir> [--budget-seconds N]
        <out>/sparse-NNNNN.bin  int32 docs, int32 counts[docs], then int32 token ids[sum], then float32 weights[sum], 20,000 passages per shard;
                                <out>/vocab.txt: the token string of every id. Documents go through SparseEncoder.encode_document.
    gpu_reference.py rerank --data <beir set dir> --model <cross-encoder hf dir> --candidates <jsonl> --out <scores.jsonl> [--budget-seconds N]
        candidates: one {"q": query id, "t": query text, "d": [passage ids]} per line; out: one {"q": id, "d": [ids], "s": [logits]} per query.
        The pair is (query, title + " " + text) with truncation "longest_first" at 512; the score is the model's single raw logit.

Passages are sorted by length inside a shard so the batches are tight; a text's vector does not depend on its batch (padding is masked).
Weights stay float32 (no autocast, TF32 off) unless --tf32 is given.
"""
import argparse, json, os, sys, time

SHARD = 20000


def passage_text(j):
    title = j.get("title") or ""
    text = j.get("text") or ""
    return text if not title else title + " " + text


def read_corpus(path):
    with open(path, encoding="utf-8") as f:
        for line in f:
            yield json.loads(line)


def read_queries(data):
    judged = set()
    with open(os.path.join(data, "qrels", "test.tsv"), encoding="utf-8") as f:
        for i, line in enumerate(f):
            p = line.rstrip("\n").split("\t")
            if i == 0 and p[0] == "query-id":
                continue
            judged.add(p[0])
    out = []
    with open(os.path.join(data, "queries.jsonl"), encoding="utf-8") as f:
        for line in f:
            j = json.loads(line)
            if j["_id"] in judged:
                out.append((j["_id"], j["text"]))
    return out


def shards(path):
    buf = []
    for j in read_corpus(path):
        buf.append(j)
        if len(buf) == SHARD:
            yield buf
            buf = []
    if buf:
        yield buf


def atomic_write(path, write):
    with open(path + ".part", "wb") as f:
        write(f)
        f.flush()
        os.fsync(f.fileno())
    os.replace(path + ".part", path)


def device(args):
    import torch
    torch.backends.cuda.matmul.allow_tf32 = bool(args.tf32)
    torch.backends.cudnn.allow_tf32 = bool(args.tf32)
    return torch.device("cuda" if torch.cuda.is_available() else "cpu")


def batches_by_tokens(lengths, order, budget):
    """Groups indexes (already sorted by length) so that batch size x longest length stays within the token budget."""
    batch, longest = [], 0
    for i in order:
        longest_next = max(longest, lengths[i])
        if batch and longest_next * (len(batch) + 1) > budget:
            yield batch
            batch, longest_next = [], lengths[i]
        batch.append(i)
        longest = longest_next
    if batch:
        yield batch


def dense(args):
    import numpy as np, torch
    from tokenizers import Tokenizer
    from transformers import AutoModel
    dev = device(args)
    tok = Tokenizer.from_file(os.path.join(args.model, "tokenizer.json"))
    tok.enable_truncation(max_length=512)
    tok.no_padding()
    model = AutoModel.from_pretrained(args.model, torch_dtype=torch.float32).to(dev).eval()
    os.makedirs(args.out, exist_ok=True)
    instruction = "Represent this sentence for searching relevant passages: "

    def encode(texts):
        enc = tok.encode_batch(texts)
        lengths = [len(e.ids) for e in enc]
        order = sorted(range(len(texts)), key=lambda i: lengths[i])
        out = np.zeros((len(texts), model.config.hidden_size), dtype=np.float32)
        with torch.inference_mode():
            for batch in batches_by_tokens(lengths, order, args.token_budget):
                width = max(lengths[i] for i in batch)
                ids = torch.zeros((len(batch), width), dtype=torch.long)
                mask = torch.zeros((len(batch), width), dtype=torch.long)
                for r, i in enumerate(batch):
                    ids[r, : lengths[i]] = torch.tensor(enc[i].ids)
                    mask[r, : lengths[i]] = 1
                ids, mask = ids.to(dev), mask.to(dev)
                h = model(input_ids=ids, attention_mask=mask, token_type_ids=torch.zeros_like(ids)).last_hidden_state[:, 0]
                h = h / h.norm(dim=1, keepdim=True).clamp_min(1e-12)
                out[batch] = h.float().cpu().numpy()
        return out

    qpath = os.path.join(args.out, "queries.f32")
    if not os.path.exists(qpath):
        queries = read_queries(args.data)
        atomic_write(qpath, lambda f: f.write(encode([instruction + t for _, t in queries]).tobytes()))
        print(f"queries: {len(queries)}", flush=True)
    clock = time.time()
    done_docs = 0
    for n, shard in enumerate(shards(os.path.join(args.data, "corpus.jsonl"))):
        path = os.path.join(args.out, f"dense-{n:05d}.f32")
        if os.path.exists(path):
            continue
        if time.time() - clock > args.budget_seconds:
            print(f"budget used before shard {n}; run again to continue", flush=True)
            return 3
        t = time.time()
        vectors = encode([passage_text(j) for j in shard])
        atomic_write(path, lambda f: f.write(vectors.tobytes()))
        done_docs += len(shard)
        print(f"shard {n}: {len(shard)} passages in {time.time() - t:.1f} s ({len(shard) / (time.time() - t):.0f}/s), total this run {done_docs}", flush=True)
    return 0


def sparse(args):
    import numpy as np, torch
    from sentence_transformers import SparseEncoder
    dev = device(args)
    model = SparseEncoder(args.model, trust_remote_code=False, device=str(dev)).eval()
    os.makedirs(args.out, exist_ok=True)
    vocab_path = os.path.join(args.out, "vocab.txt")
    if not os.path.exists(vocab_path):
        tk = model.tokenizer
        tokens = tk.convert_ids_to_tokens(list(range(len(tk))))
        atomic_write(vocab_path, lambda f: f.write(("\n".join(tokens) + "\n").encode("utf-8")))
    clock = time.time()
    done_docs = 0
    for n, shard in enumerate(shards(os.path.join(args.data, "corpus.jsonl"))):
        path = os.path.join(args.out, f"sparse-{n:05d}.bin")
        if os.path.exists(path):
            continue
        if time.time() - clock > args.budget_seconds:
            print(f"budget used before shard {n}; run again to continue", flush=True)
            return 3
        t = time.time()
        texts = [passage_text(j) for j in shard]
        counts = np.zeros(len(texts), dtype=np.int32)
        ids_parts, w_parts = [], []
        sub = 2048
        for s in range(0, len(texts), sub):
            with torch.no_grad():
                emb = model.encode_document(texts[s : s + sub], batch_size=args.batch_size, convert_to_tensor=True,
                                            convert_to_sparse_tensor=False, show_progress_bar=False)
                nz = (emb > 0).nonzero()  # row-major: rows ascending, columns ascending
                val = emb[nz[:, 0], nz[:, 1]].float().cpu().numpy()
                rows, cols = nz[:, 0].cpu().numpy(), nz[:, 1].cpu().numpy()
            counts[s : s + sub] = np.bincount(rows, minlength=min(sub, len(texts) - s)).astype(np.int32)
            ids_parts.append(cols.astype(np.int32))
            w_parts.append(val.astype(np.float32))

        def write(f):
            f.write(np.int32(len(counts)).tobytes())
            f.write(counts.tobytes())
            f.write(np.concatenate(ids_parts).tobytes())
            f.write(np.concatenate(w_parts).tobytes())

        atomic_write(path, write)
        done_docs += len(shard)
        print(f"shard {n}: {len(shard)} passages in {time.time() - t:.1f} s ({len(shard) / (time.time() - t):.0f}/s), {counts.sum() / len(counts):.1f} tokens/passage, total this run {done_docs}", flush=True)
    return 0


def rerank(args):
    import torch
    from tokenizers import Tokenizer
    from transformers import AutoModelForSequenceClassification
    dev = device(args)
    tok = Tokenizer.from_file(os.path.join(args.model, "tokenizer.json"))
    tok.enable_truncation(max_length=512, strategy="longest_first")
    tok.no_padding()
    model = AutoModelForSequenceClassification.from_pretrained(args.model, torch_dtype=torch.float32).to(dev).eval()
    cands = [json.loads(l) for l in open(args.candidates, encoding="utf-8")]
    need = {d for c in cands for d in c["d"]}
    text = {}
    for j in read_corpus(os.path.join(args.data, "corpus.jsonl")):
        if j["_id"] in need:
            text[j["_id"]] = passage_text(j)
    done = set()
    if os.path.exists(args.out):
        for l in open(args.out, encoding="utf-8"):
            done.add(json.loads(l)["q"])
    clock = time.time()
    pairs_done = 0
    with open(args.out, "a", encoding="utf-8") as out:
        for c in cands:
            if c["q"] in done:
                continue
            if time.time() - clock > args.budget_seconds:
                print("budget used; run again to continue", flush=True)
                return 3
            enc = tok.encode_batch([(c["t"], text[d]) for d in c["d"]])
            lengths = [len(e.ids) for e in enc]
            order = sorted(range(len(enc)), key=lambda i: lengths[i])
            scores = [0.0] * len(enc)
            with torch.inference_mode():
                for batch in batches_by_tokens(lengths, order, args.token_budget):
                    width = max(lengths[i] for i in batch)
                    ids = torch.zeros((len(batch), width), dtype=torch.long)
                    types = torch.zeros((len(batch), width), dtype=torch.long)
                    mask = torch.zeros((len(batch), width), dtype=torch.long)
                    for r, i in enumerate(batch):
                        n = lengths[i]
                        ids[r, :n] = torch.tensor(enc[i].ids)
                        types[r, :n] = torch.tensor(enc[i].type_ids)
                        mask[r, :n] = 1
                    logits = model(input_ids=ids.to(dev), token_type_ids=types.to(dev), attention_mask=mask.to(dev)).logits[:, 0].float().cpu().tolist()
                    for r, i in enumerate(batch):
                        scores[i] = logits[r]
            out.write(json.dumps({"q": c["q"], "d": c["d"], "s": scores}) + "\n")
            out.flush()
            pairs_done += len(scores)
            if pairs_done % 5000 < len(scores):
                print(f"{pairs_done} pairs this run, {pairs_done / (time.time() - clock):.0f}/s", flush=True)
        os.fsync(out.fileno())
    print(f"done, {pairs_done} pairs this run in {time.time() - clock:.0f} s", flush=True)
    return 0


def main():
    p = argparse.ArgumentParser()
    p.add_argument("mode", choices=["dense", "sparse", "rerank"])
    p.add_argument("--data", required=True)
    p.add_argument("--model", required=True)
    p.add_argument("--out", required=True)
    p.add_argument("--candidates")
    p.add_argument("--budget-seconds", type=float, default=1e12)
    p.add_argument("--token-budget", type=int, default=32768)
    p.add_argument("--batch-size", type=int, default=32)
    p.add_argument("--tf32", action="store_true")
    args = p.parse_args()
    sys.exit({"dense": dense, "sparse": sparse, "rerank": rerank}[args.mode](args))


if __name__ == "__main__":
    main()
