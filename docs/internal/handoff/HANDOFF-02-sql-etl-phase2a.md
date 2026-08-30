# SQL ETL — Phase 2a (bulk copy · MERGE · progress bars)

Builds on the SQL foundation. Adds data-movement to `#!sql` cells, as both **cell
magics** and a **C# API**, sharing the same named connections. All changes are in
your repo **uncommitted** (no commits, per your workflow).

Verified in an equivalent tree: **157 unit tests pass, 3 skipped** (the live
integration tests — see below), `dotnet format` clean, Release build 0 warnings.
I also stood up **SQL Server 2022 in Docker and ran the ETL path for real** — bulk
copy from an in-memory collection, MERGE upsert with introspected columns,
MERGE-with-delete, and the `#!sql-bulk` magic between connections all passed
against a live server. The files on your Mac are byte-identical to what I tested.

## What you get

- **`#!sql-bulk`** — stream a query's rows from one connection into a table on
  another, with a live progress bar. Flags: `--from`/`--to`, `--query` or
  `--from-table`, `--table`, `--truncate`, `--batch-size`, `--timeout`,
  `--keep-identity`, `--keep-nulls`, `--no-lock`, `--no-progress`, `--map src=dest`.
- **`#!sql-merge`** — upsert a source into a target on key columns; columns are
  introspected (identity/computed excluded), `--delete` removes rows missing from
  the source, returns inserted/updated/deleted counts. Generated MERGE is verified
  valid T-SQL with ScriptDom.
- **C# API** (via `Sql` in `#!csharp` cells): `Sql.BulkCopy(conn, table, rows)`
  for any collection — POCOs, anonymous types, dictionaries, scalar arrays
  ("array variables") — or a `DataTable`/`IDataReader` (streaming, no buffering);
  `Sql.Merge(conn, new MergeSpec { ... })`; `Sql.OpenConnection(conn)`.
- **Progress bars** — a reusable `ProgressBar` in ClrKernel.Primitives (usable
  from C# cells too), driving the bulk-copy progress display.
- **Raw connection strings** now work as a real escape hatch: `#!sql-connect
  --name x --connection-string "..."` is used as-is (a new `RawConnectionString`
  auth mode), instead of being overridden by the auth switch.

## New files to stage

- `src/ClrKernel.Primitives/ProgressBar.cs`
- `src/ClrKernel.Sql/Etl/` — `SqlIdentifier`, `DataTableBuilder`,
  `CountingDataReader`, `BulkCopy`, `MergeBuilder`, `SqlEtlDirectives`.
- `src/ClrKernel.Sql/SqlSession.Etl.cs` — the ETL methods on the session (partial).
- `test/ClrKernel.UnitTest/SqlEtlTest.cs` — 21 unit tests.
- `test/ClrKernel.UnitTest/SqlIntegrationTest.cs` — 3 live tests (skipped unless
  `CLRKERNEL_TEST_SQL` is set — see below).
- `samples/SqlEtl.nb.md` — a runnable sample.

## Modified files to stage

- `src/ClrKernel.Sql/SqlAuthMode.cs` — new `RawConnectionString` mode.
- `src/ClrKernel.Sql/SqlConnectionSpec.cs` — raw-mode short-circuit + describe.
- `src/ClrKernel.Sql/SqlDirectives.cs` — default to raw mode for `--connection-string`.
- `src/ClrKernel.Sql/SqlSession.cs` — made `partial`.
- `src/ClrKernel.Core/Extensions.cs` — `Sql` accessor for C# cells.
- `src/ClrKernel.Core/InteractiveScriptEngine.cs` — ClrKernel.Sql reference/imports
  and `#!sql-bulk` / `#!sql-merge` dispatch.
- `README.md` — an ETL subsection.
- A handful of foundation `.cs` files were re-synced only because the formatter had
  converted them to file-scoped namespaces after the last delivery (no logic
  change) — e.g. `SqlSession.cs`, `TrailingExpressionTest.cs`. This keeps the repo
  `dotnet format`-clean.

```bash
git add src test samples/SqlEtl.nb.md README.md
```

## Suggested commit message

```
feat(sql): bulk copy, MERGE upsert, and progress bars (ETL, phase 2a)

Adds data movement to SQL cells as both magics and a C# API sharing the same
connections. #!sql-bulk streams a query into a table with a live progress bar;
#!sql-merge upserts on key columns (introspected, optional delete) and reports
inserted/updated/deleted counts (generated MERGE verified with ScriptDom).
Sql.BulkCopy loads any collection (POCOs, dicts, scalar arrays), DataTable, or
streaming reader; Sql.Merge runs upserts from C#. Adds a reusable ProgressBar
primitive and a RawConnectionString auth mode so --connection-string is honored
as-is. Validated end-to-end against SQL Server 2022.
```

## Notes

- **Run the live tests against your own server**: set `CLRKERNEL_TEST_SQL` to a
  connection string and run `./build.sh Test --filter SqlIntegrationTest` (or
  `dotnet test --filter SqlIntegrationTest`). Without it, those 3 tests report
  inconclusive/skip — so CI stays green.
- **Cleanup**: the transfer staging is at `_to_delete/phase2-delivery/` — delete
  when convenient (the bridge can't remove files).
- **No extension changes** this phase — the magics route through the existing SQL
  cell path and the C# API is auto-imported into C# cells.

## Next (Phase 2b)

Dependency-based parallelization (per-cell `-- step` / `-- needs` annotations →
auto DAG, independent steps run in parallel) and idempotent definition deployment
(`CREATE OR ALTER` from a folder, dependency-ordered). Ready when you are.
