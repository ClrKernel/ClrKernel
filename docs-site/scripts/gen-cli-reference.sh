#!/usr/bin/env bash
# Captures `--help` output from the built CLIs into scripts/out/cli/*.txt, which
# sync-content.mjs turns into reference/cli.md. File name = command with "__" for
# spaces: "clrkernel__run.txt" -> "## clrkernel run --help".
#
# Expects the solution already built:  dotnet build ClrKernel.slnx -c Release
# Run from anywhere; paths are relative to this script.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
out="$here/out/cli"
cfg="${CONFIGURATION:-Release}"
mkdir -p "$out"

capture() {                       # capture <file-stem> <project> [args...]
  local stem="$1" proj="$2"; shift 2
  # Help goes to stdout in ClrKernel and to stderr in some Studio paths; take both.
  if ! dotnet run --project "$repo/src/$proj" -c "$cfg" --no-build -- "$@" --help > "$out/$stem.txt" 2>&1; then
    echo "warn: '$proj $* --help' exited non-zero; keeping whatever it printed" >&2
  fi
}

capture clrkernel            ClrKernel
capture clrkernel__run       ClrKernel        run
capture clrkernel-studio     ClrKernel.Studio
capture clrkernel-studio__serve ClrKernel.Studio serve

ls -la "$out"
