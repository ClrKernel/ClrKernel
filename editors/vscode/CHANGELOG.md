# Changelog

## [0.7.0] - 2026-08-19

**Needs kernel 0.10.x.** The extension no longer hard-codes any language: it
asks the kernel.

- **Languages come from the kernel.** The cell-language picker, the selector
  prepended when a cell runs, the language-tag map, and which cells get
  completion/hover all follow the descriptor list the kernel reports at
  connect time. A language added to the kernel — including one a notebook
  loads at run time with `#r "nuget: …"` — appears without an extension
  update.
- **One connection wizard for every language.** The separate SQL and DAX
  wizards are replaced by a single wizard generated from each provider's
  settings schema, so new providers get a working connection UI for free.
  Prompts now reflect what is actually required: credentials are asked for
  (and required) only for auth modes that use them, and a Fabric workspace
  asks for its semantic model.
- PowerShell cells now get language features from the kernel.

## [0.6.2] - 2026-08-17

- **Smaller, faster extension.** The extension is now bundled (esbuild): 17
  files in the package instead of 344, and activation no longer walks a
  `node_modules` tree. No functional changes.

## [0.6.1] - 2026-08-17

Fixes the two ways an upgrade could leave you on the wrong kernel.

- **A 0.9.0 kernel is now flagged, not silently accepted.** The version check
  compared only major.minor, so 0.9.0 counted as a supported kernel and nothing
  suggested 0.9.1 — you kept the old kernel and got the fallback behaviors
  (whole-server restart instead of per-notebook). The check now knows the
  minimum patch this build's RPCs need (0.9.1) and treats anything below it as
  out of date.
- **One-click "Update Kernel" that actually works on Windows.** The out-of-date
  warning now offers **Update Kernel**, which stops this window's kernel
  *before* running `dotnet tool update` — the manual command the old warning
  suggested always failed on Windows, because the running `clrkernel.exe` keeps
  the tool locked while any notebook window has it open. If another window
  still holds a kernel, the error now says so instead of failing cryptically.

## [0.6.0] - 2026-08-15

Pairs with **kernel 0.9.x** (`ClrKernel` 0.9.1). Installing through the
extension pins `--version 0.9.*`, and a mismatched pair warns with the exact
command to get back in step, the same guard as before. Against a plain 0.9.0
kernel everything still works — the per-notebook features below simply fall
back to the old whole-server behavior.

- **Each notebook is its own session.** Variables, connections, loaded packages
  and language state no longer leak between notebooks open in the same window —
  every notebook gets its own kernel session inside the one server process, and
  IntelliSense sees only its own notebook's state. **Restart Kernel** now
  restarts just the active notebook's session and leaves the others running
  (against an older kernel it falls back to restarting the whole server).
- **Documentation everywhere, like an IDE.** Hover, completion, and signature
  help now show `///` summaries — from nuget packages (their bundled XML docs),
  the .NET base library (ref-pack docs), and ClrKernel's own API (the packages
  now ship XML documentation). Completion fetches each item's documentation
  lazily as you move through the list, so the list itself stays fast.
- **Peek a namespace from its `using` line.** Go to Definition on
  `using System.Text;` opens a browsable overview of the namespace's public
  types with their summaries (capped at 300 types); F12 on a type name in a
  cell still opens its full decompiled source. The using line resolves wherever
  the caret sits — trailing comments, tabs, `global`/`static`/alias forms, and
  cells that start with `#r` lines all work.
- **Define extension methods in a cell.** A cell containing extension-method
  classes (or namespace declarations) — which Roslyn's script mode cannot host —
  now compiles as a real class library behind the scenes: it executes,
  re-running an edited cell replaces the previous definition, the methods
  complete with their `///` docs, and Go to Definition shows their source.
  Works with `#r "nuget: …"` in the same cell, including a package version
  newer than one the kernel itself ships.
- **Go to Definition no longer works "sometimes".** Fixed the causes of
  intermittent failures: duplicate assembly identities poisoning symbol
  resolution, caret-position strictness, and using directives being mangled by
  the merged-document parse. Completion documentation can also no longer show
  the wrong symbol's text when a newer completion list arrives mid-resolve.
- **`Fabric.Interactive()`** connects to Microsoft Fabric with an explicit
  browser sign-in that always prompts for the account — for when you want to
  pick, rather than have `Fabric.Connect()`'s credential chain silently reuse
  an ambient az CLI or Visual Studio session.

## [0.5.0] - 2026-08-11

- **Go to Definition / Peek Definition in C# cells.** Right-click a symbol (or
  F12 / ⌥F12) to jump to — or peek — where it's defined: functions, variables,
  records, and classes from earlier cells or the same cell, across cells in the
  notebook. Definitions reflect the live session, the same way completion does,
  and the peek frames the whole declaration, not just its first line.
