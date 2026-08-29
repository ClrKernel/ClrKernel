# `#!dax` cell language (query a default cube)

Adds a DAX cell language, mirroring the `#!sql` model: `#!dax-connect` registers
named cubes (with a default), `#!dax` cells run DAX against the default (or a
named) cube and render an interactive grid, with autocompletion for the magics,
flags, cube names, and DAX keywords/functions. All changes are in your repo
**uncommitted** (no commits, per your workflow).

Verified: **205 unit tests pass, 5 skipped**, `dotnet format` clean, Release build
0 warnings, and the VS Code extension compiles. As with the SSAS package, I could
not run DAX against a live cube in the sandbox — the query path is validated by
your server; the routing, connection specs, directive parsing, completion, and
importer wiring are unit-tested.

## What you get

- **`#!dax-connect`** — register cubes: `--name`, `--server`, `--database`,
  `--default`; `--fabric --workspace W --model M` (Fabric / Power BI, Entra auth);
  `--azure-as` (Azure AS, Entra); `--user svc --secret <env-var>` (password from an
  environment variable, never the notebook — a committed `--password` is rejected);
  `--connection-string "..."`.
- **`#!dax` cells** — run DAX against the default cube, or one named with a leading
  `-- connections <name>` comment (valid DAX). Results render as the interactive
  grid. Set a cell's language to **DAX** in VS Code, or use a ` ```dax ` fence.
- **Editor** — a `dax` language + a DAX TextMate grammar (keywords, functions,
  `'Table'[Column]` refs, comments, strings) + language-configuration. DAX cells
  route to the kernel and get completion/hover.
- **Autocompletion** — the `#!dax` / `#!dax-connect` magics, each magic's flags,
  cube names after `--connections`, the `-- connections` directive, and DAX
  keyword/function completion inside a query (Ctrl+Space).
- **Headless** — ` ```dax ` fences and `#!dax` DIB sections run via `#!import` /
  the command-line runner.

The same models remain reachable from C# cells via `Ssas.Connect(...)` (ad-hoc,
separate from the `#!dax` cube registry).

## New files to stage

- `src/ClrKernel.AnalysisServices/` — `SsasConnectionRegistry.cs`, `DaxDirectives.cs`
  (parse `#!dax-connect` / cell selector), `SsasSession.cs` (registry + execute),
  `DaxLanguage.cs` (completion/hover).
- `editors/vscode/syntaxes/dax.tmLanguage.json`, `editors/vscode/language-configuration.dax.json`.
- `test/ClrKernel.UnitTest/DaxTest.cs` — 12 unit tests.
- `samples/Dax.nb.md`.

## Modified files to stage

- `src/ClrKernel.Core/InteractiveScriptEngine.cs` — lazy `Cubes` (SsasSession) +
  `#!dax-connect` / `#!dax` dispatch.
- `src/ClrKernel.Core/NotebookImporter.cs` — `dax` fences + DIB sections.
- `src/ClrKernel.Server/Lsp/LspServer.cs` — `dax` languageId → DAX completion/hover
  with cube-name context.
- `editors/vscode/src/controller.ts` (supportedLanguages + `#!dax` prefix),
  `markdownSerializer.ts` (dax fence), `serverClient.ts` (dax in the document
  selector); `package.json` (0.6.0, dax language + grammar) + `package-lock.json`.
- `README.md` — a DAX cells subsection.

```bash
git add src editors/vscode samples/Dax.nb.md README.md test
```

## Suggested commit message

```
feat(dax): #!dax cell language against a default cube

Adds a DAX cell language mirroring #!sql: #!dax-connect registers named cubes
(SSAS / Azure AS / Fabric-Power BI, with a default), and #!dax cells run DAX
against the default (or a -- connections named) cube, rendering an interactive
grid. Ships a DAX TextMate grammar and context-aware completion for the magics,
flags, cube names, and DAX keywords/functions. Backed by ClrKernel.AnalysisServices.
```

## Notes

- **Recompile the extension** to pick up the new language/grammar: `./build.sh Extension`.
- **Passwords**: `--secret <ref>` resolves from an environment variable
  (`CLRKERNEL_SECRET_<REF>`), consistent with the SQL side's headless convention.
  On-prem cubes typically use Integrated auth (no secret); Fabric/Azure AS use Entra.
- **Validate the query path against a real cube** — it couldn't run in the sandbox.
- **Cleanup**: staging is at `_to_delete/` — delete when convenient.

Possible follow-ups if useful: a cube connection button (like the SQL one) instead
of `#!dax-connect`, and schema-aware DAX completion (table/measure/column names
pulled from a connected model).
