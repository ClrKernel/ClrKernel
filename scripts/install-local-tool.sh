#!/usr/bin/env bash
# End-to-end test of the PACKAGED tool without touching nuget.org:
# pack into ./artifacts with a unique dev version, install/update the global
# tool from that local feed, then register the kernelspec that ships inside
# the package (via `clrkernel --kernel-spec-path`).
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$REPO_ROOT/src/ClrKernel"
FEED="$REPO_ROOT/artifacts"
BASE_VERSION="${BASE_VERSION:-0.3.0}"
DEV_VERSION="$BASE_VERSION-dev.$(date +%Y%m%d%H%M%S)"

dotnet pack "$PROJ/ClrKernel.csproj" -c Release -o "$FEED" -p:Version="$DEV_VERSION"

# `update` handles both first install and upgrade; the unique ascending
# version defeats NuGet's global-packages cache.
dotnet tool update --global ClrKernel --add-source "$FEED" --prerelease

export PATH="$HOME/.dotnet/tools:$PATH"
echo
clrkernel --kernel-spec-details
echo

jupyter kernelspec install "$(clrkernel --kernel-spec-path)" --user --name clrkernel
echo
echo "Kernel 'clrkernel' registered from packed tool $DEV_VERSION"
