# SQL smoke test — AdventureWorksDW2025

A runnable, top-to-bottom check of ClrKernel's SQL functionality against
Microsoft's sample warehouse
[AdventureWorksDW2025](https://learn.microsoft.com/sql/samples/adventureworks-install-configure)
(any AdventureWorksDW20xx restore works — the tables used here exist in all of
them). **Edit the `#!sql-connect` cell below** (server / auth) — or use the
connection button next to the cell language picker — then Run All. Every cell
after the connect line should succeed; scratch tables are prefixed
`ClrKernelSmoke_` and dropped by the last cell.

```sql
#!sql-connect --name advdw --server localhost --database AdventureWorksDW2025 --auth integrated --default
```

`--auth integrated` is Windows Integrated auth on Windows and Microsoft Entra
"Default" on macOS/Linux. For a SQL login use
`--auth sql --user <name>` and store the password once via the connection button
(or `CLRKERNEL_SECRET_SQL_ADVDW` for headless runs). The `--name advdw` also
binds a C# variable `advdw`, which the C# cells below use.

**Already have `advdw` in a `connections.json`?** Then don't restate it — a
name-only connect cell *references* the saved definition instead of defining a
new one, and still binds the `advdw` variable and sets the default. Swap the
cell above for:

```sql
#!sql-connect --name advdw --default
```

## SQL cells → interactive grid

Customers joined to geography — sort the columns, try the per-column filters and
the Analyze panel:

```sql
SELECT TOP (200)
    c.CustomerKey, c.FirstName, c.LastName, c.EmailAddress,
    c.YearlyIncome, g.City, g.EnglishCountryRegionName AS Country
FROM dbo.DimCustomer c
JOIN dbo.DimGeography g ON g.GeographyKey = c.GeographyKey
ORDER BY c.CustomerKey;
```

Internet sales by calendar year (the summary badge under the grid shows the
connection name, result-set count, and timing):

```sql
SELECT d.CalendarYear,
       COUNT(DISTINCT f.SalesOrderNumber) AS Orders,
       SUM(f.SalesAmount)                 AS Revenue
FROM dbo.FactInternetSales f
JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey
GROUP BY d.CalendarYear
ORDER BY d.CalendarYear;
```

## Query from C# → the same grid

```csharp
advdw.Query(@"
    SELECT TOP (10)
        p.EnglishProductName    AS Product,
        SUM(f.SalesAmount)      AS Revenue,
        SUM(f.OrderQuantity)    AS Units
    FROM dbo.FactInternetSales f
    JOIN dbo.DimProduct p ON p.ProductKey = f.ProductKey
    GROUP BY p.EnglishProductName
    ORDER BY Revenue DESC").Results()
```

The same object enumerates as **dynamic rows** — columns by name or index:

```csharp
var byYear = advdw.Query(@"
    SELECT d.CalendarYear, COUNT(DISTINCT f.SalesOrderNumber) AS Orders, SUM(f.SalesAmount) AS Revenue
    FROM dbo.FactInternetSales f
    JOIN dbo.DimDate d ON d.DateKey = f.OrderDateKey
    GROUP BY d.CalendarYear ORDER BY d.CalendarYear").Results();

Console.WriteLine($"{byYear.Count} calendar years");
foreach (var y in byYear) {
    Console.WriteLine($"{y.CalendarYear}  {y.Orders,6:N0} orders  {y.Revenue,16:C0}");
}
byYear[0]["CalendarYear"]
```

## Typed results

```csharp
record Product(string EnglishProductName, string Color, decimal? ListPrice);

var priciest = advdw.Query(@"
    SELECT TOP (5) EnglishProductName, Color, ListPrice
    FROM dbo.DimProduct WHERE ListPrice IS NOT NULL
    ORDER BY ListPrice DESC").Results<Product>();
priciest[0]
```

## Parameters and scalars

Date-agnostic on purpose: whatever vintage your restore is, this derives its
window from the data:

```csharp
var latest = advdw.Scalar<DateTime>("SELECT MAX(OrderDate) FROM dbo.FactInternetSales");
var from   = latest.AddMonths(-6);

var recentOrders = advdw.Scalar<int>(
    "SELECT COUNT(DISTINCT SalesOrderNumber) FROM dbo.FactInternetSales WHERE OrderDate >= @from",
    new { from });
var totalRevenue = advdw.Scalar<decimal>("SELECT SUM(SalesAmount) FROM dbo.FactInternetSales");

new { LatestOrder = latest, LastSixMonthsOrders = recentOrders, TotalRevenue = totalRevenue }
```

## Display() and trailing values render identically

`Display()` mid-cell and the bare trailing value take the same formatting path
— the two outputs below should look the same (and no handle is printed after):

```csharp
var summary = new { Database = "AdventureWorksDW2025", Years = byYear.Count, Revenue = totalRevenue };
summary.Display();
summary
```

## Bulk copy — query → table

Streams a query into a scratch table, creating it from the source schema
(`--truncate` semantics via options make this cell re-runnable):

```csharp
var copied = advdw.Table("dbo.ClrKernelSmoke_TopCustomers")
    .BulkCopyFrom(
        advdw.Query(@"SELECT TOP (1000) CustomerKey, FirstName, LastName, EmailAddress, YearlyIncome
                      FROM dbo.DimCustomer ORDER BY CustomerKey"),
        new BulkCopyOptions { TruncateFirst = true },
        createIfMissing: true);
copied
```

## Bulk copy — in-memory rows → table

```csharp
var people = new[] {
    new { Id = 1, Name = "Ann",  Region = "Europe"  },
    new { Id = 2, Name = "Ben",  Region = "Pacific" },
};
advdw.Table("dbo.ClrKernelSmoke_People")
     .BulkCopyFrom(people, new BulkCopyOptions { TruncateFirst = true }, createIfMissing: true)
```

## MERGE from C#

Stage some changes (one update, one new row), then merge on the key — the
result shows per-action counts (expect Inserted 1, Updated 1):

```csharp
var changes = new[] {
    new { Id = 1, Name = "Ann (updated)", Region = "Europe"        },
    new { Id = 3, Name = "Cid (new)",     Region = "North America" },
};
advdw.Table("dbo.ClrKernelSmoke_PeopleStaging")
     .BulkCopyFrom(changes, new BulkCopyOptions { TruncateFirst = true }, createIfMissing: true);

SqlServer.Merge("advdw", new MergeSpec {
    Target     = "dbo.ClrKernelSmoke_People",
    Source     = "dbo.ClrKernelSmoke_PeopleStaging",
    KeyColumns = { "Id" },
})
```

## The `#!sql-bulk` and `#!sql-merge` magics

The same operations as cell magics (re-running the merge is idempotent — expect
Updated 2, Inserted 0 this time):

```sql
#!sql-bulk --from advdw --query "SELECT TOP (500) ProductKey, EnglishProductName, Color, ListPrice FROM dbo.DimProduct" --to advdw --table dbo.ClrKernelSmoke_Products --create --truncate
```

```sql
#!sql-merge --connection advdw --target dbo.ClrKernelSmoke_People --source dbo.ClrKernelSmoke_PeopleStaging --on Id
```

Verify the merged rows (Ann updated, Ben untouched, Cid inserted):

```sql
SELECT * FROM dbo.ClrKernelSmoke_People ORDER BY Id;
```

## Transactions

No `Commit()` → the insert rolls back when the transaction disposes:

```csharp
var before = advdw.Scalar<int>("SELECT COUNT(*) FROM dbo.ClrKernelSmoke_People");
using (var tx = advdw.Transaction()) {
    tx.Execute("INSERT INTO dbo.ClrKernelSmoke_People (Id, Name, Region) VALUES (99, 'Rolled Back', 'X')");
    // tx.Commit();   // deliberately omitted
}
var after = advdw.Scalar<int>("SELECT COUNT(*) FROM dbo.ClrKernelSmoke_People");
new { Before = before, After = after, RolledBack = before == after }
```

## Cleanup

```csharp
var countSmoke = "SELECT COUNT(*) FROM sys.tables WHERE name LIKE 'ClrKernelSmoke[_]%'";
var before = advdw.Scalar<int>(countSmoke);
advdw.Execute(@"
    DROP TABLE IF EXISTS dbo.ClrKernelSmoke_TopCustomers;
    DROP TABLE IF EXISTS dbo.ClrKernelSmoke_People;
    DROP TABLE IF EXISTS dbo.ClrKernelSmoke_PeopleStaging;
    DROP TABLE IF EXISTS dbo.ClrKernelSmoke_Products;");
var after = advdw.Scalar<int>(countSmoke);
$"dropped {before - after} smoke table(s), {after} remaining"
```

## Other ways to connect (reference, not run)

```csharp
// SQL login — password from the secret store (never inline):
// var app = SqlServer.Connection("sql01", "AppDb", "svc_reader", "sql:app-reader");

// Microsoft Entra (Azure AD):
// var azure = SqlServer.AzureConnection("myserver.database.windows.net", "Sales");

// Full connection string (escape hatch):
// var raw = SqlServer.ConnectionString("Server=...;Database=...;Trusted_Connection=True;");

// Reuse any #!sql-connect connection by name:
// var reused = SqlServer.Database("advdw");
```
