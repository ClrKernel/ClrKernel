# SQL in ClrKernel

ClrKernel runs **SQL cells** against Microsoft SQL Server. In a notebook, set a
cell's language to **SQL** (or start it with the `#!sql` selector); in a plain
`.nb.md` file, use a ` ```sql ` fenced block. You get T-SQL syntax highlighting,
live syntax checking as you type, keyword/function completion, and results
rendered as an interactive grid (sort, a global filter, a per-column filter row,
a per-column value picker, and an Analyze panel) — the same grid C# query
results use.

## Connect

Connections are **named** and **secret-free**. Define them with a
`#!sql-connect` cell (or the connection button next to the cell's language
picker, which guides you and stores the password in your OS credential store —
macOS Keychain, Windows Credential Manager, or Linux libsecret). Passwords are
never written into the notebook.

```sql
#!sql-connect --name analytics --server sql-warehouse --database reports --auth integrated --default
#!sql-connect --name sales --server sql-sales --database dw --auth sql --user reporting
```

`--auth integrated` uses Windows Integrated auth on Windows and Microsoft Entra
(Azure AD) sign-in on macOS/Linux. For a SQL login (`--auth sql`), store the
password once from the connection button, or set the
`CLRKERNEL_SECRET_SQL_<NAME>` environment variable for headless / CI runs.

## Save a connection for next time

By default a connection lives in the running session — great for the current
notebook, but gone when the kernel restarts. After you add one with the
**connection button**, ClrKernel offers to **save it to a `connections.json`**: it
shows a file found up the folder tree (or lets you choose where to create one) and
writes the connection there. Only a secret *reference* is stored — the password
stays in your OS credential store. Saved `SqlServer` entries are loaded
automatically the next time you open a notebook in (or under) that folder, so the
connection is ready without re-adding it. You can also hand-write the file:

```json
{
  "analytics": {
    "$type": "SqlServer",
    "server": "sql-warehouse",
    "database": "reports",
    "auth": "integrated"
  }
}
```

The same `connections.json` feeds the C# `Oracle.FromConfig` / `Odbc.FromConfig`
providers, so one file can hold every kind of connection.

## Use a connection from C#

A `#!sql-connect` connection is also handed to C# cells as a variable. When the
`--name` is a valid C# identifier, ClrKernel binds a variable of that name
automatically, so you can drop straight into the fluent query API:

```csharp
// `analytics` was defined by the #!sql-connect cell above:
analytics.Query("select top 100 * from dbo.Orders order by OrderDate desc").Results()
```

If the name isn't identifier-safe (e.g. `--name my-dw`), add `--var` to pick the
variable name yourself, or `--no-var` to skip the binding entirely:

```sql
#!sql-connect --name my-dw --server sql-warehouse --database reports --var dw
```

Either way `Sql.Database("<name>")` still resolves the connection by name.

## Query

A cell runs against the default connection, or one you name with a leading
`-- connections <name>` comment (still valid T-SQL) — the connection button
writes this for you.

```sql
-- connections analytics
SELECT TOP 100 OrderId, CustomerId, Total, OrderDate
FROM dbo.Orders
WHERE OrderDate >= DATEADD(DAY, -30, SYSUTCDATETIME())
ORDER BY OrderDate DESC;
```

## Multiple connections in one file

Each cell picks its own connection, so a single notebook can read from several
servers:

```sql
-- connections sales
SELECT COUNT(*) AS Orders, SUM(Total) AS Revenue FROM dbo.Orders;
```

Results come back as an interactive grid: click a column header to sort, type in
the top box to filter across all columns, type in a column's own filter box (or
open its ▾ value picker to check specific values) to narrow individual columns —
all filters combine — and open **Analyze** for per-column stats. **Clear** resets
every filter at once.
