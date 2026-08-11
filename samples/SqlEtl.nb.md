# SQL ETL in ClrKernel — bulk copy & MERGE

Beyond querying, ClrKernel can move and upsert data. Two ways: **cell magics**
(quick, declarative) and a **C# API** (composable, for custom logic). Both use
the same named, secret-free connections as `#!sql` cells.

## Connect

```sql
#!sql-connect --name analytics --server sql-analytics --database reports --auth integrated --default
#!sql-connect --name warehouse --server sql-warehouse --database dw --auth sql --user etl
```

## Bulk copy (magic)

Copy a query's rows from one connection into a table on another. A live progress
bar streams during the load.

```sql
#!sql-bulk --from analytics --query "SELECT * FROM dbo.Orders WHERE OrderDate >= '2026-01-01'" --to warehouse --table stg.Orders --truncate --batch-size 10000
```

Flags: `--from`/`--to` (connections), `--query` or `--from-table`, `--table`
(destination), `--truncate`, `--create`, `--batch-size`, `--timeout`,
`--keep-identity`, `--keep-nulls`, `--no-lock`, `--no-progress`, `--map src=dest`.
`--create` builds the destination table from the source schema when it doesn't
already exist — handy for staging tables you don't want to define by hand.

## Upsert with MERGE (magic)

Merge a staging table into a target on key columns. Columns are introspected
(identity/computed excluded); add `--delete` to remove target rows missing from
the source. Returns inserted / updated / deleted counts.

```sql
#!sql-merge --connection warehouse --target dbo.Customers --source stg.Customers --on Id
```

## Bulk copy from in-memory data (C# API)

Any collection — POCOs, anonymous types, dictionaries, or scalar arrays — can be
bulk-loaded. `SqlServer` is the session's connection/ETL handle in C# cells.

```csharp
var rows = Enumerable.Range(1, 50_000).Select(i => new {
    Id = i,
    Name = $"item-{i}",
    Amount = i * 1.5m,
});

var result = SqlServer.BulkCopy("warehouse", "dbo.Items", rows,
    new BulkCopyOptions { BatchSize = 10_000, TruncateFirst = true });

result   // "50,000 rows → dbo.Items (… ms)"
```

## Programmatic MERGE (C# API)

```csharp
var merge = SqlServer.Merge("warehouse", new MergeSpec {
    Target = "dbo.Customers",
    Source = "stg.Customers",
    KeyColumns = new[] { "Id" },
    DeleteNotMatchedBySource = true,
});

merge   // "inserted 120, updated 4,300, deleted 12 (… ms)"
```

## Streaming copy between servers (C# API)

Read a reader from one connection and stream it straight into another — no
buffering, exact row count reported:

```csharp
using var src = SqlServer.OpenConnection("analytics");
using var cmd = src.CreateCommand();
cmd.CommandText = "SELECT * FROM dbo.BigTable";
using var reader = cmd.ExecuteReader();

SqlServer.BulkCopy("warehouse", "dbo.BigTable", reader);
```
