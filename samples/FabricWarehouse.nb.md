# Writing to a Microsoft Fabric Warehouse from ClrKernel

The `ClrKernel.Fabric` helper lets C# cells load data into **Fabric Warehouse**
tables. It bulk-inserts an `IDataReader` by staging Parquet to a lakehouse in
OneLake and loading it with `OPENROWSET` — the fast path for large loads — and it
can create the target table from the reader's schema using Fabric-supported types.
It's available in every C# cell as `Fabric`.

Authentication is Microsoft Entra (Azure AD) only; no passwords are handled here.
Fabric execution needs a live tenant, so run these cells against your own
workspace.

## Connect

```csharp
// Interactive / default Entra sign-in (az login, VS credential, managed identity,
// then an interactive browser fallback):
var fabric = Fabric.Connect();

// Or a service principal:
// var fabric = Fabric.ClientSecret(tenantId, clientId, clientSecret);
```

Resolve a warehouse, and point it at a lakehouse in the same workspace to use as
the Parquet staging area:

```csharp
var wh = fabric
    .Workspace("Analytics")
    .Warehouse("SalesDW")
    .WithStaging("Lakehouse_Staging");
```

## Query the warehouse

`Query` runs T-SQL and returns an interactive grid; `Execute` runs a non-query.

```csharp
wh.Query("SELECT TOP 100 * FROM dbo.FactSales ORDER BY OrderDate DESC")
```

## Bulk-insert from a SQL Server source

Open a reader against any source (here a SQL Server connection defined with
`#!sql-connect`) and hand it to `BulkInsert`. With `createIfMissing: true` the
target table is created from the reader's schema (UTF-8 `varchar`, `datetime2` —
never `nvarchar`) if it doesn't already exist.

```csharp
using var conn = Sql.OpenConnection("analytics");
using var cmd = new Microsoft.Data.SqlClient.SqlCommand("SELECT * FROM dbo.Orders", conn);
using var reader = cmd.ExecuteReader();

var result = wh.BulkInsert(reader, "dbo.Orders", createIfMissing: true);
result   // "12,480 row(s) → dbo.Orders (table created)"
```

Under the hood each bulk-insert: writes the rows to a temporary Parquet file,
uploads it to `Files/Staging-BulkInsert/<guid>.parquet` in the staging lakehouse,
runs `INSERT INTO <table> SELECT * FROM OPENROWSET(BULK '<onelake-url>', FORMAT =
'PARQUET')`, then deletes the staged file.

## Reload a batch of segments in parallel

`ReloadBatch` deletes a segment of each table and reloads it, running up to
`maxParallelism` tables concurrently. You supply a factory that returns a fresh
`IDataReader` for each request's source query. `SegmentFilter` builds the
`DELETE ... WHERE ...`; set `DeleteCommand` for full control, or leave both unset
for an append-only reload.

```csharp
using System.Data;

var requests = new[] {
    new FabricReloadRequest { TableName = "FactSales",   SegmentFilter = "Year = 2026" },
    new FabricReloadRequest { TableName = "FactReturns", SegmentFilter = "Year = 2026" },
};

var results = wh.ReloadBatch(
    requests,
    req => {
        var c = Sql.OpenConnection("analytics");
        var q = new Microsoft.Data.SqlClient.SqlCommand(
            $"SELECT * FROM {req.TableName} WHERE {req.SegmentFilter}", c);
        return q.ExecuteReader(CommandBehavior.CloseConnection);
    },
    maxParallelism: 4);

results.DisplayTable();   // one row per segment: rows deleted / inserted, or the error
```

Each segment runs on its own connection and reports its own outcome, so one
failing table doesn't abort the rest — inspect `Succeeded` / `Error` per row.

## Notes

- **Staging lakehouse.** Bulk-insert needs a lakehouse in the same workspace to
  stage Parquet. Set it once with `.WithStaging("<lakehouse>")`, or per call with
  `BulkInsert(reader, table, stagingLakehouse: "<lakehouse>")`.
- **Types.** Fabric Warehouse doesn't support `nvarchar` or `datetime`; generated
  tables use UTF-8 `varchar` and `datetime2(3)`. To adapt an existing SQL Server
  `CREATE TABLE`, run it through `WarehouseTableDefinition.ToFabricTypes(ddl)`.
- **Headless.** These cells run unchanged under `jupyter nbconvert --execute` /
  papermill; use a service-principal credential (`Fabric.ClientSecret(...)`) for
  unattended runs.
