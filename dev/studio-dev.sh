#!/usr/bin/env bash
# Local development loop for ClrKernel.Studio: the API/scheduler with `dotnet watch`
# (restarts on C# edits) and the Vite dev server (hot-reloads the UI on save).
#
#   ./dev/studio-dev.sh                       # dev/notebooks, which starts empty
#   ./dev/studio-dev.sh ~/my-notebooks        # your own tree
#   API_PORT=5099 ./dev/studio-dev.sh         # if something else holds the port
#   STORE=files ./dev/studio-dev.sh           # run history as files, not sqlite
#
# DATA_DIR=./dev/data API_PORT=5091 UI_PORT=5181 nohup ./dev/studio-dev.sh ./dev/nb > ./dev/dev.log 2>&1 & echo $! > $S/pid
# dotnet watch --project src/ClrKernel.studio run -- serve  --notebooks "$PWD/dev/notebooks" --data-dir "$PWD/dev/data"   --clrkernel "$PWD/src/ClrKernel/bin/Debug/net8.0/ClrKernel"


# Open the UI at http://localhost:5173 — it proxies /api to the backend, so the
# whole app works from that one URL. Ctrl+C stops both.
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NOTEBOOKS="$(cd "${1:-$REPO/dev/notebooks}" && pwd)"
# Absolute, for the same reason the notebooks root is, and this one is easy to
# miss: `dotnet run --project` runs the app from the *project* folder, so a
# relative --data-dir lands under src/ClrKernel.Studio. The store test below then
# reads settings.json from your shell's directory while the app reads a different
# one entirely — you get "serve needs an explicit run-history store", no backend,
# and a front end returning 500s. Made before the mkdir because it may not exist.
DATA="${DATA_DIR:-$REPO/dev/data}"
mkdir -p "$DATA"
DATA="$(cd "$DATA" && pwd)"
API_PORT="${API_PORT:-5000}"
UI_PORT="${UI_PORT:-5173}"

# Prefer the locally built kernel so kernel changes show up too; fall back to the
# installed global tool.
KERNEL="$REPO/src/ClrKernel/bin/Debug/net8.0/ClrKernel"
if [ ! -x "$KERNEL" ]; then
    echo "Building the kernel once (so cells run against this checkout)…"
    dotnet build "$REPO/src/ClrKernel/ClrKernel.csproj" -v quiet --nologo
fi

# Whole process groups, twice. `dotnet watch` and npm both spawn the thing that
# actually holds the port, so signalling the job leader alone leaves the port
# taken — and the next run then fails in a way that looks like this script being
# broken rather than the last one not having let go. TERM first so they can shut
# down properly, KILL a second later for whatever ignored it. Idempotent, because
# both traps below can run it.
cleanup() {
    local sig
    for sig in TERM KILL; do
        [ -n "${API_PID:-}" ] && kill -"$sig" -- "-$API_PID" 2>/dev/null || true
        [ -n "${UI_PID:-}" ] && kill -"$sig" -- "-$UI_PID" 2>/dev/null || true
        # `if`, not `[ … ] && sleep`: that test is false on the KILL pass, and a
        # failing last command under `set -e` aborts the function — so the trap
        # never reached its `exit` and Ctrl+C reported 1.
        if [ "$sig" = TERM ]; then
            sleep 1
        fi
    done
}
# The signal traps exit rather than returning: a trap that returns drops back
# into the wait loop, and Ctrl+C then tears down half the environment and leaves
# the script sitting there.
trap 'cleanup; exit 130' INT
trap 'cleanup; exit 143' TERM
trap cleanup EXIT

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
#
# `< /dev/null` on both, and it is what makes this work from a real terminal.
# `set -m` above puts each background job in its own process group so cleanup can
# kill the group; a background process group that reads the controlling terminal
# is stopped with SIGTTIN. Both of these read it — `dotnet watch` for Ctrl+R,
# Vite for its own shortcuts — so both end up in state T, suspended, before the
# API ever binds. What you see is Vite up, nothing on the API port, and every
# request through the proxy failing. Waiting does not help: nothing is running.
#
# The cost is Ctrl+R and Vite's keystrokes, which two programs could not have
# shared one terminal for anyway. Ctrl+C still works — the trap kills both groups.
if [ -f "$DATA/settings.json" ] && grep -q '"store"' "$DATA/settings.json"; then
    serve_api < /dev/null &
else
    serve_api --store "${STORE:-sqlite}" < /dev/null &
fi
API_PID=$!

CLRKERNEL_STUDIO_API="http://localhost:$API_PORT" \
    npm --prefix "$REPO/src/ClrKernel.Studio/webapp" run dev -- --port "$UI_PORT" < /dev/null &
UI_PID=$!
set +m

# Not a bare `wait`: bash does not run a trap until the current command finishes,
# and `wait` with both children alive never finishes — so Ctrl+C did nothing at
# all and left dotnet watch and Vite holding their ports. An interruptible sleep
# lets the trap fire between iterations, and the loop also ends the script when
# either half dies on its own rather than leaving half a dev environment up.
while kill -0 "$API_PID" 2>/dev/null && kill -0 "$UI_PID" 2>/dev/null; do
    sleep 1
done
