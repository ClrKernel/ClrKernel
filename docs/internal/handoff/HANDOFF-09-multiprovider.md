# Multi-provider data access: Oracle, ODBC, JDBC + config-file connections

Brings the multi-provider database support from your lib-notebooks
`Integrator.Databases` into ClrKernel as native packages, plus JSON config-file
connections. The fluent query API you already have (`Query(sql).Results()` → grid +
dynamic rows, `.Results<T>()`, `.Table()`, `.Transaction()`) now works across engines.

All changes are in your repo **uncommitted** (no commits, per your workflow).

Verified: **243 unit tests pass, 8 skipped** (the skipped are the gated live-SQL
tests). The provider-agnostic core is exercised **end-to-end over a real ADO.NET
provider (SQLite in-memory)** — insert / query / grid+dynamic rows / typed mapping /
parameters / transactions. Config loader, secret resolution, and provider factories
are unit-tested. `dotnet format` clean (core + providers + JDBC), full-solution
Release build 0 errors, all publishable packages pack.

## Architecture — a shared core (this is the big structural change)

To avoid duplicating result-mapping and the secret store across providers, I
extracted a provider-agnostic core, **`ClrKernel.Data`**, and refactored
`ClrKernel.Sql` to sit on it. This moved code **out of `ClrKernel.Sql`** — see
"Files removed" below.

- **`ClrKernel.Data`** (new core; depends only on ClrKernel.Primitives):
  - `Database` / `DatabaseQuery` / `DatabaseTable` / `DatabaseTransaction` — the
    fluent API over any ADO.NET `DbConnection` (built from a `Func<DbConnection>`).
  - `DataResults` (was `SqlResults`), `ObjectMapper`, `ValueConverter`,
    `ParameterBinder` — moved here from ClrKernel.Sql (now shared).
  - `Secrets/*` — the OS-credential secret store, moved here from
    `ClrKernel.Sql.Secrets` → `ClrKernel.Data.Secrets` (secrets aren't SQL-specific,
    and providers need them without depending on ClrKernel.Sql).
  - `Config/ConnectionConfig` — the config-file loader.
- **`ClrKernel.Sql`** now references `ClrKernel.Data`; `SqlDatabase`/`SqlQuery`/
  `SqlTable` keep the SQL-Server extras (SqlBulkCopy, `createIfMissing`) and return
  the shared `DataResults`. **No behaviour change** to the SQL fluent API.

Because `ClrKernel.Core` → `ClrKernel.Sql` → `ClrKernel.Data`, the core is bundled in
the kernel, so `Database` and the config loader are always available. The provider
packages below are **opt-in** (`#r "nuget: …"`) so their drivers aren't pulled in
unless used.

## The providers

- **`ClrKernel.Data.Oracle`** (built + tested) — `Oracle.Connect(host, port, service, user, secretRef)`, `Oracle.FromConnectionString(...)`, `Oracle.FromConfig(name)`. Built on `Oracle.ManagedDataAccess.Core`.
- **`ClrKernel.Data.Odbc`** (built + tested) — `Odbc.FromConnectionString(cs)`, `Odbc.FromDsn(dsn, user, secretRef)`, `Odbc.Connect(driver, props, …)`, `Odbc.FromConfig(name)`. Built on `System.Data.Odbc` (the ODBC driver for your DB must be installed).
- **`ClrKernel.Data.Jdbc`** (experimental — see below) — `Jdbc.Connect(url, driverClass, driverAssemblyPath, …)`, `Jdbc.ConnectJar(...)`, and an `OpenEdge.Connect(server, database, user, secretRef, driverAssemblyPath)` helper. Ported faithfully from your lib (IKVM-based).

Usage (README "Other databases" section + `samples/MultiProvider.nb.md`):

```csharp
#r "nuget: ClrKernel.Data.Oracle"
using ClrKernel.Data.Oracle;
var erp = Oracle.Connect("orahost", 1521, "ORCL", "scott", "oracle:erp");
erp.Query("select * from emp").Results()
```

Config-file connections (`connections.json` / `clrkernel.connections.json`, searched
up the folder tree; `$type` selects the provider; `{ "secret": "<ref>" }` resolves
from the OS store / `CLRKERNEL_SECRET_<REF>`; a node value `"inherit"` chains files):

```csharp
var erp = Oracle.FromConfig("erp");
```

## ⚠️ JDBC / OpenEdge is EXPERIMENTAL and out of the default build

It ports your IKVM bridge faithfully and — pleasant surprise — **compiles cleanly on
Linux here**. But it is runtime-**unverified**: it needs a real JDBC driver assembly,
a live server, and `IkvmConfiguration` still resolves `IKVM.Home` from a **win-x64**
NuGet path (effectively Windows-only as written). Also, the JDBC bridge **doesn't
implement command parameters** — use parameter-less SQL (`db.Query(sql)`); the
Oracle/ODBC providers support parameters normally.

