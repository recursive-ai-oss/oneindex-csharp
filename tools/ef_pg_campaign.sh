#!/usr/bin/env bash
# Runs the Entity Framework kill campaign (tools/ef_kill_campaign.sh) against a user-local PostgreSQL server started from the `pgserver`
# wheel in a uv virtual environment, then stops the server.
#
#   tools/ef_pg_campaign.sh <work dir> <first seed> <lanes> <cycles>
#
# EF_RETAIN_BASE and EF_WINDOW are passed through to the campaign. Needs `uv` and network access to the Python package index for the
# first run; the server's data directory lives under <work dir>.
set -u
work=${1:?work dir}; seed=${2:-1}; lanes=${3:-4}; cycles=${4:-100}
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for d in "$HOME/dotnet10" "$HOME/oneindex/dotnet"; do [ -d "$d" ] && export PATH="$d:$PATH" DOTNET_ROOT="$d" && break; done
mkdir -p "$work"
venv="$work/pgvenv"; data="$work/pgdata"
[ -x "$venv/bin/python" ] || { uv venv "$venv" --python 3.12 >/dev/null && uv pip install --python "$venv/bin/python" pgserver==0.1.4 >/dev/null; } || exit 1
"$venv/bin/python" - "$data" <<'EOF'
import sys, pgserver
srv = pgserver.get_server(sys.argv[1], cleanup_mode=None)
print("postgres:", srv.psql("select version();").strip().splitlines()[-2].strip())
EOF
[ $? -eq 0 ] || exit 1
EF_PG="Host=$data;Username=postgres" bash "$here/ef_kill_campaign.sh" "$work/campaign" "$seed" "$lanes" "$cycles"
rc=$?
"$venv/bin/python" -c "import sys, pgserver; pgserver.get_server(sys.argv[1], cleanup_mode='stop').cleanup()" "$data"
exit $rc
