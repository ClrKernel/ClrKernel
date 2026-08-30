# ClrKernel.AnalysisServices — SSAS / Fabric helper for C# cells

A new package that ports your `Integrator.Databases.AnalysisServices` capabilities
into a clean, self-contained ClrKernel helper. From any C# cell, `Ssas` connects
to a Tabular model and queries (DAX/DMV), reads metadata, processes (refreshes),
and manages partitions. All changes are in your repo **uncommitted** (no commits,
per your workflow).

Verified: **193 unit tests pass, 5 skipped**, `dotnet format` clean, Release build
0 warnings, and a C#-cell test confirms `Ssas` resolves and runs inside a cell.
The TOM (AMO) + ADOMD .NET Core client libraries restore and build here. I could
**not** run against a live SSAS instance in the sandbox (those libraries need a
server, and on-prem Integrated auth/TOM processing generally need Windows), so the
execution paths are validated by your server — the pure logic (connection strings,
partition specs, refresh mapping, DMV mapper, factory) is unit-tested. Files on
your Mac are byte-identical to what I tested.

## What you get (`Ssas` in every C# cell)

- **Connect** — `Ssas.Connect(server, db)` (Windows Integrated by default),
  `Ssas.Connect(server, db, user, password)`, `Ssas.FromConnectionString(cs)`,
  `Ssas.ConnectAzureAnalysisServices(server, db, credential?)`, and
  `Ssas.ConnectFabric(workspace, model, credential?)` (Entra auth via Azure.Identity;
  `powerbi://` XMLA endpoint).
- **Query** — `cube.Query(dax)` → interactive grid; `cube.QueryRows(dax)` → rows as
  dictionaries for further C#.
- **Metadata** — `cube.Tables()` / `cube.Partitions()` → typed lists (name, row
  counts, refresh time, last error) from the TMSCHEMA DMVs; `.DisplayTable()` for a grid.
- **Process** — `cube.Recalculate()`, `cube.ProcessModel(SsasRefresh.Full)`,
  `cube.ProcessTables("A","B")`, `cube.ProcessPartitions(new[]{ ("Sales","2026") }, SsasRefresh.Full, maxParallelism: 8)`
  — with your query-override logic ported for partition sources.
- **Partition management** — `cube.EnsurePartition(table, name, dataSource, query)` /
  `EnsurePartitions(...)` and `RemovePartition(...)` / `RemovePartitions(...)`, ported
  from your library (idempotent add/update, optional recalc).

Design choices you approved: a clean ClrKernel-native API (no Integrator.Common
dependency), Integrated Security default, full capability set + Fabric.

## New files to stage

- `src/ClrKernel.AnalysisServices/` — `ClrKernel.AnalysisServices.csproj`, `Ssas.cs`
  (factory + refresh enum), `SsasConnectionSpec.cs` (auth + connection-string builder),
  `SsasConnection.cs` (query + metadata), `SsasConnection.Processing.cs` (TOM
  processing + partitioning), `SsasMetadata.cs` (DMV records + reflective mapper +
  partition types).
- `test/ClrKernel.UnitTest/SsasTest.cs` — 10 unit tests.
- `samples/AnalysisServices.nb.md` — usage sample.

## Modified files to stage

- `ClrKernel.slnx` — adds the project.
- `src/ClrKernel.Core/ClrKernel.Core.csproj` — references the package.
- `src/ClrKernel.Core/InteractiveScriptEngine.cs` — imports `ClrKernel.AnalysisServices`
  and adds its assembly so `Ssas` is available in C# cells.
- `test/ClrKernel.UnitTest/ClrKernel.UnitTest.csproj` — references the package.
- `README.md` — an Analysis Services subsection.

```bash
git add src/ClrKernel.AnalysisServices ClrKernel.slnx README.md \
        src/ClrKernel.Core/ClrKernel.Core.csproj \
        src/ClrKernel.Core/InteractiveScriptEngine.cs \
        test/ClrKernel.UnitTest
```

## Suggested commit message

```
feat(ssas): Analysis Services helper for C# cells (SSAS / Azure AS / Fabric)

Adds ClrKernel.AnalysisServices: from C# cells, Ssas connects to a Tabular model
and queries with DAX (interactive grid), reads table/partition metadata from the
TMSCHEMA DMVs, processes the model (recalculate, process model/tables/partitions
with query overrides and parallelism), and adds/removes partitions idempotently.
Supports on-prem SSAS (Windows Integrated), Azure Analysis Services, and Fabric /
Power BI semantic models (Entra auth). Ported from Integrator.Databases into a
clean, self-contained API. Built on AMO/TOM + ADOMD.NET.
```

## Notes & differences from your original

- **Two NuGet deps** (restored on build): `Microsoft.AnalysisServices.NetCore.retail.amd64`
  and `Microsoft.AnalysisServices.AdomdClient.NetCore.retail.amd64` (19.84.1), plus
  `Azure.Identity` (for Fabric/Azure AS tokens).
- **No Integrator.Common**: I re-implemented the small pieces you needed (connection,
  DMV mapping, partition types) rather than pull that library in — so your existing
  `.dib` code needs light edits to the new `Ssas.*` API (the shapes are close:
  `AnalysisServices.Connection(s,d).ProcessPartitions(...)` → `Ssas.Connect(s,d).ProcessPartitions(...)`).
- **AAD tokens**: ADOMD in this version has no `AccessToken` property, so for
  Fabric/Azure AS the Entra token is passed as the connection-string password on the
  query side; TOM uses `Server.AccessToken`. Validate the Fabric path against a real
  workspace when you can.
- **Secrets**: user/password is passed directly. If you want passwords from the OS
  credential store, resolve one in a C# cell and pass it —
  `Ssas.Connect(s, d, user, Sql.Secrets.Resolve("ssas:prod"))`.
- **Cleanup**: staging is at `_to_delete/` — delete when convenient.
- Please **validate the process/partition paths against your SSAS server** — those
  couldn't run in the sandbox.

A natural follow-up, if useful: `#!dax` cell language (so a cell can be pure DAX
against a default cube, like `#!sql`), and/or wiring SSAS connections into the same
named-connection UI the SQL side uses.