So `ClrKernel.Data.Jdbc` is intentionally **not in `ClrKernel.slnx` and not in the
release pack** — it can't break CI, and won't be auto-published unverified. It builds
standalone (`dotnet build src/ClrKernel.Data.Jdbc`). To ship it once you've validated
on Windows with your DataDirect driver: add it to `ClrKernel.slnx` and add a
`dotnet pack` line in `.github/workflows/release.yml`.

## Files removed from ClrKernel.Sql (already moved on your disk)

I moved these out of `src/ClrKernel.Sql/` into `_to_delete/multiprovider-removed/`
(the bridge can't delete). **When you stage, make sure these are removed from
`ClrKernel.Sql`** (a `git add -A` / `git rm` will record the deletions):

```
src/ClrKernel.Sql/Secrets/                     (whole folder → ClrKernel.Data/Secrets)
src/ClrKernel.Sql/Fluent/SqlResults.cs         (→ ClrKernel.Data/Fluent/DataResults.cs)
src/ClrKernel.Sql/Fluent/ObjectMapper.cs       (→ ClrKernel.Data/Fluent/)
src/ClrKernel.Sql/Fluent/ValueConverter.cs     (→ ClrKernel.Data/Fluent/)
src/ClrKernel.Sql/Fluent/ParameterBinder.cs    (→ ClrKernel.Data/Fluent/)
```

## New files to stage

```
src/ClrKernel.Data/**            (core: Fluent/, Secrets/, Config/, csproj)
src/ClrKernel.Data.Oracle/**     (Oracle.cs, csproj)
src/ClrKernel.Data.Odbc/**       (Odbc.cs, csproj)
src/ClrKernel.Data.Jdbc/**       (JDBC bridge + Jdbc.cs + OpenEdge.cs, csproj) — experimental
test/ClrKernel.UnitTest/MultiProviderTest.cs
samples/MultiProvider.nb.md
```

## Modified files to stage

- `src/ClrKernel.Sql/ClrKernel.Sql.csproj` — references ClrKernel.Data.
- `src/ClrKernel.Sql/Fluent/{SqlQuery,SqlTable,SqlDatabase}.cs` — use core `DataResults` / `ObjectMapper` / `ValueConverter`.
- `src/ClrKernel.Sql/{SqlConnectionSpec,SqlSession,SqlSession.Etl}.cs` — `ClrKernel.Sql.Secrets` → `ClrKernel.Data.Secrets`.
- `ClrKernel.slnx` — adds ClrKernel.Data, .Oracle, .Odbc (NOT .Jdbc).
- `test/ClrKernel.UnitTest/ClrKernel.UnitTest.csproj` — refs Data/Oracle/Odbc + Microsoft.Data.Sqlite (test-only).
- `test/ClrKernel.UnitTest/{FluentSqlTest,SqlTest}.cs` — updated namespaces / `DataResults`.
- `.github/workflows/release.yml` — packs ClrKernel.Data, .Oracle, .Odbc (Core's nuspec now depends on ClrKernel.Data, so it must publish).
- `README.md` — "Other databases (Oracle, ODBC, JDBC)" section.

## Stage & commit

```bash
# stage new + modified, and record the moved-out deletions
git add -A src/ClrKernel.Data src/ClrKernel.Data.Oracle src/ClrKernel.Data.Odbc \
           src/ClrKernel.Data.Jdbc src/ClrKernel.Sql ClrKernel.slnx \
           test/ClrKernel.UnitTest samples/MultiProvider.nb.md \
           README.md .github/workflows/release.yml
git status   # confirm the 5 removed ClrKernel.Sql paths show as deleted
```

Suggested commit message:

```
feat(data): provider-agnostic core + Oracle/ODBC/JDBC providers + config connections

Extracts ClrKernel.Data (fluent Database over any ADO.NET DbConnection, plus the
secret store, result grid, and object mapper shared with ClrKernel.Sql) and adds
opt-in providers: ClrKernel.Data.Oracle and ClrKernel.Data.Odbc (built + tested via
SQLite/offline), and experimental ClrKernel.Data.Jdbc (IKVM bridge + OpenEdge helper,
Windows-centric, kept out of the default build). Adds JSON connections.json config
loading with secret-store passwords. Refactors ClrKernel.Sql onto the core with no
API change.
```

## Notes

- **No VS Code extension change** — all C# APIs, no new cell language.
- **Live Oracle/ODBC** weren't run here (no Oracle server / ODBC driver in the sandbox); the shared query path is validated live-equivalent via SQLite and, previously, live SQL Server. Please smoke-test Oracle/ODBC against your own servers.
- **Cleanup**: transfer scratch + the removed ClrKernel.Sql files are under `_to_delete/` (`multiprovider-scratch` and `multiprovider-removed`) — delete when convenient.
- **Not brought over** (say the word and I'll add): the encrypted-in-file secret scheme (Inferno/DPAPI) — config passwords use the OS secret store / env vars instead, per your choice.
