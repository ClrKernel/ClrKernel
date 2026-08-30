# `#!sql-bulk --create` + save/auto-load connections to `connections.json`

Two related additions. Build clean (**0 warnings**), `dotnet format` clean, **259 tests
pass / 8 skipped** (Docker), extension TypeScript compiles (`tsc`, 0 errors). All changes
are **uncommitted** in your repo. Extension stays at **0.4.0**.

> Note: the kernel side is fully unit-tested here. The VS Code UI pieces (the save prompt,
> the dropdown auto-load) are **compiled but not runtime-tested** in this environment — they
> want a click-through in your Windows dev host (the checklist has the steps).

---

## 1. `#!sql-bulk --create`

The bulk magic can now create the destination table from the source schema when it doesn't
exist — the same create-from-schema the C# `.Table(name).BulkCopyFrom(query,
createIfMissing: true)` already used.

```sql
#!sql-bulk --from analytics --query "SELECT * FROM dbo.Orders" --to warehouse --table stg.Orders --create
```

- `src/ClrKernel.Sql/Etl/BulkCopy.cs` — added `BulkCopyOptions.CreateIfMissing`; both
  `BulkCopyRunner.Execute` overloads now create the table from the source reader's schema
  (`SqlServerTableDefinition.Generate`) when the flag is set and the table is missing.
- `src/ClrKernel.Sql/Etl/SqlEtlDirectives.cs` — `--create` (alias `--create-if-missing`).
- `src/ClrKernel.Sql/SqlLanguage.cs` — `--create` added to `#!sql-bulk` completion.

Tested: `SqlBulkCreateDirectiveTest` (parse on/off/alias). The create-then-load itself runs
against SQL Server, so it's exercised via the Windows checklist (needs a live server).

## 2. Save & auto-load connections in `connections.json`

**The gap it closes:** SQL connections lived only in the session registry (in-memory, per
window). Now the button offers to persist them, and saved ones reload automatically.

**Save flow (button):** after Add/Edit, ClrKernel checks for a `connections.json` up the
folder tree from the notebook and prompts — *Save to this file* (shows the found path + its
existing connection names), *Choose a file…*, or *Don't save*. It writes a
`"$type": "SqlServer"` node; the password is a `{ "secret": "<ref>" }` **reference only**,
never plaintext (the password stays in the OS credential store).

**Auto-load:** on first cell run (and when the connection dropdown opens), the extension
registers every `SqlServer` entry from the nearest `connections.json` into the session, so a
reopened notebook resolves `-- connections <name>` without re-adding. Same file also feeds
the existing C# `Oracle.FromConfig` / `Odbc.FromConfig`.

### Kernel

- `src/ClrKernel.Data/Config/ConfigProperty.cs` (new) — write model (plain vs. `{secret}`).
- `src/ClrKernel.Data/Config/RawConnectionNode.cs` (new) — a node read **without** resolving
  secrets (so the secret *ref* is kept and loading never fails on a missing password).
- `src/ClrKernel.Data/Config/ConnectionConfig.cs` — added `FindFile`, `ListNames`,
  `LoadAllRaw`, and `Upsert` (merge-preserving writer via `System.Text.Json.Nodes`).
- `src/ClrKernel.Sql/SqlConnectionConfig.cs` (new) — `SqlConnectionSpec` ⇄ `SqlServer` node
  mapping (auth strings match `#!sql-connect --auth`).
- `src/ClrKernel.Sql/SqlSession.Config.cs` (new, partial) — `FindConfigFile`,
  `ConfigConnectionNames`, `LoadFromConfig(dir)`, `SaveConnectionToConfig(name, path)`.
- `src/ClrKernel.Server/Lsp/LspServer.cs` — LSP methods `clrkernel/sql/configStatus`,
  `clrkernel/sql/loadConnectionsConfig`, `clrkernel/sql/saveConnection` (+ param types).

Tested: `ConnectionConfigWriteTest` (write/round-trip, merge-preserve, secret-ref-not-
plaintext, up-tree discovery), `SqlConnectionConfigMappingTest` (spec round-trip, integrated
writes no password), `SqlSessionConfigTest` (save in one session → auto-load in a fresh one).

### Extension (compiled, not runtime-tested here)

- `editors/vscode/src/controller.ts` — `ensureConnectionsConfigLoaded(notebook)` (once per
  notebook, before the first run; best-effort, never blocks execution).
- `editors/vscode/src/sqlConnections.ts` — `promptSaveToConfig` after Add/Edit; auto-load
  before showing the connection dropdown.

## Docs / samples updated

`README.md` (bulk `--create`, save/auto-load), `editors/vscode/CHANGELOG.md` (0.4.0),
`samples/Sql.nb.md` ("Save a connection for next time" + JSON example),
`samples/SqlEtl.nb.md` (`--create`), and `docs/windows-verification-checklist.md`
(new test items).

## Files changed

```
src/ClrKernel.Data/Config/ConfigProperty.cs            (new)
src/ClrKernel.Data/Config/RawConnectionNode.cs         (new)
src/ClrKernel.Data/Config/ConnectionConfig.cs
src/ClrKernel.Sql/SqlConnectionConfig.cs               (new)
src/ClrKernel.Sql/SqlSession.Config.cs                 (new)
src/ClrKernel.Sql/Etl/BulkCopy.cs
src/ClrKernel.Sql/Etl/SqlEtlDirectives.cs
src/ClrKernel.Sql/SqlLanguage.cs
src/ClrKernel.Server/Lsp/LspServer.cs
editors/vscode/src/controller.ts
editors/vscode/src/sqlConnections.ts
editors/vscode/CHANGELOG.md
test/ClrKernel.UnitTest/ConnectionConfigWriteTest.cs   (new)
README.md
samples/Sql.nb.md
samples/SqlEtl.nb.md
docs/windows-verification-checklist.md
```
