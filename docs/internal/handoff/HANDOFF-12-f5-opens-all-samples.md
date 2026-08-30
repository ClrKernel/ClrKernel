# F5 opens all the sample notebooks

One-line fix so pressing **F5** (Run Extension) opens the full `samples/` folder in the
Extension Development Host, not the near-empty dev stub.

All changes are in your repo **uncommitted**; no version bump.

## What was wrong

`editors/vscode/.vscode/launch.json` pointed the dev host at
`${workspaceFolder}/samples` — i.e. `editors/vscode/samples`, which only contains
`hello.nb.md` and `test.ipynb`. The real sample set (Sql, SqlQuery, SqlEtl,
SqlPipeline, Dax, AnalysisServices (cube), MermaidDiagrams, FabricWarehouse,
MultiProvider, HttpRequests, PowerShell — 11 `.nb.md`) lives at the **repo-root**
`samples/`, which F5 never opened.

## What changed

- `editors/vscode/.vscode/launch.json` — the launch arg now opens
  `${workspaceFolder}/../../samples` (the repo-root `samples/`). All `*.nb.md` files
  open as ClrKernel notebooks (the `clrkernel-markdown` notebook type is registered
  for `*.nb.md`), so you can click any of them in the Explorer and run cells.

## Try it

Press **F5**. The dev host opens with the samples in the Explorer. `npm: watch` is the
pre-launch task, so the extension TypeScript recompiles automatically — but the
**ClrKernel server is .NET**, so make sure the solution is built first
(`dotnet build ClrKernel.slnx -c Release`) or the kernel won't start.

Notes on what runs without extra setup:

- **MermaidDiagrams**, **PowerShell**, **HttpRequests** — run as-is.
- **Sql / SqlQuery / SqlEtl / SqlPipeline** — need a reachable SQL Server; use the
  connection button (with "trust the server certificate" for a local instance).
- **Dax / AnalysisServices** — need a cube (SSAS / Fabric / Azure AS).
- **FabricWarehouse** — needs a Fabric tenant.
- **MultiProvider** — uses `#r "nuget: ClrKernel.Data.Oracle/.Odbc"`; those packages
  aren't on nuget.org yet, so that sample won't restore until you publish them (or add
  a local NuGet feed pointing at your `dotnet pack` output).

## Files to stage

```bash
git add editors/vscode/.vscode/launch.json \
        docs/handoff/HANDOFF-12-f5-opens-all-samples.md
```

Suggested commit message:

```
chore(vscode): F5 opens the repo-root samples folder

The Run Extension launch config pointed at editors/vscode/samples (only a hello stub);
point it at ../../samples so all the .nb.md samples open in the dev host.
```
