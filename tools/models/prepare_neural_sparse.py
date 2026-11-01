#!/usr/bin/env python3
"""Prepares opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill (Apache-2.0) for OneIndex.Models.

    prepare_neural_sparse.py <out dir>

Downloads the model at a pinned revision, wraps its masked language model so that the graph ends in SPLADE pooling
(max over the tokens of log(1 + relu(logit)), padding masked out) and writes model.onnx (fp32), model.int8.onnx (weight-only int8
like prepare_bge_small.py), vocab.txt and idf.json (the query-side token weights), plus the model card. The converter then turns the
ONNX files into OneModel files. Run in a venv with torch, transformers, onnx and huggingface-hub (tools/models/requirements-sparse.txt).
The weights are never committed.
"""
import hashlib, json, os, sys

REPO = "opensearch-project/opensearch-neural-sparse-encoding-doc-v3-distill"
REVISION = "babf71f3c48695e2e53a978208e8aba48335e3c0"


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    out = sys.argv[1]
    os.makedirs(out, exist_ok=True)
    import torch
    from huggingface_hub import snapshot_download
    from transformers import AutoModelForMaskedLM

    src = snapshot_download(REPO, revision=REVISION, local_dir=os.path.join(out, "hf"))

    class SpladePooled(torch.nn.Module):
        def __init__(self, mlm):
            super().__init__()
            self.mlm = mlm

        def forward(self, input_ids, attention_mask):
            logits = self.mlm(input_ids=input_ids, attention_mask=attention_mask).logits  # [B, L, V]
            activated = torch.log(1.0 + torch.relu(logits))
            activated = activated * attention_mask.unsqueeze(-1).to(activated.dtype)
            return activated.max(dim=1).values  # [B, V]

    mlm = AutoModelForMaskedLM.from_pretrained(src).eval()
    wrapper = SpladePooled(mlm).eval()
    ids = torch.randint(1000, 5000, (2, 24))
    mask = torch.ones_like(ids)
    target = os.path.join(out, "model.onnx")
    torch.onnx.export(wrapper, (ids, mask), target, input_names=["input_ids", "attention_mask"], output_names=["sparse"],
                      dynamic_axes={"input_ids": {0: "batch", 1: "tokens"}, "attention_mask": {0: "batch", 1: "tokens"}, "sparse": {0: "batch"}},
                      opset_version=17, dynamo=False)
    sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
    from prepare_bge_small import write_weight_only_int8
    write_weight_only_int8(target, os.path.join(out, "model.int8.onnx"))
    for n in ("vocab.txt", "idf.json", "README.md"):
        d = os.path.join(out, n)
        if os.path.lexists(d):
            os.remove(d)
        os.symlink(os.path.join(src, n), d)
    for n in ("model.onnx", "model.int8.onnx", "vocab.txt", "idf.json"):
        print(n, sha256(os.path.join(out, n)))


if __name__ == "__main__":
    main()
