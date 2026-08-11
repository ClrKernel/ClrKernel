# PLAN — Package taxonomy refactor (Core / Language / Database / DataEngineering)

> **This is a forward-looking plan, not a record of completed work.** It is written to be
> stopped and resumed. Before doing anything, read **§0 Resume protocol** and the
> **§1 Progress board** — the board is the single source of truth for where the work is.

Restructures the 17 projects into a 22-project taxonomy with three tiers — `ClrKernel.Core.*`
(implementation), `ClrKernel.Language.*` (cell languages), `ClrKernel.Database*` (data access) —
plus a new provider-agnostic `ClrKernel.DataEngineering`. Package IDs, assembly names, root
namespaces, and notebook-facing entry types all change. This is a **breaking change** for
published packages and for existing notebooks.

---

## 0. Resume protocol

1. Read the **Progress board** (§1). The lowest phase not marked `DONE` is the resume point.
2. Run `git log --oneline -15` — each landed phase is one commit whose subject starts with
   `refactor(taxonomy): P<n> …`. The board and the log must agree; if they don't, trust the log
   and fix the board.
3. Run the phase gate for the last `DONE` phase (§3) to confirm the tree is green before adding
   to it.
4. Work the next phase. When it is green, commit it, tick the board **in the same commit**.

**Commit convention for this work:**
`refactor(taxonomy): P<n> <what moved>` — e.g. `refactor(taxonomy): P1 extract ClrKernel.Core.Secrets`.
**Do not add any AI/assistant co-author trailer or attribution to these commits.**

**Branching:** phased commits directly on `main` (decision D9). Each commit must be green on its
own — `main` will carry intermediate states where the taxonomy is half-applied, and that is
accepted.

---

## 1. Progress board

| Phase | Scope | Status |
| --- | --- | --- |
| P0 | Baseline capture + guardrails | DONE |
| P1 | Extract `ClrKernel.Core.Secrets` | DONE |
| P2a | Rename the `Core.*` group | DONE |
| P2b | Rename `Language.*` (Http/Mermaid/PowerShell) + `Database*` providers; Jdbc into the solution | DONE |
| P3 | Cell-language registration seam in `Core.Scripting` | DONE |
| P4a | Split `ClrKernel.Sql` → `Language.Sql` + `Database.Provider.SqlServer` (move only) | DONE |
| P4b | Fluent dedup: rebase `SqlDatabase`/`SqlQuery`/`SqlTable` onto `DataSource*` (D7) | CODE DONE — live gate OPEN |
| P5 | Split `ClrKernel.AnalysisServices` → `Language.Dax` + `Database.Provider.AnalysisServices` | DONE |
| P6 | Extract shared Entra auth into `ClrKernel.Database.Entra` (new package, D5 amended) | DONE — live gate OPEN |
| P7 | Entry-type renames (`Sql`→`SqlServer`, `Ssas`→`AnalysisServices`) + samples/README/extension sweep | TODO |
| P8 | New `ClrKernel.DataEngineering` (table actions + step DAG) | TODO |
| P9 | Split the test project three ways | TODO |
| P10 | Final verification sweep | TODO |

Status values: `TODO` → `IN PROGRESS` → `DONE`. The commit that ticks a row to `DONE` **is** that
phase's commit, so its sha can't be written into itself — `git log --grep "refactor(taxonomy): P<n>"`
is the record. Add a `— <sha>` suffix only when back-filling a row after the fact.

---

## 2. Decisions already made (do not re-litigate)

| # | Decision | Consequence |
| --- | --- | --- |
| D1 | **Clean break on NuGet.** New PackageIds only; no meta-packages, no type forwarders. Old packages get deprecated/unlisted on nuget.org manually. | Existing notebooks' `#r "nuget: ClrKernel.Data.Oracle"` stop resolving new versions. Samples/README/checklist must be swept. |
| D2 | **Namespaces = package names.** `ClrKernel.Core` → `ClrKernel.Core.Scripting`, `ClrKernel.Data` → `ClrKernel.Database`, etc. | Every `namespace`/`using` in `src/`, `test/`, and samples changes. |
| D3 | **`ClrKernel.DataEngineering` is abstractions, not implementations.** It holds the step/DAG handling and a table-action model — Insert; Delete (optional where); Truncate; Merge from source (optional where); Truncate+Insert from source; Delete (optional where)+Insert from source. Each provider implements the actions its own way (SQL Server bulk copy, Fabric Parquet+delete/insert, Oracle bulk load). | Providers depend on DataEngineering, not the reverse. No SqlClient/Fabric/Oracle types in DataEngineering. |
| D4 | **Add the registration seam now.** `Core.Scripting` must stop referencing `Language.*`. | The engine's selector chain and the LSP language surface both move behind contracts; the CLI becomes the composition root. See P3. |
| D5 | **Fabric split by workload, shared Entra auth.** `Provider.AnalysisServices` keeps semantic-model work (incl. `ConnectFabric`); `Provider.Fabric` keeps warehouse/OneLake work; both consume shared auth. **Amended in P6 (user decision): the shared auth lives in a new `ClrKernel.Database.Entra` package, not in `ClrKernel.Database`.** `ClrKernel.Database` has zero `PackageReference`s, and putting `Azure.Identity` there would have pushed it transitively into `Provider.Oracle`, `Provider.Odbc`, `Provider.Jdbc`, `Provider.SqlServer` and `Language.Sql` — none of which do Entra, and three of which are opt-in `#r "nuget: …"` packages where download weight is user-visible. | New package: **22 projects**, one more `slnx` and `release.yml` entry. `ClrKernel.Database` stays dependency-free. |
| D6 | **Jdbc is renamed *and* added to `ClrKernel.slnx`.** | It compiles in CI for the first time. IKVM restore/build on ubuntu is **unverified** — see risk R2. |
| D7 | **Collapse the fluent duplication during the split.** `SqlDatabase`/`SqlQuery`/`SqlTable` rebase onto `ClrKernel.Database`'s `DataSource`/`DataSourceQuery`/`DataSourceTable` (see D13) while they move. **Landed in P4b**; the original mitigation ("the SQL fluent tests run at each") was hollow — those tests are `Inconclusive` without a server — so P4b carries an explicit live gate instead. | Unseals `DataSourceQuery`/`DataSourceTable` and adds `virtual` to seven members of a shipped package. Deletes `SqlDatabaseTransaction`, so `db.Transaction()` returns `DataSourceTransaction` and its owner property is `.DataSource`. Three behaviour changes adopted from the shared base — see P4b. |
| D8 | **Entry types renamed:** `Sql` → `SqlServer`, `Ssas` → `AnalysisServices`. `Fabric`, `Oracle`, `Odbc`, `Jdbc` already match and stay. | Every sample cell body, README snippet, and the C# variable binding emitted by `#!sql-connect` changes. |
| D9 | **Phased commits on `main`.** No AI/assistant attribution in commit messages. `CLAUDE.md` is gitignored. | Done: `.gitignore` entry added. |
| D10 | **Flat layout** — `src/<PackageName>/<PackageName>.csproj`. | Keeps `Build.cs`'s `ResolveProject` and `./build.sh --project <name>` working unchanged. |
| D11 | **Test project split three ways** — `Core`, `Language`, `Database`. | `Build.cs` gains a test-project list; `InternalsVisibleTo` entries multiply. |
| D12 | **Version bump is the user's call**, done manually once the refactor is tested and working. | `Directory.Build.props` stays `0.8.0` through every phase. No tag, no release, mid-refactor. `release.yml` hard-fails if a tag doesn't match `<Version>`. |
| D13 | **The shared fluent type family is renamed `Database*` → `DataSource*`** (`DataSource`, `DataSourceQuery`, `DataSourceTable`, `DataSourceTransaction`), decided during P2b. Renaming the package/namespace to `ClrKernel.Database` collided with the type `Database`: inside `ClrKernel.Database.*` it resolves, but any sibling namespace (`ClrKernel.UnitTest`, and `ClrKernel.Language.Sql` after P4) binds `Database` to the **namespace** and fails CS0118. | Changes the return type of `Oracle.Connect` / `Odbc.FromConnectionString` / `Jdbc.Connect` / every `FromConfig`. `DataSourceTransaction`/`DataSourceTable` expose the owner as `.DataSource`. Inside `DataSource.cs` the static `CreateCommand` is called fully qualified (`ClrKernel.Database.DataSource.CreateCommand`) because the instance property shadows the type. |

