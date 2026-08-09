# SQL in ClrKernel

ClrKernel runs **SQL cells** against Microsoft SQL Server. In a notebook, set a
cell's language to **SQL** (or start it with the `#!sql` selector); in a plain
`.nb.md` file, use a ` ```sql ` fenced block. You get T-SQL syntax highlighting,
live syntax checking as you type, keyword/function completion, and results
rendered as an interactive grid (sort, filter, and an Analyze panel) — the same
grid C# query results use.

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

Results come back as an interactive grid: click a column header to sort, use the
filter row to narrow rows, and open **Analyze** for per-column stats.
