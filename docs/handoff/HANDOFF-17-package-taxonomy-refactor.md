# PLAN — Package taxonomy refactor (Core / Language / Database / DataEngineering)

> **This is a forward-looking plan, not a record of completed work.** It is written to be
> stopped and resumed. Before doing anything, read **§0 Resume protocol** and the
> **§1 Progress board** — the board is the single source of truth for where the work is.

Restructures the 17 projects into a 21-project taxonomy with three tiers — `ClrKernel.Core.*`
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
| P2b | Rename `Language.*` (Http/Mermaid/PowerShell) + `Database*` providers; Jdbc into the solution | TODO |
| P3 | Cell-language registration seam in `Core.Scripting` | TODO |
| P4 | Split `ClrKernel.Sql` → `Language.Sql` + `Database.Provider.SqlServer` (incl. fluent dedup) | TODO |
| P5 | Split `ClrKernel.AnalysisServices` → `Language.Dax` + `Database.Provider.AnalysisServices` | TODO |
| P6 | Extract shared Entra auth into `ClrKernel.Database` | TODO |
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
| D5 | **Fabric split by workload, shared Entra auth in `ClrKernel.Database`.** `Provider.AnalysisServices` keeps semantic-model work (incl. `ConnectFabric`); `Provider.Fabric` keeps warehouse/OneLake work; both consume shared auth. | New auth abstraction in `ClrKernel.Database` (P6). |
| D6 | **Jdbc is renamed *and* added to `ClrKernel.slnx`.** | It compiles in CI for the first time. IKVM restore/build on ubuntu is **unverified** — see risk R2. |
| D7 | **Collapse the fluent duplication during the split.** `SqlDatabase`/`SqlQuery`/`SqlTable` rebase onto `ClrKernel.Database`'s `Database`/`DatabaseQuery`/`DatabaseTable` while they move. | P4 mixes a move with a behavior-bearing refactor. Mitigation: sub-commits, and the SQL fluent tests run at each. |
| D8 | **Entry types renamed:** `Sql` → `SqlServer`, `Ssas` → `AnalysisServices`. `Fabric`, `Oracle`, `Odbc`, `Jdbc` already match and stay. | Every sample cell body, README snippet, and the C# variable binding emitted by `#!sql-connect` changes. |
| D9 | **Phased commits on `main`.** No AI/assistant attribution in commit messages. `CLAUDE.md` is gitignored. | Done: `.gitignore` entry added. |
| D10 | **Flat layout** — `src/<PackageName>/<PackageName>.csproj`. | Keeps `Build.cs`'s `ResolveProject` and `./build.sh --project <name>` working unchanged. |
| D11 | **Test project split three ways** — `Core`, `Language`, `Database`. | `Build.cs` gains a test-project list; `InternalsVisibleTo` entries multiply. |
| D12 | **Version bump is the user's call**, done manually once the refactor is tested and working. | `Directory.Build.props` stays `0.8.0` through every phase. No tag, no release, mid-refactor. `release.yml` hard-fails if a tag doesn't match `<Version>`. |

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

**Check before starting P4:** `SqlSession.Etl.cs`'s `OpenConnection` returns a
`Microsoft.Data.SqlClient.SqlConnection`, which would give `Language.Sql` a direct SqlClient
dependency. Decide whether the facade can return the shared `Database`/`DbConnection` type
instead. If it can't, record here that `Language.Sql` **intentionally** references SqlClient, so
nobody later files it as a layering bug.

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

**Verify the rule mechanically — across all four `Core.*` consumers, not just the engine.** The
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

**Gate:** P2b gate + every magic exercised end-to-end (`#!http`, `#!mermaid`, `#!pwsh`,
`#!sql-connect`, `#!sql`, `#!sql-bulk`, `#!sql-merge`, `#!sql-run`, `#!sql-deploy`, `#!dax-connect`,
`#!dax`) + `python3 test/tools/lsp_harness.py <dll>` and `jupyter_completion_test.py` pass +
headless smoke (`jupyter nbconvert` over `test/notebooks/smoke.ipynb`, and `smoke-fail.ipynb` must
still exit non-zero).

### P4 — Split `ClrKernel.Sql` (+ fluent dedup)

The largest phase. Land it as sub-commits, each building:

1. Create `Database.Provider.SqlServer`; move the provider-half files (§4.3) with namespaces
   updated. `Language.Sql` (the renamed remainder) references it.
2. Rebase `SqlDatabase`/`SqlQuery`/`SqlTable` onto `ClrKernel.Database`'s
   `Database`/`DatabaseQuery`/`DatabaseTable` (D7). Keep the SqlClient-specific surface —
   `SqlConnection` typing, bulk copy, the connection registry — as SQL-Server additions on top of
   the shared base. `FluentSqlTest` and `MultiProviderTest` must both stay green; this is the one
   place in the plan where behavior can actually change.
3. Re-point the P3 registration at `Language.Sql` — a registration edit and a composition-root
   edit, **not** a change to the contract. If the contract has to change here, stop and record why.
4. Update `release.yml` (one entry becomes two) and `slnx`.

**Gate:** P3 gate + `FluentSqlTest`, `SqlTest`, `SqlEtlTest`, `SqlPhase2bTest`, `MultiProviderTest`
all green, and `./build.sh Test` count matches baseline.

### P5 — Split `ClrKernel.AnalysisServices`

Per §4.4. `Language.Dax` references `Database.Provider.AnalysisServices`, and re-points the P3
registration the same way P4 did.

**Gate:** P4 gate + `DaxTest`, `SsasTest` green.

### P6 — Shared Entra auth into `ClrKernel.Database`

Both `Provider.AnalysisServices` (`ConnectFabric`, Azure AS) and `Provider.Fabric` (warehouse,
OneLake) do Entra sign-in via `Azure.Identity`. Extract the common credential/token acquisition
into `ClrKernel.Database`; both providers consume it (D5).

Keep `ConnectFabric` in `Provider.AnalysisServices` — it is an auth/endpoint variant of the same
AMO/ADOMD code path, not a warehouse operation.

**Gate:** P5 gate. Entra paths need a live tenant, so runtime verification is deferred to
`docs/windows-verification-checklist.md` — add rows for both providers there in this phase.

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
- [ ] `.github/workflows/release.yml` — the hand-written `dotnet pack` list (16 → 21 entries)
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
      `"ClrKernel.Sql"`, `"ClrKernel.Sql.Etl"`, `"ClrKernel.AnalysisServices"`,
      `"ClrKernel.Fabric"`, `"ClrKernel.Mermaid"`). A stale one **builds clean** and fails only
      when a cell runs. P2a hit this. `grep -rn '"ClrKernel\.' --include="*.cs" src` after every
      rename, and rely on `./build.sh Test` (the scripting tests execute real cells) to catch it.

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
| R2 | **Jdbc breaks Linux CI** once it enters the solution (D6) — IKVM is Windows-centric by its own csproj comment. | Partly de-risked in P1: `dotnet build src/ClrKernel.Data.Jdbc -c Release` is clean (0 warnings, 0 errors) on macOS/arm64 with SDK 10.0.106, so IKVM at least restores and compiles off-Windows. **ubuntu CI is still unverified.** If it fails there, revert it out of `slnx`, keep the rename, and amend D6 here. |
| R3 | **P4 mixes a move with a behavior refactor** (D7). | Sub-commits; SQL fluent + ETL tests green at each; no other work in the phase. |
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
