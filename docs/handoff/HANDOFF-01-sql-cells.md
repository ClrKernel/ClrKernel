# SQL language support (foundation)

`#!sql` cells now run T-SQL against Microsoft SQL Server, with T-SQL
highlighting, live syntax checking, keyword/function completion, secure named
connections, and results rendered as the existing interactive grid. All changes
are in your repo **uncommitted** (no commits made, per your workflow).

Everything was built, tested, and format-verified in an equivalent tree:
**136/136 tests pass**, `dotnet format` clean, Release build with 0 warnings,
and the VS Code extension compiles (`tsc`). The files on your Mac are
byte-identical to what was verified.

## What you get

- **`#!sql` cells** — set a cell's language to SQL (or start it with `#!sql`).
  Highlighting comes from VS Code's built-in `sql` grammar; syntax checking,
  completion, and diagnostics come from the ClrKernel server.
- **Live T-SQL syntax check** — parsed with Microsoft's ScriptDom (the SSMS/ADS
  grammar) and pushed as editor diagnostics as you type; also a pre-flight check
  before a cell runs.
- **Named, secret-free connections** — define with `#!sql-connect`, or use the
  **connection button** on each SQL cell's status bar (a guided QuickPick to
  pick / add connections). Passwords are typed into a masked input and stored in
  the **OS credential store** (macOS Keychain / Windows Credential Manager /
  Linux libsecret) — never written to the notebook. A pluggable
  `ISecretProvider` lets a Vault / Key Vault / CyberArk provider drop in later.
- **Cross-platform auth** — `--auth sql` (user + password from the store),
  `--auth integrated` (Windows Integrated on Windows, Microsoft Entra default on
  macOS/Linux), plus `--auth entra` / `entra-password` / `entra-interactive`.
- **Multiple connections per file** — a cell targets the default connection or
  one named by a leading `-- connections <name>` comment (valid T-SQL; the
  button writes it for you).
- **Interactive grid results** — each result set renders as the same
  sort/filter/Analyze grid your C# query results use.

Deferred to a follow-up (as agreed — "foundation first"): SQL bulk copy, MERGE
helpers, dependency checks, parallelization, progress bars, and SQL definition
deployment. Schema-aware completion (tables/columns from a live connection) is
also a planned add on top of this.

## New files to stage

- `src/ClrKernel.Sql/**` — the backend package: secret providers
  (`Secrets/`), `SqlConnectionSpec`, `SqlConnectionRegistry`, `SqlDirectives`,
  `TSqlSyntax`, `SqlLanguage` (completion/hover), `SqlSession`, and enums.
- `editors/vscode/src/sqlConnections.ts` — the connection button + QuickPick UI.
- `test/ClrKernel.UnitTest/SqlTest.cs` — 24 unit tests.
- `samples/Sql.nb.md` — a runnable sample notebook.

## Modified files to stage

- `ClrKernel.slnx` — adds the `ClrKernel.Sql` project.
- `src/ClrKernel.Core/ClrKernel.Core.csproj` — references `ClrKernel.Sql`.
- `src/ClrKernel.Core/InteractiveScriptEngine.cs` — `#!sql` / `#!sql-connect`
  dispatch + a lazy `Sql` session.
- `src/ClrKernel.Core/NotebookImporter.cs` — `sql`/`tsql` fences and `#!sql`
  DIB sections (headless `#!import`).
- `src/ClrKernel.Server/Lsp/LspServer.cs`, `Lsp/LspTypes.cs` — SQL
  completion/hover dispatch, live `publishDiagnostics`, and `clrkernel/sql/*`
  connection-management methods for the UI.
- `editors/vscode/src/controller.ts` — `sql` in `supportedLanguages`, `#!sql`
  prefix, and a `getClient` accessor.
- `editors/vscode/src/serverClient.ts` — `sql` added to the LSP document
  selector (so diagnostics/completion flow) + a generic `request` helper.
- `editors/vscode/src/extension.ts` — registers the SQL connection UI.
- `editors/vscode/src/markdownSerializer.ts` — `sql` fence round-trip.
- `editors/vscode/package.json` (0.5.0) + `package-lock.json` — version, new
  commands, updated description.
- `test/ClrKernel.UnitTest/ClrKernel.UnitTest.csproj` — references
  `ClrKernel.Sql`.
- `test/ClrKernel.UnitTest/NotebookImporterTest.cs` — one existing test used a
  `sql` fence as an example of a *skipped* language; switched to `python` since
  `sql` now runs.
- `README.md` — adds a "SQL cells" subsection.

```bash
git add src/ClrKernel.Sql editors/vscode samples/Sql.nb.md \
        ClrKernel.slnx README.md \
        src/ClrKernel.Core/ClrKernel.Core.csproj \
        src/ClrKernel.Core/InteractiveScriptEngine.cs \
        src/ClrKernel.Core/NotebookImporter.cs \
        src/ClrKernel.Server/Lsp/LspServer.cs \
        src/ClrKernel.Server/Lsp/LspTypes.cs \
        test/ClrKernel.UnitTest
```

## Suggested commit message

```
feat(sql): add #!sql cells with T-SQL check, secure connections, grid output

Adds a ClrKernel.Sql package and #!sql / #!sql-connect cells: T-SQL syntax
checking (ScriptDom) as live diagnostics, keyword/function completion, and
result sets rendered as the interactive grid. Connections are named and
secret-free — passwords resolve from the OS credential store (Keychain /
Credential Manager / libsecret) behind a pluggable ISecretProvider, never
written to the notebook. Cross-platform auth: SQL login, Windows Integrated,
and Microsoft Entra. A VS Code status-bar button + QuickPick guides picking
and adding connections. Foundation only; bulk copy / MERGE / dependency
parallelization / deployment to follow.
```

## Notes

- **Two new NuGet deps** (restored on first build): `Microsoft.Data.SqlClient`
  5.2.2 and `Microsoft.SqlServer.TransactSql.ScriptDom` 170.3.0.
- **Try it**: open `samples/Sql.nb.md`, edit the `#!sql-connect` server/database
  to a reachable SQL Server, click the cell's connection button to store the
  password, then run a query.
- **Headless/CI secrets** without a keychain: set
  `CLRKERNEL_SECRET_SQL_<NAME>` (e.g. `CLRKERNEL_SECRET_SQL_ANALYTICS`).
- **Cleanup**: the transfer archive is at `_to_delete/sql-delivery/` — delete
  that folder when convenient (the bridge can't remove files for you).
- **Still open from the last task**: adding `.nuke/temp/` to `.gitignore`
  (I can do that whenever you want).