---

## 3. Current-state facts this plan depends on

Established by inspection on 2026-08-10. Re-verify if resuming much later.

- **17 projects**, all in `ClrKernel.slnx` **except `ClrKernel.Data.Jdbc`**, which is deliberately
  excluded (its csproj comment: IKVM is Windows-centric, would break Linux CI).
- **16 packages are packed explicitly by name** in `.github/workflows/release.yml` (the `Pack` step
  is a hand-written list of `dotnet pack src/<X>/<X>.csproj` lines — it is *not* a solution-wide
  pack). Every rename must edit that list; every new project must be added to it.
- `release.yml` **fails the build if the git tag doesn't match `<Version>` in
  `Directory.Build.props`** (currently `0.8.0`).
- **Baseline moved from 267 to 275 total in P3 slice 1**: `CellSelectorOrderingTest` adds 8
  passing tests (selector precedence for every prefix pair). Skips stay at 8. Compare later
  phases against **272 passed / 8 skipped / 280 total** — P3 slice 4 added 5 more
  (`CellLanguageServicesTest`, covering the language-service adapters, which no RPC harness
  reaches: `lsp_harness.py` only exercises C#).
- **CI gates on formatting before build**: `dotnet format ClrKernel.slnx --verify-no-changes`. A
  phase that renames namespaces without a format pass fails CI, not the phase.
- **P0 found `main` already format-dirty** and therefore CI-red before this refactor started:
  `test/ClrKernel.UnitTest/FluentSqlTest.cs:185` had a stray space before a closing paren
  (`…DW2025" )`), committed in `2cae638`. Fixed in the P0 commit via `./build.sh Format --apply`;
  the fix is one character and touched nothing else. Worth knowing that the format gate is
  *load-bearing and was failing* — don't assume a red CI in a later phase is your doing without
  checking `main` first.
- `ClrKernel.Core` **project-references** `Http`, `Mermaid`, `PowerShell`, `Primitives`, `Sql`,
  `AnalysisServices`, `Fabric`. This is the layering inversion D4 fixes.
- `InteractiveScriptEngine.ExecuteAsync` (`src/ClrKernel.Core/InteractiveScriptEngine.cs`, ~L333–465)
  is an **ordered** `TryStrip*Selector` if-chain, 9 language branches. Order is load-bearing
  (`#!sql-connect` before `#!sql`, `#!dax-connect` before `#!dax`).
- The engine is constructed in **five** places: `ExecuteHandler` (Jupyter), `NotebookRunner` ×2,
  `NotebookServer`, `LspServer`.
- `LspServer.cs` (818 lines) reaches directly into `ClrKernel.Sql.TSqlSyntax`,
  `ClrKernel.Sql.SqlCompletion`/`SqlCompletionContext`, `_engine.Sql.Connections`,
  `_engine.Sql.Pipeline`, the DAX equivalents, and `_engine.PowerShell.{Complete,Hover,SignatureHelp}`.
  It also exposes `clrkernel/sql/{configStatus,loadConnectionsConfig,saveConnection}`. **The seam
  needs a language-*service* contract, not just an execution contract.**
- `#!sql-connect` binds a C# variable by generating script text:
  `var {name} = Sql.Database("<conn>");` — string-generated, so D8's rename must update the
  generated text, and the seam (P3) needs a "post-connect script binding" hook.
- `ClrKernel.Data`'s `InternalsVisibleTo` lists five assemblies **by name**:
  `ClrKernel.UnitTest`, `ClrKernel.Sql`, `ClrKernel.Data.Oracle`, `ClrKernel.Data.Odbc`,
  `ClrKernel.Data.Jdbc`. All five names change.
- `test/tools/server_harness.py:79` asserts `initialize.name == "ClrKernel.Server"`.
- `src/ClrKernel` (the CLI) **keeps its name**, so `kernel-spec/kernel.json`,
  `scripts/install-dev-kernel.sh`, `scripts/install-local-tool.sh`, and the packages' README/icon
  asset paths (`../ClrKernel/kernel-spec/logo-64x64.png`) are unaffected.
- **Baseline test count (measured 2026-08-10, `./build.sh Test`):
  `Failed: 0, Passed: 259, Skipped: 8, Total: 267`.** The 8 skips are the SQL Server integration
  tests (no live server / Docker locally). Every later phase must match these numbers.
- Caveat on that baseline: the test csproj multi-targets `net8.0;net9.0;net10.0`, but on this
  machine (only SDK `10.0.106` installed) the run reported **one** result line —
  `ClrKernel.UnitTest.dll (net8.0)`. So 259/8 is the **net8.0** figure, not all three TFMs. CI
  uses `dotnet-version: 10.0.x` and may differ; re-baseline from a CI run before trusting the
  number for the P9 sum-check.

---

## 4. Target structure

### 4.1 Package map

| New package | From | Notes |
| --- | --- | --- |
| `ClrKernel` | `src/ClrKernel` | **Unchanged name.** Becomes the composition root (P3). |
| `ClrKernel.Core.Scripting` | `ClrKernel.Core` | Engine, `#!import`, `#r` resolution. Loses all `Language.*` references in P3. |
| `ClrKernel.Core.Primitives` | `ClrKernel.Primitives` | `DisplayData`, `InteractiveTable`, `ProgressBar`, formatters. |
| `ClrKernel.Core.Secrets` | `ClrKernel.Data/Secrets/*` | `SecretStore`, `ISecretProvider`, OS/env/in-memory providers. |
| `ClrKernel.Core.LanguageServices` | `ClrKernel.LanguageServices` | Roslyn completion/hover for C#. |
| `ClrKernel.Core.Runner` | `ClrKernel.Runner` | Headless `run` mode. |
| `ClrKernel.Core.ExtensionServer` | `ClrKernel.Server` | `serve` + `lsp` hosts. |
| `ClrKernel.Core.JupyterKernel` | `ClrKernel.Jupyter` | ZeroMQ wire protocol, kernelspec. |
| `ClrKernel.Language.Mermaid` | `ClrKernel.Mermaid` | |
| `ClrKernel.Language.PowerShell` | `ClrKernel.PowerShell` | |
| `ClrKernel.Language.Http` | `ClrKernel.Http` | |
| `ClrKernel.Language.Sql` | `ClrKernel.Sql` (language half) | Magics, directive parsing, T-SQL syntax check, completions, cell session. |
| `ClrKernel.Language.Dax` | `ClrKernel.AnalysisServices` (DAX half) | `DaxLanguage.cs`, `DaxDirectives.cs`, the DAX cell session. |
| `ClrKernel.Database` | `ClrKernel.Data` minus `Secrets/` | Fluent base + `connections.json` config + shared Entra auth (P6). |
| `ClrKernel.Database.Entra` | **new** (P6) | Shared Microsoft Entra credential chains, token acquisition and scopes. Referenced only by `Provider.AnalysisServices` and `Provider.Fabric` — see D5. |
| `ClrKernel.DataEngineering` | **new** | Table-action model + step/DAG orchestration. Abstractions only (D3). |
| `ClrKernel.Database.Provider.SqlServer` | `ClrKernel.Sql` (provider half) | Fluent SQL Server, connection spec/registry/config, bulk copy, MERGE, T-SQL deploy. |
| `ClrKernel.Database.Provider.Fabric` | `ClrKernel.Fabric` | Warehouse writes, OneLake Parquet staging, reload batch. |
| `ClrKernel.Database.Provider.AnalysisServices` | `ClrKernel.AnalysisServices` (non-DAX) | Connections (incl. `ConnectFabric`), metadata, processing. |
| `ClrKernel.Database.Provider.Oracle` | `ClrKernel.Data.Oracle` | |
| `ClrKernel.Database.Provider.Odbc` | `ClrKernel.Data.Odbc` | |
| `ClrKernel.Database.Provider.Jdbc` | `ClrKernel.Data.Jdbc` | **Enters `ClrKernel.slnx`** (D6). |

### 4.2 Target dependency layering

```
Core.Primitives      Core.Secrets
        \                /
      ClrKernel.Database  ──►  ClrKernel.DataEngineering
                                        │
      Database.Provider.{SqlServer, Fabric, AnalysisServices, Oracle, Odbc, Jdbc}
                                        │
      Language.{Sql, Dax, Http, Mermaid, PowerShell}   ──┐
                                                          ├─► contracts defined in
      Core.Scripting  (engine + language contracts)  ◄────┘   Core.Scripting, NOT refs to Language.*
                │
      Core.{JupyterKernel, ExtensionServer, Runner, LanguageServices}
                │
           ClrKernel (CLI) ── composition root: registers every Language.* implementation
```

**The one rule that makes the names honest:** after P3, `Core.Scripting` has **zero**
`ProjectReference` entries pointing at `Language.*` or `Database.*`. Enforce it by reading the
csproj, not by intent.

### 4.3 File-level split of `ClrKernel.Sql` (P4)

| To `Language.Sql` | To `Database.Provider.SqlServer` | To `DataEngineering` (P8) |
| --- | --- | --- |
| `SqlLanguage.cs` | `Fluent/SqlDatabase.cs` | `Pipeline/Pipeline.cs` |
| `SqlDirectives.cs` | `Fluent/SqlQuery.cs` | `Pipeline/PipelineStep.cs` |
| `SqlOrchestrationDirectives.cs` | `Fluent/SqlTable.cs` | `Pipeline/PipelineRunner.cs` |
| `Etl/SqlEtlDirectives.cs` | `Fluent/SqlServerTableDefinition.cs` | `Pipeline/PipelineBoard.cs` |
| `TSqlSyntax.cs` | `SqlConnectionSpec.cs` | generic deploy pass-retry loop |
| `SqlCellException.cs` | `SqlConnectionRegistry.cs` | (extracted from `Deploy/DeployRunner.cs`) |
| `SqlSession.cs` | `SqlConnectionConfig.cs` | |
| `SqlSession.Etl.cs` (thin facade) | `SqlAuthMode.cs` | |
| `SqlSession.Orchestration.cs` (thin facade) | `Etl/BulkCopy.cs` | |
| `SqlSession.Config.cs` | `Etl/MergeBuilder.cs` | |
| `Fluent/SqlSession.Fluent.cs` | `Etl/DataTableBuilder.cs` | |
| | `Etl/CountingDataReader.cs` | |
| | `Etl/SqlIdentifier.cs` | |
| | `Deploy/CreateOrAlter.cs` (T-SQL specific) | |
| | `Deploy/GoBatchSplitter.cs` (T-SQL specific) | |
| | `Deploy/DeployBoard.cs` | |

Judgement call recorded: **connection identity is a provider concern** (spec, registry, config
mapping, auth mode live in `Provider.SqlServer`); **the cell session is a language concern**
(`SqlSession` lives in `Language.Sql` and holds a registry owned by the provider).

**Namespace mapping (D2), verified against the tree after P3.** `ClrKernel.Sql` holds four
namespaces and they do **not** split along the same line as the files:

| Today | Goes to |
| --- | --- |
| `ClrKernel.Sql` (20 files) | `ClrKernel.Language.Sql`, except the connection/fluent files → `ClrKernel.Database.Provider.SqlServer` |
| `ClrKernel.Sql.Etl` (6 files) | `SqlEtlDirectives` → `Language.Sql`; `BulkCopy`, `MergeBuilder`, `DataTableBuilder`, `CountingDataReader`, `SqlIdentifier` → `Provider.SqlServer` |
| `ClrKernel.Sql.Deploy` (4 files) | `CreateOrAlter`, `GoBatchSplitter`, `DeployBoard` → `Provider.SqlServer`; `DeployRunner` stays in `Language.Sql` until P8 lifts its generic pass-retry loop into `DataEngineering` |
| `ClrKernel.Sql.Pipeline` (4 files) | stays in `Language.Sql` until P8 |

Also move the two files P3 added: `SqlCellLanguage.cs` (with `SqlGlobals`) and
`SqlCellLanguageServices.cs` → `Language.Sql`.

**The runtime-only failure mode in this phase.** `SqlCellLanguage.ScriptContribution` imports the
namespace strings `"ClrKernel.Sql"` and `"ClrKernel.Sql.Etl"`, and `SqlGlobals` is reached through
`"using static ClrKernel.Sql.SqlGlobals;"`. All three are **string literals** — stale ones compile
clean and fail only when a cell runs (the P2a lesson). The cell-facing types they carry are
`MergeSpec`, `BulkCopyOptions` and `BulkCopyResult`, which land in `Provider.SqlServer`, so the
contribution must import **both** new namespaces. `FluentSqlTest` and `SqlEtlTest` execute cells
through the engine and are what catches this.

**Tripwire (from P3).** Re-pointing the registration should be one edit in
`src/ClrKernel/CellLanguages.cs` plus the `using` in the test fixture — a registration change, not
a contract change. If `ICellLanguage` or `ICellLanguageServices` has to change to make the split
work, stop and record why: that means the seam was shaped wrong, and bending it here hides the
problem instead of fixing it.

**Resolved (P4a): `Language.Sql` intentionally references SqlClient.** The pre-P4 check framed
this as `SqlSession.Etl.cs`'s `OpenConnection` return type, but it is broader — `SqlSession.Execute`
itself opens a `SqlConnection`, reads a `SqlDataReader`, and catches `SqlException` to build the
msg/level/line error detail (HANDOFF-10), and `TSqlSyntax` parses with ScriptDom. `#!sql` is a
T-SQL cell language, not a generic SQL one; making it provider-neutral is a redesign, not a
taxonomy move. Both `PackageReference`s stay in `ClrKernel.Language.Sql.csproj` with a comment
pointing here, so nobody later files it as a layering bug.

**Amendments made during P4a** (both forced by the compiler, not by preference):

- **`SqlCellException.cs` moved to `Provider.SqlServer`**, not `Language.Sql` as §4.3 listed.
  `Fluent/SqlDatabase.cs` throws it when a secret can't be resolved, and language→provider is the
  only legal direction. The language half still throws it freely.
- **`Deploy/DeployBoard.cs` stayed in `Language.Sql`**, not `Provider.SqlServer` as §4.3 listed.
  It renders `DeployState`/`DeployFileResult`, which are declared in `DeployRunner.cs` — and
  `DeployRunner` stays language-side until P8. It is the deploy run's presentation, so it belongs
  with the run; this also matches `PipelineBoard`. Only `CreateOrAlter` and `GoBatchSplitter` (pure
  T-SQL text manipulation) went to the provider.

**Namespaces were flattened, not preserved.** Both new packages use a single namespace —
`ClrKernel.Language.Sql` and `ClrKernel.Database.Provider.SqlServer` — dropping the old `.Etl`,
`.Deploy` and `.Pipeline` sub-namespaces. This is D2 applied literally, and it halves the number of
namespace string literals the script contribution has to get right. One fallout: `SqlSession`'s
`Pipeline` property and the `Pipeline` type now share a namespace, resolved by the C# color-color
rule (`Pipeline.Pipeline` qualification removed).

### 4.4 File-level split of `ClrKernel.AnalysisServices` (P5)

| To `Language.Dax` | To `Database.Provider.AnalysisServices` |
| --- | --- |
| `DaxLanguage.cs` | `Ssas.cs` → entry type `AnalysisServices` (D8) |
| `DaxDirectives.cs` | `SsasConnection.cs` |
| `SsasSession.cs` → rename to the DAX cell session | `SsasConnection.Processing.cs` |
| | `SsasConnectionSpec.cs` |
| | `SsasConnectionRegistry.cs` |
| | `SsasMetadata.cs` |

### 4.5 Test split (P9)

| `ClrKernel.Core.UnitTest` | `ClrKernel.Language.UnitTest` | `ClrKernel.Database.UnitTest` |
| --- | --- | --- |
| `DisplayedValueTest` | `HttpFileParserTest` | `ConnectionConfigWriteTest` |
| `InteractiveTableTest` | `HttpExecutionTest` | `FluentSqlTest` |
| `ResultFormatterTest` | `HttpRendererTest` | `MultiProviderTest` |
| `TrailingExpressionTest` | `HttpVariableResolverTest` | `SqlEtlTest` |
| `NotebookImporterTest` | `MermaidTest` | `SqlPhase2bTest` |
| `NotebookOutputTest` | `PowerShellTest` | `SqlIntegrationTest` |
| `RunnerParametersTest` | `SqlTest` (directives/magics) | `SsasTest` |
| `ScriptLanguageServiceTest` | `DaxTest` | `FabricTest` |
| `UnitTest1` | | |

Files that straddle (notably `SqlTest`) get split by test method, not moved wholesale.

**Sequencing note:** the split is last so that P1–P8 each edit one test project instead of three.
If a resumed session prefers the opposite trade, moving P9 to just after P2b is safe — nothing
later depends on the split.

---

## 5. Phases

Every phase ends with its gate green and **one commit**. Phase gates are cumulative — a later
phase's gate includes all earlier ones.

### P0 — Baseline capture + guardrails

- Run `./build.sh Test`; **write the exact pass/skip/fail counts into §3** of this file. Every
  later phase compares against it.
- Run `./build.sh Format` and confirm clean *before* touching anything.
- Confirm `.gitignore` contains `CLAUDE.md` (already added).
- Commit: the baseline numbers + this plan document.

**Gate:** `./build.sh Format` clean; `./build.sh Test` green; counts recorded.

### P1 — Extract `ClrKernel.Core.Secrets`

Smallest real move; proves the mechanics (folder, csproj, namespace, `InternalsVisibleTo`,
`slnx`, `release.yml`) before they are applied 20 times.

- New `src/ClrKernel.Core.Secrets/` from `src/ClrKernel.Data/Secrets/*` (6 files).
- Namespace `ClrKernel.Data.Secrets` → `ClrKernel.Core.Secrets`.
- `ClrKernel.Data` gains a `ProjectReference` to it; consumers (`ClrKernel.Sql`, Oracle, Odbc,
  Jdbc, tests) update their `using`.
- Add to `ClrKernel.slnx` **and** to `release.yml`'s pack list.

**Gate:** solution builds; `./build.sh Format` clean; test count matches baseline.

### P2a — Rename the `Core.*` group

`ClrKernel.Core`→`Core.Scripting`, `Primitives`→`Core.Primitives`,
`LanguageServices`→`Core.LanguageServices`, `Runner`→`Core.Runner`,
`Server`→`Core.ExtensionServer`, `Jupyter`→`Core.JupyterKernel`.

Per project: `git mv` the folder and csproj, update `RootNamespace` + `PackageId` + `Description`,
rewrite `namespace`/`using`, fix relative `ProjectReference` paths, fix `InternalsVisibleTo`
names, update `ClrKernel.slnx` and `release.yml`.

Also: `test/tools/server_harness.py:79` expects `"ClrKernel.Server"` from `initialize` — update to
whatever `NotebookServer.Initialize()` now reports.

**Gate:** P1 gate + `./build.sh Extension` compiles + `python3 test/tools/server_harness.py <dll>`
passes.

### P2b — Rename `Language.*` and `Database*` providers

`Http`→`Language.Http`, `Mermaid`→`Language.Mermaid`, `PowerShell`→`Language.PowerShell`,
`Data`→`Database`, `Data.Oracle`→`Database.Provider.Oracle`, `Data.Odbc`→`Database.Provider.Odbc`,
`Fabric`→`Database.Provider.Fabric`, `Data.Jdbc`→`Database.Provider.Jdbc`.

`ClrKernel.Sql` and `ClrKernel.AnalysisServices` are **not** renamed here — they are split in
P4/P5 and renaming them twice is wasted churn.

**Jdbc (D6):** add to `ClrKernel.slnx` and to `release.yml`'s pack list; update the csproj's
exclusion comment to say what changed. If IKVM restore or build fails on ubuntu CI, fall back to
keeping it out of the solution and record that here as a plan amendment — do not spend the phase
fighting it (risk R2).

**Gate:** P2a gate + a `dotnet build` of the Jdbc project on this machine.

### P3 — Registration seam

**Deliberately sequenced before the splits** (user decision, 2026-08-10). The selector chain would
otherwise be edited in the SQL split, the DAX split, and the entry-type rename before being
deleted here. Doing the seam first means those three phases land behind a contract that is already
stable, and each one re-registers rather than re-editing an if-chain. The trade, accepted: the
plan's highest-regression-risk work (R1) happens before the splits have shaken out — so R1's
ordering tests are written **first**, in this phase, against the current behavior.

At this point `ClrKernel.Sql` and `ClrKernel.AnalysisServices` still have their old names (they are
renamed as part of their splits in P4/P5). The seam is built against those names and the P4/P5
registrations simply change which assembly implements the contract.

The phase that makes the taxonomy real (D4). Two contracts, both defined in `Core.Scripting`:

1. **Execution** — a cell-language contract: does this selector match this cell, and run it.
   Replaces the ordered `TryStrip*Selector` if-chain with an ordered registry. **Selector
   ordering must be preserved explicitly** (a registration priority or an ordered list) — a
   hash-ordered registry silently breaks `#!sql-connect` vs `#!sql`. This is the single highest
   regression risk in the plan.
   Include a hook for post-execution script bindings so `#!sql-connect` can still emit
   `var x = Sql.Database("…");` into the C# script state (that generated text becomes
   `SqlServer.Database(…)` in P7).
