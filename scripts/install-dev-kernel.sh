#!/usr/bin/env bash
# Register a Jupyter kernel that runs ClrKernel straight from this repo's build
# output. Iterate with `dotnet build` + restart the notebook kernel -- no file
# copying, no packing, no NuGet.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJ="$REPO_ROOT/src/ClrKernel"
CONFIG="${1:-Debug}"

dotnet build "$PROJ/ClrKernel.csproj" -c "$CONFIG"

DLL="$(find "$PROJ/bin/$CONFIG" -maxdepth 2 -name ClrKernel.dll | head -1)"
[ -n "$DLL" ] || { echo "ClrKernel.dll not found under $PROJ/bin/$CONFIG" >&2; exit 1; }

STAGE="$(mktemp -d)/clrkernel-dev"
mkdir -p "$STAGE"
cp "$PROJ"/kernel-spec/logo-*.png "$STAGE"/ 2>/dev/null || true
cat > "$STAGE/kernel.json" << JSON
{
    "argv": ["$(command -v dotnet)", "$DLL", "jupyter", "{connection_file}"],
    "display_name": "ClrKernel (dev build)",
    "language": "csharp"
}
JSON

jupyter kernelspec install "$STAGE" --user --name clrkernel-dev
echo
echo "Kernel 'clrkernel-dev' -> $DLL"
echo "Iterate: dotnet build ($CONFIG), then restart the kernel in Jupyter."
