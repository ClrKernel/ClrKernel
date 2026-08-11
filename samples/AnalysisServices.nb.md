# Analysis Services (SSAS / Fabric) in ClrKernel

The `ClrKernel.Database.Provider.AnalysisServices` helper lets C# cells work with Tabular models —
on-premises SQL Server Analysis Services, Azure Analysis Services, or Microsoft
Fabric / Power BI semantic models. Query with DAX, read metadata, and process
(refresh) the model. It's available in every C# cell as `AnalysisServices`.

## Connect

```csharp
// On-prem SSAS with Windows Integrated auth (the default):
var cube = AnalysisServices.Connect("DataWarehouseServer01.yourdomain.local", "AdventureWorksDW2025");

// SSAS with a username/password. Read the password from the secret store rather
// than typing it into the notebook — this resolves from the OS credential manager
// (or CLRKERNEL_SECRET_SSAS_REPORTING) and returns null if it isn't set yet.
new ClrKernel.Core.Secrets.SecretStore().TryResolve("ssas:reporting", out var password);
var cube2 = AnalysisServices.Connect("DataWarehouseServer01.yourdomain.local", "AdventureWorksDW2025", "svc_reporting", password);

// A Microsoft Fabric / Power BI semantic model (Entra auth via az login / managed identity):
var model = AnalysisServices.ConnectFabric("Analytics Workspace", "Sales Model");

// Azure Analysis Services, or a fully custom connection string:
var aas  = AnalysisServices.ConnectAzureAnalysisServices("asazure://westus.asazure.windows.net/myserver", "Model");
var raw  = AnalysisServices.FromConnectionString("Provider=MSOLAP;Data Source=...;Catalog=...;");
```

## Query with DAX

`Query` returns an interactive grid (sort / filter / analyze); `QueryRows`
returns the rows as objects for further C# processing.

```csharp
cube.Query("EVALUATE TOPN(100, 'Sales', 'Sales'[Amount], DESC)")
```

```csharp
var rows = cube.QueryRows("EVALUATE ROW(\"Total\", SUM('Sales'[Amount]))");
var total = rows[0]["[Total]"];
```

## Metadata

Read table and partition metadata (names, row counts, refresh times, last error)
straight from the model DMVs:

```csharp
cube.Tables().DisplayTable()
```

```csharp
cube.Partitions().DisplayTable()
```

## Process the model

Refresh the whole model, specific tables, or specific partitions. Row counts and
parallelism are handled through the Tabular Object Model.

```csharp
// Recalculate (relationships, calculated columns/tables):
cube.Recalculate();

// Process specific tables:
cube.ProcessTables("Sales", "Customers");

// Process a full refresh of the whole model:
cube.ProcessModel(SsasRefresh.Full);

// Process specific partitions (table, partition) — e.g. just the current year:
cube.ProcessPartitions(new[] {
    ("Sales", "2026"),
    ("Sales", "2025"),
}, SsasRefresh.Full, maxParallelism: 8);
```

## Manage partitions

Add or update a partition's query, or remove one — idempotently.

```csharp
cube.EnsurePartition(
    tableName: "Sales",
    partitionName: "2026",
    dataSourceName: "AdventureWorksDW2025",
    query: "SELECT * FROM fact.Sales WHERE Year = 2026");

cube.RemovePartition("Sales", "2019");
```

## A typical cube-processing job

```csharp
// 1) stage keys in SQL, 2) ensure this year's partition exists, 3) process it.
var cube = AnalysisServices.Connect("DataWarehouseServer01.yourdomain.local", "AdventureWorksDW2025");
cube.EnsurePartition("Sales", "2026", "AdventureWorksDW2025", "SELECT * FROM fact.Sales WHERE Year = 2026");
cube.ProcessPartitions(new[] { ("Sales", "2026") });
cube.Recalculate();
```

> On-prem SSAS with Integrated auth and TOM processing generally run on Windows
> (e.g. under SQL Server Agent). Fabric / Azure AS use Entra tokens and work
> cross-platform.
