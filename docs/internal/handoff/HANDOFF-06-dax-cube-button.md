# Cube connection button for `#!dax` cells

Adds a guided cube-connection button to DAX cells, mirroring the SQL connection
button. All changes are in your repo **uncommitted** (no commits, per your
workflow).

Verified: **206 unit tests pass, 5 skipped**, `dotnet format` clean, Release build
0 warnings, and the VS Code extension compiles.

## What you get

- **Status-bar button on every `#!dax` cell** showing the cube it runs against
  (or "Select cube"). Clicking it opens a QuickPick to pick an existing cube or
  add a new one — no `#!dax-connect` syntax to memorize.
- **Guided "Add cube…"** — asks for a name, then the cube type (on-prem SSAS /
  Fabric-Power BI / Azure AS), then the relevant fields (server + model, or
  workspace + model). It builds the `#!dax-connect` line, registers it via the
  kernel, and writes `-- connections <name>` into the cell.
- **Passwordless by design** — cube connections use Windows Integrated (on-prem
  SSAS) or Microsoft Entra (Fabric / Azure AS), so no secret is collected. For a
  rare SQL-login cube, use `#!dax-connect --user … --secret <env-var>` directly.

## New files to stage

- `editors/vscode/src/daxConnections.ts` — the cube status-bar button + QuickPick.

## Modified files to stage

- `src/ClrKernel.AnalysisServices/SsasConnectionRegistry.cs` — adds `All`
  (name/spec pairs), `Remove`, and `SetDefault` used by the connection RPCs.
- `src/ClrKernel.Server/Lsp/LspServer.cs` — `clrkernel/dax/listConnections`,
  `addConnection`, and `removeConnection` RPC methods for the UI.
- `editors/vscode/src/extension.ts` — registers `DaxConnectionUi`.
- `editors/vscode/package.json` (0.6.1) — adds the `clrkernel.dax.selectConnection`
  and `clrkernel.dax.addConnection` commands; `package-lock.json` — version bump.
- `test/ClrKernel.UnitTest/DaxTest.cs` — one test for the new registry methods.

```bash
git add editors/vscode src/ClrKernel.AnalysisServices/SsasConnectionRegistry.cs \
        src/ClrKernel.Server/Lsp/LspServer.cs \
        test/ClrKernel.UnitTest/DaxTest.cs
```

## Suggested commit message

```
feat(dax): cube connection button for #!dax cells

Adds a status-bar button on DAX cells and a guided QuickPick to pick or add a
cube — on-prem SSAS (Integrated), Fabric / Power BI, or Azure AS (Entra) — so
users don't have to write #!dax-connect by hand. Backed by new
clrkernel/dax/{list,add,remove}Connection RPCs and cube-registry helpers. Cube
connections are passwordless, so no secrets are collected in the UI.
```

## Notes

- **Recompile the extension** to pick up the button and the two new commands:
  `./build.sh Extension`.
- **Cleanup**: the transfer staging is at `_to_delete/` — delete when convenient
  (the bridge can't remove files for you).
- Possible follow-up: schema-aware DAX completion (table/measure/column names
  pulled from the connected cube).