2. **Language services** — completion, hover, signature help, diagnostics. `LspServer` currently
   hard-codes `IsSql`/`IsDax`/`IsPowerShell` branches and reaches into
   `_engine.Sql.Connections`/`_engine.Sql.Pipeline` for completion context. Those become
   registry lookups keyed by cell languageId.

Then: engine constructors in all **five** call sites (`ExecuteHandler`, `NotebookRunner` ×2,
`NotebookServer`, `LspServer`) take the registry; `src/ClrKernel/Program.cs` composes it.

**Write the ordering tests before the seam** (R1): one test per prefix pair —
`#!sql-connect` vs `#!sql`, `#!dax-connect` vs `#!dax` — asserting the longer selector wins.
They must pass against today's if-chain first, so they are proving the seam, not describing it.

**§4.2 amendment (made in P3, as the phase text allows).** `Core.ExtensionServer` keeps
`ProjectReference`s to `ClrKernel.Sql`, `ClrKernel.AnalysisServices` and
`ClrKernel.Language.PowerShell` — **solely** for the 14 connection-management RPCs the VS Code
extension calls (`clrkernel/sql/{listConnections,addConnection,storeSecret,removeConnection,
setDefault,configStatus,loadConnectionsConfig,saveConnection}`, `clrkernel/dax/*`). Those are a UI
wire contract typed against each session's own API; genericising them means inventing a connection
abstraction, which belongs with `ClrKernel.DataEngineering` in P8, and would break the extension
today. The exemption is scoped: **language *features* — completion, hover, signature help,
diagnostics — must still dispatch through the registry.** Its boundary check:

