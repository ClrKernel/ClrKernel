# Querying other databases (Oracle, ODBC, JDBC)

ClrKernel's fluent query API works the same across database engines. The SQL Server
support ships in the kernel (`Sql.Connection(...)`); other engines come as opt-in
provider packages that return the **same** `Database` type — so
`Query(sql).Results()` (interactive grid + dynamic rows), typed `.Results<T>()`,
`.Table()`, and `.Transaction()` behave identically everywhere.

Load a provider per notebook with `#r "nuget: …"`; its driver isn't pulled unless
you use it. Passwords are never inline — a secret reference resolves from ClrKernel's
secret store (OS credential manager, or the `CLRKERNEL_SECRET_<REF>` env var for
headless runs).

## Oracle

```csharp
#r "nuget: ClrKernel.Database.Provider.Oracle"
using ClrKernel.Database.Provider.Oracle;
```

```csharp
// Password for 'scott' resolves from the secret store under "oracle:erp".
var erp = Oracle.Connect("orahost", 1521, "ORCL", "scott", "oracle:erp");

erp.Query("select empno, ename, sal from emp order by sal desc").Results()
```

```csharp
record Employee(decimal EmpNo, string EName, decimal Sal);
var top = erp.Query("select * from emp").Results<Employee>();
```

## ODBC (any ODBC data source)

The ODBC driver for your database must be installed on the machine.

```csharp
#r "nuget: ClrKernel.Database.Provider.Odbc"
using ClrKernel.Database.Provider.Odbc;
```

```csharp
var pg = Odbc.FromConnectionString("Driver={PostgreSQL Unicode};Server=host;Database=app;Uid=app;");
pg.Query("select * from public.orders limit 100").Results()
```

```csharp
// Or a DSN with a secret-store password:
var db = Odbc.FromDsn("MyWarehouse", user: "svc", secretRef: "odbc:warehouse");
```

## JDBC / OpenEdge (experimental)

`ClrKernel.Database.Provider.Jdbc` runs Java JDBC drivers inside .NET via IKVM. It's experimental
and currently Windows-centric; you supply the JDBC driver assembly. The JDBC bridge
doesn't support command parameters — use parameter-less SQL.

```csharp
#r "nuget: ClrKernel.Database.Provider.Jdbc"
using ClrKernel.Database.Provider.Jdbc;
```

```csharp
var oe = OpenEdge.Connect("host", "sports2000", "user", "openedge:app",
    driverAssemblyPath: "OpenEdge.JdbcDriver.dll");
oe.Query("select CustNum, Name from public.Customer").Results()
```

```csharp
// Any JDBC driver:
var db = Jdbc.Connect("jdbc:postgresql://host:5432/app", "org.postgresql.Driver",
    driverAssemblyPath: "postgresql.dll", user: "app", secretRef: "jdbc:app");
```

## Config-file connections

Keep connection settings out of the notebook in a `connections.json` (or
`clrkernel.connections.json`), searched from the working directory up the folder
tree. `$type` selects the provider; a `{ "secret": "<ref>" }` value resolves from the
secret store; a node value of `"inherit"` continues the search in the next file up.

```json
{
  "erp":       { "$type": "Oracle", "server": "orahost", "port": 1521,
                 "serviceName": "ORCL", "userId": "scott",
                 "password": { "secret": "oracle:erp" } },
  "warehouse": { "$type": "Odbc",
                 "connectionString": "Driver={PostgreSQL Unicode};Server=h;Database=dw;",
                 "password": { "secret": "odbc:warehouse" } }
}
```

```csharp
var erp = Oracle.FromConfig("erp");
var wh  = Odbc.FromConfig("warehouse");
erp.Query("select * from emp").Results()
```

## Notes

- **Same everywhere.** Anything you learned from the SQL Server
  [SqlQuery sample](SqlQuery.nb.md) — grid + dynamic rows, `.Results<T>()`,
  parameters, `.Table()`, `.Transaction()` — applies to every provider (SQL Server
  adds bulk copy on top). Parameters work on Oracle and ODBC; not on the JDBC bridge.
- **Secrets.** No passwords in notebooks: pass a `secretRef` (resolved from the OS
  store / `CLRKERNEL_SECRET_<REF>`), or a DSN/connection string that carries its own
  credentials.
- **Provider-agnostic core.** All of this is `ClrKernel.Database` under the hood; you can
  build a `Database` over any ADO.NET `DbConnection` factory directly:
  `new Database("name", () => new SomeDbConnection(cs))`.
