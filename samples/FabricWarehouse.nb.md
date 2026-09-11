# Writing to a Microsoft Fabric Warehouse from ClrKernel

The `ClrKernel.Database.Provider.Fabric` helper lets C# cells load data into **Fabric Warehouse**
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

Hand `BulkInsert` a source database and a table name to copy the whole table,
a query for part of one, or any `IDataReader`. With `createIfMissing: true` the
target table is created from the source's schema (UTF-8 `varchar`, `datetime2` —
never `nvarchar`) if it doesn't already exist.

```csharp
var dw = SqlServer.Connection("sql.example.com", "Warehouse");

wh.BulkInsert(dw, "dbo.Orders", createIfMissing: true)   // "12,480 row(s) → dbo.Orders (table created)"
```

```csharp
wh.BulkInsert(dw.Query("select * from dbo.Orders where Year = 2026"), "dbo.Orders2026", createIfMissing: true)
```

Under the hood each bulk-insert: writes the rows to a temporary Parquet file,
uploads it to `Files/Staging-BulkInsert/<guid>.parquet` in the staging lakehouse,
runs `INSERT INTO <table> SELECT * FROM OPENROWSET(BULK '<onelake-url>', FORMAT =
'PARQUET')`, then deletes the staged file.

## Reload a set of tables

`ReloadBatch` clears each target — truncates it, or deletes the segment named by
`segmentFilter` — and loads it again from the source, `MaxDegreeOfParallelism`
tables at a time. The source query defaults to `select *` from the same-named
table on the source database. A table name is one identifier: dots inside it are
part of the name, and a hand-written query brackets it.

```csharp
var results = wh.ReloadBatch([
    new FabricReloadRequest("Mart", "COMPANY.Dimension.Forecast"),
    new FabricReloadRequest("Mart", "COMPANY.Dimension.Instrument",
        sourceQuery: "select * from [Other].[Mart].[COMPANY.Dimension.Instrument]"),
    new FabricReloadRequest("Mart", "FactSales", segmentFilter: "Year = 2026",
        sourceQuery: "select * from Mart.FactSales where Year = 2026"),
], dw, new() { MaxDegreeOfParallelism = 1, CreateTableIfMissing = true });

results   // one row per table: rows deleted / inserted, or the error
```

The same batch under the spelling a `.dib` reload library used — nested names,
named arguments — so those cells paste in unchanged:

```csharp
var batch = Fabric.ReloadBatch.Create([
    new Fabric.ReloadRequest("Mart", "COMPANY.Dimension.Forecast", SourceQuery: """select * from [database].[Mart].[COMPANY.Dimension.Forecast];"""),
    new Fabric.ReloadRequest("Mart", "COMPANY.Dimension.Instrument"),
]);
await batch.Run(dw, wh, new() { MaxDegreeOfParallelism = 1, CreateTableIfMissing = true });
```

Each table runs on its own connections and reports its own outcome, so one
failing table doesn't abort the rest — inspect `Succeeded` / `Error` per row.
Set `DeleteCommand` on a request for full control over what clears the target.
The reader-factory form, `wh.ReloadBatch(requests, req => IDataReader,
maxParallelism)`, is for rows a `DataSource` does not reach.

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
