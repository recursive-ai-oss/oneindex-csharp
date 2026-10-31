"""Prints one verdict per line for each JSON document in a file, checked against a JSON Schema (draft 2020-12).

Usage: python jsonschema_check.py schema.json documents.jsonl
Needs the `jsonschema` wheel; the tests use it only as a black-box validator.
"""
import json
import sys

from jsonschema import Draft202012Validator

with open(sys.argv[1], encoding="utf-8") as f:
    schema = json.load(f)
Draft202012Validator.check_schema(schema)
validator = Draft202012Validator(schema)
with open(sys.argv[2], encoding="utf-8") as f:
    for line in f:
        line = line.strip()
        if not line:
            continue
        errors = list(validator.iter_errors(json.loads(line)))
        print("valid" if not errors else "invalid: " + errors[0].message[:200].replace("\n", " "))