```bash
grep -c "IsSql\|IsDax\|IsPowerShell\|BuildSqlContext\|SqlCompletion\|DaxCompletion" \
  src/ClrKernel.Core.ExtensionServer/Lsp/LspServer.cs     # must be 0 after slice 4
```

**Verify the rule mechanically — across the other four `Core.*` consumers.** The
pattern must include the not-yet-renamed `ClrKernel.Sql` / `ClrKernel.AnalysisServices`:

```bash
grep -l "ClrKernel\.Language\.\|ClrKernel\.Database\|ClrKernel\.Sql\|ClrKernel\.AnalysisServices" \
  src/ClrKernel.Core.Scripting/*.csproj src/ClrKernel.Core.ExtensionServer/*.csproj \
  src/ClrKernel.Core.JupyterKernel/*.csproj src/ClrKernel.Core.Runner/*.csproj \
  src/ClrKernel.Core.LanguageServices/*.csproj    # must print nothing
```

`Core.ExtensionServer` is the one that actually violates this today: `LspServer.cs` hard-references
`ClrKernel.Sql.{TSqlSyntax,SqlCompletion,SqlCompletionContext}`, `_engine.Sql.Connections`,
`_engine.Sql.Pipeline`, and the DAX/PowerShell equivalents. A check scoped only to
`Core.Scripting` passes with the inversion still in place. If a `Language.*` reference from
`ExtensionServer` turns out to be unavoidable, amend §4.2 to say so explicitly rather than
quietly leaving it.

