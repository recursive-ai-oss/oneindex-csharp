#!/usr/bin/env bash
# Information-only SQLite FTS5 rows over the Wikipedia abstracts, run through the pinned sqlite3 command-line tool.
#
#   fts5_rows.sh <abstracts.jsonl> <queries.json> <docs> <work dir> <out.tsv>
#
# FTS5's unicode61 tokenizer lower-cases and splits on non-alphanumerics (no stop words, no stemming), the closest match to
# the matched Standard analysis. Each row's queries run as one sqlite3 invocation and are timed together, wall clock, so the
# per-query figure includes a small process start-up share; results go to /dev/null. Fuzzy has no FTS5 counterpart.
set -euo pipefail
corpus=$1; queries=$2; docs=$3; work=$4; out=$5
root="${ONEINDEX_DATA:-$HOME/oneindex}"
sqlite="${SQLITE3:-$root/oracles/sqlite-3540100/sqlite3}"
py="${PYTHON:-$root/oracles/venv/bin/python}"
rm -rf "$work"; mkdir -p "$work"
"$py" - "$corpus" "$docs" "$work" <<'PY'
import json, sys
corpus, n, work = sys.argv[1], int(sys.argv[2]), sys.argv[3]
with open(f"{work}/load.sql", "w", encoding="utf-8") as sql, open(corpus, encoding="utf-8") as f:
    sql.write("PRAGMA journal_mode=OFF; PRAGMA synchronous=OFF;\nCREATE VIRTUAL TABLE d USING fts5(text, tokenize='unicode61 remove_diacritics 0');\nBEGIN;\n")
    for i, line in enumerate(f):
        if i >= n: break
        t = json.loads(line)["text"].replace("'", "''")
        sql.write(f"INSERT INTO d(text) VALUES('{t}');\n")
    sql.write("COMMIT;\n")
PY
s=$(date +%s.%N); "$sqlite" "$work/fts.db" < "$work/load.sql"; e=$(date +%s.%N)
printf 'index\t%s\t%s\tbytes=%s\n' "$docs" "$(awk -v s="$s" -v e="$e" 'BEGIN{print e-s}')" "$(wc -c < "$work/fts.db")" > "$out"
"$py" - "$queries" "$work" <<'PY'
import json, sys
queries, work = sys.argv[1], sys.argv[2]
q = json.load(open(queries, encoding="utf-8"))
rows = {
    "Terms1": lambda x: " OR ".join(x["terms"]),
    "Terms2": lambda x: " OR ".join(x["terms"]),
    "Terms5": lambda x: " OR ".join(x["terms"]),
    "Phrase": lambda x: '"' + " ".join(x["terms"]) + '"',
    "Prefix": lambda x: x["prefix"] + "*",
    "AsYouType": lambda x: " AND ".join(x["required"] + [x["prefix"] + "*"]),
}
for row, make in rows.items():
    with open(f"{work}/{row}.sql", "w", encoding="utf-8") as f:
        for x in q[row]:
            m = make(x).replace("'", "''")
            f.write(f"SELECT rowid FROM d WHERE d MATCH '{m}' ORDER BY rank LIMIT 10;\n")
PY
for row in Terms1 Terms2 Terms5 Phrase Prefix AsYouType; do
  n=$(wc -l < "$work/$row.sql")
  s=$(date +%s.%N); "$sqlite" "$work/fts.db" < "$work/$row.sql" > /dev/null; e=$(date +%s.%N)
  printf '%s\t%s\t%s\n' "$row" "$n" "$(awk -v s="$s" -v e="$e" -v n="$n" 'BEGIN{print (e-s)*1000/n}')" >> "$out"
done
