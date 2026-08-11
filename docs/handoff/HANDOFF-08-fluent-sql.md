# Fluent C# SQL query API (`Sql.Connection(...).Query(...).Results()`)

A ClrKernel-native version of the ergonomic connect→query→results API from your
lib-notebooks `Integrator.Databases`, added to **ClrKernel.Sql**. From a C# cell:

```csharp
var dw = Sql.Connection("database.example.com", "AdventureWorksDW2025");
var orders = dw.Query("select * from dbo.Orders").Results();   // grid when shown, rows in code
```

It replaces the verbose ADO.NET dance (`OpenConnection` → `SqlCommand` →
`ExecuteReader`) with one line, while reusing the existing connection-string,
secret-store, and bulk-copy machinery so it's consistent with `#!sql` cells.

All changes are in your repo **uncommitted** (no commits, per your workflow).

Verified: **239 unit tests pass, 0 skipped** — including **live SQL Server**
integration tests (query/grid/typed/params, table bulk-copy with create-if-missing,
transaction commit/rollback) run against a real SQL Server 2022 in Docker.
`dotnet format --verify-no-changes` clean, full-solution Release build 0 errors,
ClrKernel.Sql packs.

## The API (per your choices: `Sql.Connection(...)`, grid+rows, Core + Table/BulkCopy)

**Connect** (ad-hoc — no `#!sql-connect` needed):

```csharp
Sql.Connection("srv", "db")                      // Integrated Security (Entra Default off Windows)
Sql.Connection("srv", "db", "user", "sql:ref")   // SQL login; password from the secret store
Sql.AzureConnection("srv.database.windows.net","Sales")   // Microsoft Entra
Sql.Database("analytics")                          // reuse a registered #!sql-connect connection
Sql.ConnectionString("Server=...;Database=...")    // raw connection string
```

**Query → `.Results()`** returns one object that is *both*:
- an **interactive grid** when it's the cell value (it's a `DisplayData`, same grid as `#!sql`), and
- **enumerable as dynamic rows** — `foreach (var r in results) … r.OrderId …`, `results[0]["Customer"]`, `results.Count`.

**Typed** — `.Results<T>()` maps rows to a record (constructor params) or class
(settable properties), matched to column names. `.Results()` also has `.As<T>()`.

**Parameters / scalars / commands**:

```csharp
dw.Query("… where Id >= @min", new { min = 2 }).Results();
dw.Scalar<decimal>("select sum(Total) from dbo.Orders");
dw.Execute("update dbo.Orders set Status='Closed' where Total = 0");
```

**Tables + bulk copy** (`.Table(name)` is source and target):

```csharp
dw.Table("stg.Orders").Exists();  dw.Table("stg.Orders").Count();
dw.Table("stg.Orders").BulkCopyFrom(dw.Query("select * from dbo.Orders"), createIfMissing: true);
dw.Table("dbo.People").BulkCopyFrom(rows /* POCOs/records/anon */, createIfMissing: true);
```

**Transactions** — `using (var tx = dw.Transaction()) { tx.Execute(...); tx.Commit(); }`
(dispose without commit rolls back).

Sample notebook: `samples/SqlQuery.nb.md`. README gains a "Querying from C#"
subsection under SQL cells.

## New files to stage

```
src/ClrKernel.Sql/Fluent/SqlDatabase.cs             # Sql.Connection handle + SqlDatabaseTransaction
src/ClrKernel.Sql/Fluent/SqlQuery.cs                # lazy query: Results / Results<T> / OpenReader
src/ClrKernel.Sql/Fluent/SqlResults.cs              # DisplayData grid + IEnumerable<dynamic> rows
src/ClrKernel.Sql/Fluent/SqlTable.cs                # Exists/Count/Query + BulkCopyFrom(createIfMissing)
src/ClrKernel.Sql/Fluent/SqlSession.Fluent.cs       # Sql.Connection/AzureConnection/Database/ConnectionString
src/ClrKernel.Sql/Fluent/ValueConverter.cs          # DB value -> CLR type coercion
src/ClrKernel.Sql/Fluent/ParameterBinder.cs         # anon-object / dictionary -> @params
src/ClrKernel.Sql/Fluent/ObjectMapper.cs            # rows -> record/class/scalar
src/ClrKernel.Sql/Fluent/SqlServerTableDefinition.cs# CREATE TABLE from schema (createIfMissing)
test/ClrKernel.UnitTest/FluentSqlTest.cs            # 15 offline + 3 live (gated) tests
samples/SqlQuery.nb.md
```

## Modified files to stage

- `src/ClrKernel.Sql/ClrKernel.Sql.csproj` — adds `InternalsVisibleTo ClrKernel.UnitTest`
  (so the tests can cover the internal mapper/converter/binder). No new package refs.
- `README.md` — "Querying from C#" subsection.

No wiring changes were needed elsewhere: `Sql` is already exposed in C# cells and
`ClrKernel.Sql` is already imported, so the new types resolve with no edits to
Core, the solution file, or the extension.

## Running the live tests yourself

The 3 integration tests skip unless `CLRKERNEL_TEST_SQL` holds a connection string
(same gate as the existing `SqlIntegrationTest`). Point it at any SQL Server:

```bash
export CLRKERNEL_TEST_SQL="Server=localhost,1433;Database=master;User ID=sa;Password=…;TrustServerCertificate=True;Encrypt=False"
dotnet test test/ClrKernel.UnitTest -c Release --filter FullyQualifiedName~FluentSql
```

## Stage & commit

```bash
git add src/ClrKernel.Sql/Fluent \
        src/ClrKernel.Sql/ClrKernel.Sql.csproj \
        test/ClrKernel.UnitTest/FluentSqlTest.cs \
        samples/SqlQuery.nb.md \
        README.md
```

Suggested commit message:

```
feat(sql): fluent C# query API (Sql.Connection → Query → Results)

Adds an ergonomic ad-hoc query API on the Sql helper: Sql.Connection(server, db)
returns a SqlDatabase with .Query(sql).Results()/.Results<T>(), .Scalar<T>(),
.Execute(), .Table(name) (source + bulk-copy target with createIfMissing), and
.Transaction(). Results render as the interactive grid AND enumerate as dynamic
rows from one object. Reuses the existing connection-string/secret-store and
BulkCopyRunner; no inline passwords. Verified against a live SQL Server.
```

## Notes

- **No VS Code extension change** — this is a C# API, not a new cell language.
- **Design choice**: I built a focused native layer rather than vendoring the whole
  `Integrator.Databases` framework (IDataSource, DisposableScope, Configuration,
  SecretConverter, JDBC/Oracle/OpenEdge providers, etc.), which is a large surface
  coupled to `Integrator.Common`. If you specifically want the multi-provider
  (Oracle/JDBC/OpenEdge) or the `Configuration.Load` config-file connections
  brought over too, that's a follow-up — tell me which providers matter.
- **Cleanup**: transfer scratch is under `_to_delete/fluent-sql-scratch` — delete
  when convenient (the bridge can't remove files for you).
