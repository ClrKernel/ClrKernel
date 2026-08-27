#!/usr/bin/env bash
# Local development loop for ClrKernel.Studio: the API/scheduler with `dotnet watch`
# (restarts on C# edits) and the Vite dev server (hot-reloads the UI on save).
#
#   ./dev/studio-dev.sh                       # dev/notebooks, which starts empty
#   ./dev/studio-dev.sh ~/my-notebooks        # your own tree
#   API_PORT=5099 ./dev/studio-dev.sh         # if something else holds the port
#   STORE=files ./dev/studio-dev.sh           # run history as files, not sqlite
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
serve_api() {
    dotnet watch --project "$REPO/src/ClrKernel.Studio" run -- serve \
        "$@" \
        --notebooks "$NOTEBOOKS" \
        --data-dir "$DATA" \
        --clrkernel "$KERNEL" \
        --urls "http://localhost:$API_PORT"
}

set -m
# `serve` refuses to guess where run history goes. That is the right call for a
# real deployment and pure friction here, where a fresh clone has no
# settings.json and the whole promise is one command — so pass sqlite, unless a
# settings.json is there to answer for itself. A flag would override that file,
# and `cp dev/settings/postgres.settings.json dev/data/settings.json` is the
# documented way to point this loop at a container.
#
# Two branches rather than an unquoted "$STORE_ARGS": word splitting an empty
# variable is a different thing in bash and in zsh, and a dev loop that depends
# on which one you invoked it with is a bug waiting for a Tuesday.
#
# The test is whether settings.json names a *store*, not whether it exists.
# SettingsRegistry.Write merges only the keys that changed, so changing any one
# thing on the Settings page leaves a settings.json holding, say, just
# maxParallelism — and the next start of this script then trusted that file to
# answer a question it says nothing about, passed no --store, and died on "serve
# needs an explicit run-history store". Use the app, restart the loop, no server.
#
# ponytail: a grep, not a JSON parse. It answers "did somebody choose a store on
# purpose", and being wrong costs a flag that loses to the file it was guessing
# about anyway.
if [ -f "$DATA/settings.json" ] && grep -q '"store"' "$DATA/settings.json"; then
    serve_api &
else
    serve_api --store "${STORE:-sqlite}" &
fi
API_PID=$!

CLRKERNEL_STUDIO_API="http://localhost:$API_PORT" \
    npm --prefix "$REPO/src/ClrKernel.Studio/webapp" run dev -- --port "$UI_PORT" &
UI_PID=$!
set +m

wait
