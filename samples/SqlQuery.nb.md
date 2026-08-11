# Querying SQL from C# cells

ClrKernel's `SqlServer` helper gives C# cells an ergonomic, ad-hoc query API — connect,
query, and get results without registering a named `#!sql-connect` connection
first. Results render as the same interactive grid the `#!sql` cells use, and are
also enumerable as dynamic rows, so one object works both when displayed and in
code.

## Connect

`SqlServer.Connection(server, database)` opens an ad-hoc connection. Auth is Integrated
Security by default (Windows Integrated on Windows, Microsoft Entra "Default" on
macOS/Linux):

```csharp
var dw = SqlServer.Connection("database.example.com", "AdventureWorksDW2025");
```

Other ways to connect:

```csharp
// SQL login — password from the secret store (never inline):
var app = SqlServer.Connection("sql01", "AppDb", "svc_reader", "sql:app-reader");

// Microsoft Entra (Azure AD):
var azure = SqlServer.AzureConnection("myserver.database.windows.net", "Sales");

// Reuse a connection already defined with #!sql-connect:
var reused = SqlServer.Database("analytics");
// (a #!sql-connect --name analytics cell also binds `analytics` for you directly)

// Full connection string (escape hatch):
var raw = SqlServer.ConnectionString("Server=...;Database=...;Trusted_Connection=True;");
```

## Query → grid + rows

`.Query(sql).Results()` runs the query and returns a result that **renders as the
interactive grid** when it's the cell's value:

```csharp
dw.Query("select top 100 * from dbo.Orders order by OrderDate desc").Results()
```

The very same object is enumerable as **dynamic rows** — access columns by name or
index:

```csharp
var orders = dw.Query("select OrderId, Customer, Total from dbo.Orders").Results();
Console.WriteLine($"{orders.Count} orders");
foreach (var o in orders) {
    Console.WriteLine($"{o.OrderId}  {o.Customer}  {o.Total:C}");
}
var firstCustomer = orders[0]["Customer"];
```

## Typed results

`.Results<T>()` maps each row to a record or class (by constructor parameters or
settable properties, matched to column names):

```csharp
record Order(int OrderId, string Customer, decimal Total);

var top = dw.Query("select OrderId, Customer, Total from dbo.Orders order by Total desc")
            .Results<Order>();
var biggest = top[0];               // strongly typed
```

## Parameters, scalars, and commands

Bind parameters with an anonymous object (`@name` → property), and use `Scalar`
and `Execute` for single values and non-queries:

```csharp
var since = dw.Query("select * from dbo.Orders where OrderDate >= @from", new { from = new DateTime(2026, 1, 1) })
              .Results();

var total  = dw.Scalar<decimal>("select sum(Total) from dbo.Orders");
var closed = dw.Execute("update dbo.Orders set Status = 'Closed' where Total = 0");
```

## Tables and bulk copy

`.Table(name)` reads as a source and writes as a bulk-copy target. `createIfMissing`
builds the destination from the source schema; `.Exists()` / `.Count()` inspect it:

```csharp
var staging = dw.Table("stg.Orders");
if (!staging.Exists()) { /* … */ }

// Stream a query straight into a table, creating it if needed:
var copied = dw.Table("stg.Orders")
    .BulkCopyFrom(dw.Query("select * from dbo.Orders"), createIfMissing: true);
copied   // "12,480 rows → stg.Orders (…ms)"
```

You can also bulk-copy an in-memory collection (POCOs, records, anonymous types):

```csharp
var rows = new[] {
    new { Id = 1, Name = "Ann" },
    new { Id = 2, Name = "Ben" },
};
dw.Table("dbo.People").BulkCopyFrom(rows, createIfMissing: true);
```

## Transactions

`.Transaction()` scopes a unit of work; commit to keep it, or let it dispose to
roll back:

```csharp
using (var tx = dw.Transaction()) {
    tx.Execute("insert into dbo.Audit(Note) values ('start')");
    tx.Execute("update dbo.Orders set Status = 'Archived' where Year < 2020");
    tx.Commit();   // omit to roll back
}
```

## Notes

- **Relationship to `#!sql` cells.** This is the C# counterpart to the `#!sql`
  cell language; both render the same interactive grid. Use `#!sql` for SQL-first
  cells, `SqlServer.Connection(...)` when you're already in C# and want rows as objects.
- **Secrets.** Passwords are never inline — a SQL login reads its password from
  the OS secret store under the `secretRef` you pass (or `CLRKERNEL_SECRET_<REF>`
  for headless runs).
- **Streaming.** `.Query(sql).OpenReader()` returns a plain `IDataReader` if you
  want to stream rows yourself (e.g. into another system).