**Also update `CLAUDE.md`** in this phase: its "adding a cell language touches four places"
section is replaced by "register an implementation of the cell-language contract".

**Gate** (revised in P3 — the original was unsatisfiable off-Windows): P2b gate + **dispatch
reaches the right handler for all eleven selectors** (`#!http`, `#!mermaid`, `#!pwsh`,
`#!powershell`, `#!sql-connect`, `#!sql`, `#!sql-bulk`, `#!sql-merge`, `#!sql-run`, `#!sql-deploy`,
`#!dax-connect`, `#!dax`), which `CellSelectorOrderingTest` asserts locally + both RPC harnesses
pass + headless smoke (`jupyter nbconvert` over `test/notebooks/smoke.ipynb`, and `smoke-fail.ipynb`
must still exit non-zero). **Live** execution of the SQL verbs needs a server — those are the 8
skipped tests — so it stays deferred to `docs/windows-verification-checklist.md`. A green tick here
does **not** mean verified against SQL Server.

### P4 — Split `ClrKernel.Sql`

**Split into P4a and P4b during execution**, because the original single gate could not tell them
apart. The three `FluentSqlTest` methods that exercise the code D7 rewrites — `Query_results_…`,
`Table_bulkcopy_create_if_missing_and_exists`, `Transaction_rolls_back_on_dispose` — all gate on
`CLRKERNEL_TEST_SQL` and go `Assert.Inconclusive` without a server. So "`FluentSqlTest` green" is
satisfiable without executing a single line of rebased fluent code, and a combined phase would
leave a later live failure ambiguous between the move and the rebase. Separating them keeps P4a
fully verifiable offline.