- **Decompiled source for everything else.** Go to Definition on a symbol with
  no notebook source — the BCL, nuget packages, ClrKernel's own types — opens
  readable decompiled C# (ILSpy engine) in a read-only document, scrolled to
  the member you asked about.
- **Remote cells: SSH for shell, PSRemoting for PowerShell.** Register a target
  with `#!shell-connect --name web01 --host … --user …` (key auth via your ssh
  keys/agent/config) or `#!pwsh-connect --name srv --host …` (`--ssh` default,
  or `--winrm --user … --secret <ref>` with the password coming from the OS
  credential store, never a file), then run any cell on it with
  `#!bash --connection web01` / `#!pwsh --connection srv`. Remote PowerShell
  state lives in a persistent remote runspace; remote shell cells keep their
  working directory per target. Targets can be saved in `connections.json`
  (`"$type": "Ssh"` — shared by both languages — or `"$type": "PSRemoting"`).
- **Shell cells.** Set a cell's language to **Shell Script** (or start it with
  `#!bash` / `#!zsh` / `#!sh`) to run shell commands. The working directory and
  exported environment persist across cells like one terminal session, stderr is
  captured inline, a non-zero exit fails the cell with its exit code, and ANSI
  colour renders in the output (the session advertises a colour terminal, so
  tools don't go monochrome just because output is piped). Executable markdown
  round-trips ` ```bash `, ` ```zsh `, ` ```sh `, and ` ```shell ` fences.

- **One display pipeline, user-overridable.** `Display(x)` and a bare trailing `x`
  now render identically — both go through a formatter registry
  (`DisplayFormatters` in `ClrKernel.Core.Primitives`, default renders in the new
  `ClrKernel.Formatting.Html` package). A cell can override any render:
  `DisplayFormatters.Register<DisplayTable, DisplayHtml>(t => …)` — the newest
  registration wins. New concept-based helpers: `x.DisplayTable()`,
  `x.DisplayHtml()`, `x.DisplayMarkdown()`, `bytes.DisplayBytes("image/png")`, and
  the returned cell updates in place (`cell.UpdateProgress(…)` etc.).
- **Fixed: a trailing `x.Display()` printed the value and then the handle.** A
  display handle is a structure, never rendered; the engine now suppresses it.
- **Images and other binary output render.** `DisplayBytes` (base64 on the wire)
  becomes real bytes in the notebook renderer, so `image/png`, `application/pdf`
  and friends display instead of showing base64 text.
- **BREAKING (packages) — `DisplayData(text, html)` is gone.** Producers emit
  display concepts (`DisplayTable`, `DisplayConsoleText`, `DisplayBadge`, …) and
  the registry renders them; `ClrKernel.Core.Primitives` no longer contains any
  HTML (`ResultFormatter`, `InteractiveTable`, `AnsiRenderer` moved to
  `ClrKernel.Formatting.Html`). The `DisplayTable()` extension overloads now
  return a `DisplayCell` instead of a `DisplayedValue`.

- **BREAKING (protocol) — the connection RPCs are now language-neutral.** The eight
  `clrkernel/sql/*` and three `clrkernel/dax/*` methods are replaced by one
  `clrkernel/connections/*` set that takes a `languageId`: `list`, `add`, `remove`,
  `setDefault`, `configStatus`, `loadConfig`, `saveConfig`. This extension version and the
  kernel must be updated together. DAX gains `remove` and `setDefault`, which its registry
  always supported but never exposed; a language with no connections (HTTP, Mermaid,
  PowerShell) now answers with a clear "no connection support" instead of having no method.
- **BREAKING — packages renamed.** The kernel's NuGet packages were reorganised into
  three tiers: `ClrKernel.Core.*` (engine and hosts), `ClrKernel.Language.*` (cell
  languages), and `ClrKernel.Database*` (data access). Notably `ClrKernel.Sql` split
  into `ClrKernel.Language.Sql` + `ClrKernel.Database.Provider.SqlServer`,
  `ClrKernel.AnalysisServices` split into `ClrKernel.Language.Dax` +
  `ClrKernel.Database.Provider.AnalysisServices`, and `ClrKernel.Data.*` became
  `ClrKernel.Database.Provider.*`. Any notebook with an `#r "nuget: ClrKernel.…"`
  line for an opt-in provider (Oracle, ODBC, JDBC, Fabric) needs that line updated.
  The old package IDs are not forwarded.
- **BREAKING — the C# entry points were renamed** to match their packages:
  `Sql` → `SqlServer` and `Ssas` → `AnalysisServices`. So `Sql.Connection(...)`
  becomes `SqlServer.Connection(...)`, `Sql.BulkCopy(...)` becomes
  `SqlServer.BulkCopy(...)`, and `Ssas.Connect(...)` becomes
  `AnalysisServices.Connect(...)`. There is no compatibility alias — an old notebook
  fails with `CS0103: The name 'Sql' does not exist in the current context`, which
  names exactly what to change. `Fabric`, `Oracle`, `Odbc` and `Jdbc` are unchanged.
  The variable that `#!sql-connect --var` binds is unaffected; only the global's name
  changed.
- **BREAKING — `.Transaction()` on a SQL database** now returns the shared
  `DataSourceTransaction` rather than `SqlDatabaseTransaction`, and its owning-database
  property is `.DataSource` rather than `.Database`. Code using only `tx.Execute` /
  `tx.Query` / `tx.Commit` / `tx.Rollback` is unaffected. Two long-standing bugs are
  fixed as a result: `DefaultCommandTimeout` is now honoured by `Execute`/`Scalar`, and
  the `limit` argument to a transaction's `Query` is no longer ignored.
- **Fixed: duplicate completions and false "syntax error" squiggles in C# cells.**
  C# cells now use a dedicated language id (shown as **C#** with the C# icon) instead
  of `csharp`, so other C# tooling (C# Dev Kit / the Roslyn language server) no longer
  attaches to notebook cells — which had been adding a second set of completion entries
  and flagging valid script-mode trailing expressions (e.g. a bare `x`) as errors.
  Highlighting is unchanged (it uses the embedded C# grammar) and files still serialize
  as ` ```csharp `.
- **Save & auto-load connections.** After adding a connection with the button you
  can save it to a `connections.json` (it shows a file found nearby, or lets you
  choose one) — passwords stay in the OS credential store, only a reference is
  written. Saved `SqlServer` entries load automatically in later sessions, so a
  connection you saved is available again without re-adding it.
- **`#!sql-bulk --create`.** The bulk magic can now create the destination table
  from the source schema when it doesn't already exist (the same create-from-schema
  the C# `.Table(name).BulkCopyFrom(query, createIfMissing: true)` uses).
- Results grid: closed the gap between each column header and its filter box, and
  the column names and their border now stay pinned instead of scrolling away.

## [0.4.1] - 2026-08-11

Compatibility guard. No new features — this release exists so that the next
kernel, which changes the private `clrkernel/*` RPC surface, cannot silently
break an installed setup.

- **The extension no longer moves your kernel off the version it speaks.** Installing
  now pins `--version 0.8.*`. Previously, an install onto a machine that already had
  the tool fell back to a bare `dotnet tool update --global ClrKernel`, which pulls
  whatever is newest — so publishing a newer kernel could have upgraded working
  setups into an incompatible pair without anyone asking for it.
- **A mismatched kernel now says so.** The version reported in the language-server
  handshake is checked against what this build supports, and a mismatch shows one
  warning naming the version found and the command to get back. Cells still run —
  execution is unaffected — but the SQL and DAX connection buttons need a matching
  pair, so the notice explains that rather than leaving them mysteriously dead.

## [0.4.0] - 2026-08-09

Data notebooks — SQL, DAX, and multi-database querying with an interactive
results grid — plus HTTP, Mermaid, and PowerShell cells.

- **SQL cells.** Set a cell's language to **SQL** (or start it with `#!sql`) to
  run T-SQL against Microsoft SQL Server, with T-SQL highlighting, live syntax
  checking, and keyword/function completion. Connections are named and
  secret-free: define them with `#!sql-connect`, or use the **connection button**
  next to the cell's language picker — it prompts for server, database,
  authentication, and encryption, and stores the password in your OS credential
  store (macOS Keychain, Windows Credential Manager, or Linux libsecret), never
  in the notebook.
- **Interactive results grid.** SQL results and C# query output render in a
  sortable grid with a global filter, a **per-column filter row**, a **per-column
  value picker** (a ▾ dropdown of that column's distinct values, with search and
  select-all / clear), a one-click **Clear**, and an **Analyze** panel of
  per-column statistics. All filters combine.
- **Connections as C# variables.** A `#!sql-connect --name analytics` also binds
  a C# variable `analytics` (when the name is a valid identifier), so C# cells can
  query it right away; use `--var <name>` for a custom name or `--no-var` to skip.
- **Query databases from C#.** An ergonomic `Sql` API —
  `Sql.Connection(server, db).Query(sql).Results()` — returns rows that render as
  the grid and enumerate as dynamic or typed (`.Results<T>()`) objects, plus
  `.Scalar<T>()`, `.Execute()`, `.Table()` (a bulk-copy target that can create
  itself from the source schema), and `.Transaction()`.
- **Bulk copy, MERGE & pipelines.** Move and upsert data with `#!sql-bulk` /
  `#!sql-merge` (or the `Sql.BulkCopy` / `Sql.Merge` API), build dependency-ordered
  ETL from `-- step` / `-- needs` annotations with `#!sql-run`, and deploy a folder
  of `.sql` definitions idempotently with `#!sql-deploy`.
- **Other databases (Oracle, ODBC).** Opt-in provider packages
  (`ClrKernel.Data.Oracle`, `ClrKernel.Data.Odbc`) give the same
  `Query(sql).Results()` experience — grid, typed rows, tables, transactions —
  against Oracle and ODBC sources, and a `connections.json` config file keeps
  connection settings out of notebooks. (A JDBC provider is available as
  experimental.)
- **DAX cells & Analysis Services.** Set a cell's language to **DAX** (or `#!dax`)
  to query SSAS, Azure Analysis Services, or Fabric / Power BI semantic models,
  with a cube **connection button** that adds and edits cubes. From C#, the `Ssas`
  helper queries with DAX, reads model metadata, and processes partitions.
- **Fabric Warehouse writes.** The `Fabric` helper bulk-loads Microsoft Fabric
  Warehouse tables (staging Parquet to OneLake and loading with `OPENROWSET`),
  can create the target table from a reader's schema, and reloads table segments
  in parallel — all with Microsoft Entra authentication.
- **HTTP request cells.** Set a cell's language to **HTTP** (or `#!http`) and
  write requests in the VS Code REST Client `.http` syntax — variables, system
  variables (`{{$guid}}`, `{{$timestamp}}`, …), `###`-separated requests, and
  chaining (`{{login.response.body.$.token}}`). Each request renders a rich
  response card: color-coded status, timing and size, collapsible headers, and a
  pretty-printed, highlighted JSON body.
- **Mermaid diagram cells.** Set a cell's language to **Mermaid** (or `#!mermaid`)
  to render flowcharts, sequence, class, state, ER, gantt, pie, and more —
  **fully offline** (the library is embedded, no CDN) and following the editor's
  light/dark theme.
- **PowerShell cells.** Set a cell's language to **PowerShell** (or `#!pwsh`) to
  run PowerShell in an in-process runspace — variables, functions, and imported
  modules persist across cells — with native IntelliSense (completion, hover, and
  signature help) served from the live runspace. No separate PowerShell install
  needed.
- **Executable markdown** now round-trips ` ```sql `, ` ```dax `, ` ```http `,
  ` ```mermaid `, and ` ```powershell ` fenced blocks as their respective cells,
  alongside `csharp` — the file stays readable markdown on GitHub.
- **Clearer SQL errors.** The actual server message — with error number,
  severity, and line — now surfaces in the cell output instead of a bare stack
  trace, and the connection prompt offers a **trust-server-certificate** option
  for self-signed or local servers.
- **Edit connections in place.** The SQL connection dropdown (and the DAX cube
  dropdown) now lets you edit an existing connection, not just add one.
- **Run All** stops at the first failing cell by default (matching the headless
  runner); set `clrkernel.stopOnCellError` to `false` to run every selected cell
  regardless.

## [0.3.0] - 2026-08-07
- C# IntelliSense in notebook cells — completion, hover, and signature help —
  with no C# Dev Kit required. Powered by a built-in language server that shares
  the execution engine, so completions reflect the live session: variables from
  executed cells, `#r "nuget:"` types, and imports.
- The extension now launches `clrkernel lsp` (a unified language server) and
  carries execution + language features over one connection. Default
  `clrkernel.server.args` is now `["lsp"]`; a dev build uses
  `dotnet` + `["<path>/ClrKernel.dll", "lsp"]`.

## [0.2.0] - 2026-08-07
- The notebook server now ships inside the `ClrKernel` CLI tool and is launched
  as `clrkernel serve` (the standalone `ClrKernel.Server` dotnet tool is gone).
- Auto-install now installs the `ClrKernel` global tool
  (`dotnet tool install --global ClrKernel`).
- Default settings updated: `clrkernel.server.command` is now `clrkernel` and
  `clrkernel.server.args` defaults to `["serve"]`. If you previously overrode
  these to point at `ClrKernel.Server`, update them to `clrkernel`/`serve` (or
  `dotnet` with `["<path>/ClrKernel.dll", "serve"]` for a dev build).

## [0.1.1] - 2026-08-07
- Setting up automatic publish extension

## [0.1.0] - 2026-08-06

Initial release.

- Executable markdown notebooks (`*.nb.md`): fenced `csharp` blocks as code
  cells, prose as markdown cells, clean round-trip serialization.
- **ClrKernel C#** notebook controller executing through `ClrKernel.Server`
  (JSON-RPC over stdio) on the ClrKernel.Core Roslyn engine.
- REPL state across cells; `#r "nuget: ..."` package references;
  `#!import`/`#!lib` shared libraries with prefixes and run-once semantics.
- Streaming console output and in-place display updates (`DisplayAs`/`Update`).
- Configurable server launch (`clrkernel.server.command` / `.args`) with clear
  startup diagnostics in the ClrKernel output channel.
