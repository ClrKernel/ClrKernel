#!/usr/bin/env bash
# Local development loop for ClrKernel.Jobs: the API/scheduler with `dotnet watch`
# (restarts on C# edits) and the Vite dev server (hot-reloads the UI on save).
#
#   ./dev/jobs-dev.sh                       # sample notebooks in dev/notebooks
#   ./dev/jobs-dev.sh ~/my-notebooks        # your own tree
#   API_PORT=5099 ./dev/jobs-dev.sh         # if something else holds the port
#
# Open the UI at http://localhost:5173 — it proxies /api to the backend, so the
# whole app works from that one URL. Ctrl+C stops both.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NOTEBOOKS="$(cd "${1:-$REPO/dev/notebooks}" && pwd)"
DATA="${DATA_DIR:-$REPO/dev/data}"
API_PORT="${API_PORT:-5000}"
UI_PORT="${UI_PORT:-5173}"

# Prefer the locally built kernel so kernel changes show up too; fall back to the
# installed global tool.
KERNEL="$REPO/src/ClrKernel/bin/Debug/net8.0/ClrKernel"
if [ ! -x "$KERNEL" ]; then
    echo "Building the kernel once (so cells run against this checkout)…"
    dotnet build "$REPO/src/ClrKernel/ClrKernel.csproj" -v quiet --nologo
fi

mkdir -p "$DATA"

cleanup() {
    # dotnet watch spawns the app as a child; kill the whole group or the app
    # keeps holding the port after Ctrl+C.
    [ -n "${API_PID:-}" ] && kill -- "-$API_PID" 2>/dev/null || true
    [ -n "${UI_PID:-}" ] && kill -- "-$UI_PID" 2>/dev/null || true
}
trap cleanup EXIT INT TERM

echo "notebooks : $NOTEBOOKS"
echo "data      : $DATA"
echo "API       : http://localhost:$API_PORT"
echo "UI        : http://localhost:$UI_PORT  <- open this one"
echo

# `dotnet run --project` sets the app's working directory to the project folder,
# so every path here is absolute on purpose.
set -m
dotnet watch --project "$REPO/src/ClrKernel.Jobs" run -- serve \
    --notebooks "$NOTEBOOKS" \
    --data-dir "$DATA" \
    --clrkernel "$KERNEL" \
    --urls "http://localhost:$API_PORT" &
API_PID=$!

CLRKERNEL_JOBS_API="http://localhost:$API_PORT" \
    npm --prefix "$REPO/src/ClrKernel.Jobs/webapp" run dev -- --port "$UI_PORT" &
UI_PID=$!
set +m

wait
