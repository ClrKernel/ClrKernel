# SQL Phase 2b — pipelines (auto DAG) + definition deployment + autocompletion

Completes the SQL ETL toolkit. Adds dependency-based parallel pipelines, a live
status board, idempotent folder deployment, and context-aware autocompletion for
the whole `#!sql-*` / `-- step` syntax. All changes are in your repo
**uncommitted** (no commits, per your workflow).

Verified: **183 unit tests pass, 5 skipped** (the live tests), `dotnet format`
clean, Release build 0 warnings. I again ran **SQL Server 2022 in Docker** and
validated end-to-end: a 3-step pipeline (two parallel extracts + a dependent
combine) produced the right data, and a folder deploy of a proc + view succeeded
and was **idempotent on re-run**. Files on your Mac are byte-identical to what I
tested.

## What you get

- **Pipelines from cell annotations** — mark a SQL cell `-- step <name>` and
  `-- needs <a, b>`. Running a step cell *registers* it (doesn't execute yet). A
  step body can be plain SQL or a `#!sql-merge` / `#!sql-bulk` magic.
- **`#!sql-run`** — builds the DAG and runs it: independent steps run in
  **parallel** (cap with `--max-parallel`), a failure **skips** everything
  downstream while independent branches finish, and a **live status board** shows
  pending → running → done/failed/skipped. `--select <steps>` runs a subset plus
  its upstream dependencies. Missing deps and cycles are caught before anything
  runs. A failed run throws (non-zero exit for headless jobs).
- **`#!sql-deploy --path <folder>`** — deploys `.sql` definitions idempotently:
  procs/views/functions/triggers are rewritten to `CREATE OR ALTER`, files run in
  filename order, and failures are retried across passes so cross-file
  dependencies resolve without hand-ordering. `--recurse`, `--dry-run`,
  `--no-alter`. Also `Sql.Deploy(conn, new DeployOptions { ... })` from C#.
- **Autocompletion (the "easy for new developers" ask)** — in SQL cells,
  Ctrl+Space completes: `#!sql-*` magic names, each magic's flags, connection
  names after `--from`/`--to`/`--connection`, `--auth` values, the `-- step` /
  `-- needs` / `-- connections` directives, and — for `-- needs` — the names of
  steps declared in **other cells**. Normal T-SQL keyword/function completion
  still applies inside statements.

## New files to stage

- `src/ClrKernel.Sql/Pipeline/` — `PipelineStep`, `Pipeline` (DAG: topo sort,
  cycles, selection), `PipelineRunner` (parallel scheduler), `PipelineBoard`.
- `src/ClrKernel.Sql/Deploy/` — `GoBatchSplitter`, `CreateOrAlter`,
  `DeployRunner`, `DeployBoard`.
- `src/ClrKernel.Sql/SqlOrchestrationDirectives.cs` — `#!sql-run` / `#!sql-deploy`
  parsing.
- `src/ClrKernel.Sql/SqlSession.Orchestration.cs` — step registration, pipeline
  run, deploy (partial of SqlSession).
- `test/ClrKernel.UnitTest/SqlPhase2bTest.cs` — 26 unit tests.
- `samples/SqlPipeline.nb.md` — runnable sample.

## Modified files to stage

- `src/ClrKernel.Sql/SqlDirectives.cs` — `-- step` / `-- needs` parsing on cells.
- `src/ClrKernel.Sql/SqlSession.cs` — a `-- step` cell registers instead of
  running.
- `src/ClrKernel.Sql/SqlLanguage.cs` — context-aware completion (magics, flags,
  connections, directives, step names).
- `src/ClrKernel.Core/InteractiveScriptEngine.cs` — `#!sql-run` / `#!sql-deploy`
  dispatch.
- `src/ClrKernel.Server/Lsp/LspServer.cs` — builds completion context
  (connections + step names from open cells) and maps new completion kinds.
- `test/ClrKernel.UnitTest/SqlIntegrationTest.cs` — 2 new live tests (pipeline
  order, idempotent deploy).
- `README.md` — a pipelines & deployment subsection.

```bash
git add src test samples/SqlPipeline.nb.md README.md
```

## Suggested commit message

```
feat(sql): dependency pipelines, definition deployment, and autocompletion (phase 2b)

Cells annotated with -- step / -- needs form a DAG that #!sql-run executes in
parallel with a live status board — independent steps overlap, failures skip
downstream, cycles/missing deps are caught up front. #!sql-deploy applies a
folder of .sql definitions idempotently (CREATE OR ALTER, multi-pass to resolve
cross-file order). SQL cells gain context-aware completion for the #!sql-*
magics, their flags, connection names, and the -- step/-- needs/-- connections
directives (with cross-cell step-name completion). Validated end-to-end against
SQL Server 2022.
```

## Notes

- **Model**: a `-- step` cell *registers* (doesn't run) — run the step cells,
  then `#!sql-run` (or run headlessly: cells register top-to-bottom, a trailing
  `#!sql-run` executes the DAG). To test one step in isolation use
  `#!sql-run --select <step>`.
- **Deploy idempotency** covers programmable objects (proc/view/function/
  trigger). Tables aren't `CREATE OR ALTER`-able — guard those with your own
  `IF NOT EXISTS` / `IF OBJECT_ID(...) IS NULL`.
- **Live tests**: `CLRKERNEL_TEST_SQL=<conn> ./build.sh Test --filter SqlIntegrationTest`.
- **Cleanup**: staging is at `_to_delete/phase2b-delivery/` — delete when convenient.
- **No extension changes** — magics and completions flow through the existing SQL
  cell / LSP paths.

That's the full original wishlist delivered: array/table inputs, secrets, multi-
connection files, bulk copy, MERGE, dependency parallelization, progress bars, and
definition deployment. A natural future add is schema-aware completion (table/
column names from a live connection) — say the word if you want it.
