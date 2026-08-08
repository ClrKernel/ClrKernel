#!/usr/bin/env bash
# ClrKernel developer task runner (Nuke bootstrapper).
# Usage: ./build.sh [target] [--flags]    e.g. ./build.sh Test   ./build.sh --help
set -eo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if ! command -v dotnet >/dev/null 2>&1; then
  echo "The .NET SDK is required but 'dotnet' was not found on PATH." >&2
  echo "Install it from https://dotnet.microsoft.com/download and retry." >&2
  exit 1
fi
exec dotnet run --project "$SCRIPT_DIR/build/_build.csproj" --no-launch-profile -- "$@"