#### P4a — the move (DONE)

1. Create `Database.Provider.SqlServer`; move the provider-half files (§4.3, as amended there) with
   namespaces flattened. `Language.Sql` (the renamed remainder) references it.
2. Re-point the P3 registration at `Language.Sql` — a registration edit and a composition-root
   edit, **not** a change to the contract.
3. Update `release.yml` (one entry becomes two) and `slnx`.

**Tripwire held.** The registration re-point was exactly what P3 predicted: one `using` in
`src/ClrKernel/CellLanguages.cs`, one in `test/ClrKernel.UnitTest/TestCellLanguages.cs`, and two
qualified type names in `LspServer.cs`. `ICellLanguage` and `ICellLanguageServices` were not
touched.

**Gate (met):** P3 gate + 272 passed / 8 skipped / 280 total (baseline), `dotnet format` clean,
extension compiles, server harness 10/10, lsp harness 10/10.

The script contribution is the part that only fails at run time, and it *is* covered offline:
`FluentSqlTest.Sql_connection_is_usable_from_a_csharp_cell` and
`Engine_binds_variable_usable_from_a_csharp_cell` both execute a C# cell that calls through
`SqlGlobals` into a `SqlDatabase` — a type that now lives in the other assembly. If either the
`references` array or an `imports` string were stale, those throw `CompilationErrorException`
before touching a network.

#### P4b — the fluent dedup (D7) — code landed, live gate still open

`SqlDatabase : DataSource`, `SqlQuery : DataSourceQuery`, `SqlTable : DataSourceTable`, with the
SQL-Server-specific surface kept as additions on top. `SqlDatabaseTransaction` was **deleted**, not
rebased — it was a line-for-line copy of `DataSourceTransaction` differing only in `SqlConnection`
vs `DbConnection` typing that nothing consumed. `db.Transaction()` now returns
`DataSourceTransaction`; the owner property on it is `.DataSource`, not `.Database`.

**Changes to `ClrKernel.Database` (a shipped package's public API):**

- `DataSourceQuery` and `DataSourceTable` unsealed. `DataSourceTransaction` stayed sealed — nothing
  needed to derive from it once the SQL copy was deleted.
- `virtual` added to `DataSource.Name`, `.Open()`, `.Query()`, `.Table()`, `DataSourceQuery.OpenReader()`,
  `DataSourceTable.Query()` and `.Count()`. Nothing else — `Execute`, `Scalar`, `Transaction`,
  `Results` and `Insert<T>` all route through the virtual members, so they are shared unchanged.
- Constructors stayed `internal`; `ClrKernel.Database` already grants `InternalsVisibleTo` to the
  SQL provider (retargeted in P4a), so derived types reach them without promoting to `protected`.
  That IVT is load-bearing now, not incidental.

**Three deliberate behaviour changes**, all adopting the shared implementation over the SQL copy:

1. `SqlDatabase.Execute`/`Scalar` now honour `DefaultCommandTimeout`. The old SQL versions passed
   `null` and silently ignored the property they documented. No effect unless it is set.
2. `Transaction().Query(sql, parameters, limit)` now honours `limit`. The old
   `SqlDatabaseTransaction.Query` accepted the parameter and dropped it.
3. The connection string — and therefore the secret — is resolved inside the connection factory, on
   `Open()`, instead of in `SqlDatabase.Open()`'s body. Same timing (construction still never touches
   the credential store), same `SqlCellException` wrapping; `FluentSqlInheritanceTest` pins it.

`SqlDatabase.Name` is an **override reading the spec**, not the base's constructor-captured string.
`SqlConnectionSpec.Name` has a setter, so capturing it would have been a silent behaviour change.

**What was verified locally** (`FluentSqlInheritanceTest`, 4 new tests, no server): the fluent chain
stays SQL-typed through `Query`/`Table`/`Table().Query()`; virtual dispatch survives an upcast to
`DataSource` (a `new`-hiding implementation would fail this); `Name` tracks a renamed spec; and an
unresolvable secret throws `SqlCellException` from `Open()` rather than at construction.
`MultiProviderTest` covers the modified base over SQLite.

**Gate: still OPEN.** No local test opens a SQL Server connection, so none of
`Open()`→`SqlConnection`, `OpenReader()`→`SqlDataReader`, `BulkCopyFrom`, `Exists`, `Truncate` or
`count_big` has been executed since the rebase. Run the `CLRKERNEL_TEST_SQL` suite
(`FluentSqlIntegrationTest`) against a real server and record it in
`docs/windows-verification-checklist.md` before treating P4b as finished. Local 276/8/284 does
**not** discharge this.

### P5 — Split `ClrKernel.AnalysisServices` (DONE)

Per §4.4, and it went exactly as §4.4 predicted — the only phase so far where no placement had to
be amended. `Language.Dax` holds `DaxLanguage`, `DaxDirectives`, `SsasSession` (the DAX cell
session) and the two P3 files; `Database.Provider.AnalysisServices` holds `Ssas`,
`SsasConnection`(+`.Processing`), `SsasConnectionSpec`, `SsasConnectionRegistry` and `SsasMetadata`.
Namespaces are flat, matching P4a.

**The script contribution names one assembly here, not two** (unlike SQL): nothing in `Language.Dax`
is reachable from a C# cell. Cells call `Ssas.Connect(...)`, which is entirely provider-side, so
`DaxCellLanguage.ScriptContribution` imports only `ClrKernel.Database.Provider.AnalysisServices`.
`SsasTest` executes `Ssas.Connect(...)` in a cell and is what would catch a stale string.

**Gate (met):** P4a gate + `DaxTest`, `SsasTest` green — 276 passed / 8 skipped / 284 total,
`dotnet format` clean, 0 warnings, extension compiles, both RPC harnesses 10/10.

**Landmine handed to P7.** D8 renames the entry type `Ssas` → `AnalysisServices`, and it would now
live in namespace `ClrKernel.Database.Provider.AnalysisServices` — a type whose name matches the
last segment of its own namespace. That is the exact D13 collision: inside the namespace it
resolves, but `ClrKernel.UnitTest` and `ClrKernel.Language.Dax` would bind `AnalysisServices` to the
**namespace** and fail CS0118. D13 solved it by renaming the type (`Database` → `DataSource`).
Decide the same way before starting P7 — either the entry type gets a different name, or every
sibling-namespace reference gets fully qualified.

### P6 — Shared Entra auth into `ClrKernel.Database.Entra` (DONE, live gate open)

New package `ClrKernel.Database.Entra` holds `EntraScopes` (the four scope strings) and
`EntraAuth` (credential factories + token acquisition). `Provider.AnalysisServices` and
`Provider.Fabric` reference it; nothing else does. See D5 for why it is not in `ClrKernel.Database`.

**The two credential chains were deliberately left different.** They were not the same before:

| | chain |
| --- | --- |
| `Ssas` (Azure AS, Fabric semantic models) | `DefaultAzureCredential(includeInteractiveCredentials: true)` |
| `Fabric` (warehouse, OneLake) | `ChainedTokenCredential(DefaultAzureCredential(false), InteractiveBrowserCredential())` |

They are close enough to look like duplication and are not. Merging them changes the
credential-probe order for at least one provider, and that failure mode is a *working* connection
under the wrong identity, or an unexpected browser prompt — never a compile error, and not
reachable by any offline test. So `EntraAuth` exposes them as two named factories,
`DefaultWithInteractiveFallback()` and `DefaultThenInteractiveBrowser()`, each constructing exactly
what its provider constructed before, with a remark on each pointing at the other. **Unifying them
is a separate change that needs a live tenant.** P6 as landed has zero behaviour delta by
construction.

**SQL Server is deliberately out of scope.** It does Entra through
`SqlAuthenticationMethod.ActiveDirectory*` inside the connection string and never touches
`TokenCredential` or `Azure.Identity` — it shares nothing with this code path.

Both providers keep their own `Azure.Identity` `PackageReference`: they still use `Azure.Core`
types (`TokenCredential`, `AccessToken`) in their public signatures, and `Azure.Identity` is what
supplies it.

**Gate:** P5 gate met — 276 passed / 8 skipped / 284 total, 0 warnings, format clean, extension
compiles, both RPC harnesses 10/10. But `SsasTest`/`DaxTest` never touch Entra, so green here means
"still compiles and the non-Entra paths work", **not** that sign-in was verified. The Entra rows are
in `docs/windows-verification-checklist.md` §11a, including an identity check — success alone does
not prove the probe order is unchanged.

### P7 — Entry-type renames + docs/sample sweep

- `Sql` → `SqlServer`, `Ssas` → `AnalysisServices` (D8).
- Update the **generated** binding text in `InteractiveScriptEngine`'s post-connect hook
  (`var {x} = Sql.Database(...)` → `SqlServer.Database(...)`) and the engine's default-usings list.
- Sweep, in this order: `samples/*.nb.md` (both `#r "nuget:"` lines and cell bodies), `README.md`,
  `editors/vscode/README.md`, `docs/windows-verification-checklist.md`,
  `editors/vscode/src/{sqlConnections,daxConnections}.ts` if they emit directive or code text.
- `editors/vscode/CHANGELOG.md` — add a breaking-change entry; do **not** rewrite history entries.

**Gate:** P6 gate + `./build.sh Extension` + every `samples/*.nb.md` opens and its first cell runs
under `clrkernel run` or the dev kernel.

### P8 — `ClrKernel.DataEngineering`

New project. Abstractions only (D3):

- **Table actions** as data structures + a provider-implemented contract: `Insert`;
  `Delete(optional where)`; `Truncate`; `Merge(from source, optional where)`;
  `TruncateInsert(from source)`; `DeleteInsert(optional where, from source)`.
- **Step/DAG handling** moved from `ClrKernel.Sql/Pipeline/*` and made provider-agnostic (the
  runner currently assumes T-SQL execution — that dependency inverts into the provider).
- The generic pass-retry deploy loop lifts out of `Deploy/DeployRunner.cs`; `CREATE OR ALTER`
  rewriting and `GO` batch splitting stay in `Provider.SqlServer`.

Then: `Provider.SqlServer` implements the actions with `SqlBulkCopy` + `MERGE`; `Provider.Fabric`
with Parquet staging + delete/insert; `Provider.Oracle` with its own bulk path. Oracle/Odbc/Jdbc
implementations can be stubs that throw `NotSupportedException` **only if** each stub is listed
here as remaining work.

**Open design item (decide at the start of this phase):** whether `#!sql-run`'s status board
(`PipelineBoard`, `DeployBoard`) is provider-agnostic UI that belongs in `DataEngineering`, or
stays with the language. It renders through `Core.Primitives`, so either works.

**Gate:** P7 gate + `SqlPhase2bTest` (pipeline) green + `#!sql-run` and `#!sql-deploy` exercised
against a live SQL Server per the Windows checklist, or explicitly deferred there.

### P9 — Test project split

Per §4.5. Update `build/Build.cs` — `TestProject` becomes a list and the `Test` target iterates;
`--filter` still applies to each. Update every `InternalsVisibleTo` that names `ClrKernel.UnitTest`.

**Gate:** P8 gate + the sum of the three projects' test counts equals the P0 baseline. Any
difference must be explained in this document, not absorbed.

### P10 — Final verification sweep

- Work §6's checklist end to end.
- `./build.sh All` green from a clean tree (`./build.sh Clean` first).
- Full `docs/windows-verification-checklist.md` pass on a Windows host (Integrated auth, SSAS
  processing, Fabric, Oracle, Jdbc).
- Rewrite this file's header to past tense and renumber it as the completed
  `HANDOFF-17-…`, keeping the decision table.
- **Do not bump the version or tag** — D12, the user does that.

---

## 6. Global touchpoint checklist

Easy to miss; check on **every** rename phase.

- [ ] `<RootNamespace>` and `<PackageId>` in each csproj (they are set explicitly, not inferred)
- [ ] `<Description>` text — several mention old package names in prose
- [ ] Relative `ProjectReference` paths (`../ClrKernel.X/…`) in **every** referencing csproj
- [ ] `InternalsVisibleTo` — by **assembly name**; `ClrKernel.Data` has five entries
- [ ] `ClrKernel.slnx` project list
- [ ] `.github/workflows/release.yml` — the hand-written `dotnet pack` list (16 → 22 entries)
- [ ] `build/Build.cs` — `ResolveProject` (flat layout, D10) and `TestProject` (P9)
- [ ] `.nuke/build.schema.json` — parameter description mentions `ClrKernel.Http` (cosmetic)
- [ ] `namespace` and `using` across `src/` and `test/`
- [ ] `samples/*.nb.md` — `#r "nuget: …"` lines **and** cell bodies (entry types)
- [ ] `README.md` and `editors/vscode/README.md`
- [ ] `docs/windows-verification-checklist.md` — names packages and `#r` lines directly
- [ ] `editors/vscode/` — `package.json`, `src/*.ts`, LSP method names (`clrkernel/sql/*`)
- [ ] `test/tools/*.py` — `server_harness.py` asserts the server's `initialize.name`
- [ ] `CLAUDE.md` (gitignored, so it never shows in a diff — and it is the file most likely to
      rot). Its project list, its "`Core` project-references Http/Mermaid/PowerShell/Sql/…" fact,
      and its "adding a cell language touches four places" section are all invalidated by
      P2b–P7 and P3. Update it in the same phase that invalidates it.
- [ ] `./build.sh Format` after every namespace rewrite (CI gates on it)
- [ ] **Namespace strings the compiler never sees.** `InteractiveScriptEngine.DefaultUsingStatics`
      and the script `ScriptOptions` imports list hold namespaces as *string literals*
      (`"using static ClrKernel.Core.Scripting.Extensions;"`, `"ClrKernel.Core.Primitives"`,
      `"ClrKernel.Language.Sql"`, `"ClrKernel.Database.Provider.SqlServer"`,
      `"ClrKernel.AnalysisServices"`, `"ClrKernel.Database.Provider.Fabric"`). A stale one
      **builds clean** and fails only when a cell runs. P2a hit this. `grep -rn '"ClrKernel\.'
      --include="*.cs" src` after every rename, and rely on `./build.sh Test` (the scripting tests
      execute real cells) to catch it.
      **The `references:` array is a second, separate trap** (found in P4a): a `ScriptContribution`
      carries assemblies *and* namespaces, and once a package splits, one `typeof(X).Assembly` no
      longer covers every type the imports name. Roslyn needs one metadata reference per assembly,
      so an import string can be perfectly correct and still fail to resolve.

**Shell gotcha, learned the hard way in P2b:** the default shell here is **zsh**, which does
**not** word-split unquoted parameter expansions. `for x in $PAIRS` and `perl … $FILES` iterate
over the whole string as a *single* word — in P2b that silently ran one rename with the first
pair's old name and the last pair's new name, moving `ClrKernel.Data.Oracle` onto
`ClrKernel.Database`. Use an array (`F=(a b c)` … `"${F[@]}"`) or a literal inline list. Also
recompute any file list **after** a `git mv` — a list captured beforehand points at paths that no
longer exist, and the later rewrites silently skip those files.

**Residual-check gotcha, learned in P2a:** a `grep -rn OLD … | grep -v NEW` sweep filters on the
whole output line, **including the file path** — and after a rename the path itself contains the
new name, so real hits inside renamed folders are silently swallowed. Use `grep -rohE` (matched
text only) for the residual check, and always exclude `/obj/` and `/bin/`, whose stale generated
sources still carry the old names until `./build.sh Clean`.

Not affected (verified): `src/ClrKernel/kernel-spec/*`, `scripts/install-dev-kernel.sh`,
`scripts/install-local-tool.sh`, and the packages' README/icon asset paths — all keyed to
`src/ClrKernel`, whose name does not change.

---

## 7. Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| R1 | **Selector ordering regression in P3.** The if-chain's order is correctness-bearing; a registry can lose it silently and `#!sql-connect` starts matching `#!sql`. | Explicit ordering in the registry + a unit test per prefix pair (`sql-connect`/`sql`, `dax-connect`/`dax`) asserting the longer selector wins. Write the test **before** the seam. |
| R2 | **Jdbc breaks Linux CI** now that it is in the solution (D6, landed in P2b) — IKVM is Windows-centric by its own csproj comment. | Partly de-risked in P1: `dotnet build src/ClrKernel.Data.Jdbc -c Release` is clean (0 warnings, 0 errors) on macOS/arm64 with SDK 10.0.106, so IKVM at least restores and compiles off-Windows. **ubuntu CI is still unverified.** If it fails there, revert it out of `slnx`, keep the rename, and amend D6 here. |
| R3 | **P4 mixes a move with a behavior refactor** (D7). | **Resolved by splitting the phase** (P4a move / P4b rebase). The original mitigation — "SQL fluent + ETL tests green at each" — was hollow: the fluent tests that cover the rebased code are `Assert.Inconclusive` without `CLRKERNEL_TEST_SQL`. P4b now carries a live-verification gate instead. |
| R4 | **Clean break (D1) strands existing notebooks.** | Accepted. P7 sweeps every in-repo `#r` line; old packages get deprecated on nuget.org by hand, outside this plan. |
| R5 | **Samples are not compiled by CI**, so entry-type renames (D8) can rot silently. | P7 gate runs each sample's first cell; P10 repeats it from a clean tree. |
| R6 | **Half-applied taxonomy on `main`** (D9). | Every phase commit is independently green; no tagging until D12. |
| R7 | **Entra/SSAS/Fabric/Oracle paths have no CI coverage** — they need live services. | `docs/windows-verification-checklist.md` is the gate; P6 and P8 add rows to it rather than claiming verification. |

---

## 8. Open items

- **P8:** where the pipeline/deploy status boards live (see the phase's open design item).
- **P8:** whether Oracle/Odbc/Jdbc ship real table-action implementations or explicit
  `NotSupportedException` stubs in this pass. Decide at the start of the phase and record it here.
- **Nuget deprecation** of the 16 old package IDs is a manual nuget.org action, outside this plan.
