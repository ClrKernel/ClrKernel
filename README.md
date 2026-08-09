[![CI](https://github.com/ClrKernel/ClrKernel/actions/workflows/ci.yml/badge.svg)](https://github.com/ClrKernel/ClrKernel/actions/workflows/ci.yml)
[![Release](https://github.com/ClrKernel/ClrKernel/actions/workflows/release.yml/badge.svg)](https://github.com/ClrKernel/ClrKernel/actions/workflows/release.yml)
# ClrKernel

A Jupyter kernel for .NET. C# cells are evaluated with Roslyn's scripting engine
([Microsoft.CodeAnalysis.CSharp.Scripting](https://www.nuget.org/packages/Microsoft.CodeAnalysis.CSharp.Scripting)),
with more CLR languages (PowerShell, F#) on the roadmap. Notebooks run
interactively in JupyterLab / VS Code (Jupyter extension) and headlessly via
`nbconvert` or `papermill` — including from schedulers like SQL Server Agent.

ClrKernel is a maintained fork of
[SciSharp/ICSharpCore](https://github.com/SciSharp/ICSharpCore), created after
Microsoft deprecated .NET Interactive / Polyglot Notebooks (April 2026).
Relative to upstream it adds: correct headless execution under
nbclient/papermill, full output capture for `async`/`await` cells, control
channel + heartbeat + graceful `shutdown_request` handling, patched vulnerable
dependencies, and the kernelspec shipped inside the NuGet package.

## Install

```bash
dotnet tool install --global ClrKernel
jupyter kernelspec install "$(clrkernel --kernel-spec-path)" --user --name clrkernel
jupyter kernelspec list   # should show: clrkernel
```

Requires a .NET 8+ runtime (`RollForward=Major`: newer majors work) and Jupyter.

## Use

Pick the **ClrKernel (C#)** kernel in JupyterLab or VS Code. Cells support
`#r "nuget: Package, Version"` and `#r "path/to/local.dll"` references, with
REPL-style state persisting across cells.

### Importing shared libraries

`#!import` loads C# code from another file into the session — use it to share
helper libraries between notebooks, no .NET Interactive required:

```csharp
#!import "../lib/jobbooks.dib"
```

Supports `.dib` (C# sections run; markdown and other-language sections are
skipped), `.ipynb` (code cells run), and `.csx`/`.cs` (whole file). Relative
paths resolve against the notebook's directory, and nested `#!import`s inside
a library resolve relative to that library's own file. Each resolved file runs
once per session — re-importing is a no-op unless you pass `--force`
(`#!import --force "lib.dib"`), which is handy while iterating on the library
itself. Imported files can use `#r` directives, including `#r "nuget: ..."`,
and can `#!import` further files.

### SQL cells

Set a cell's language to **SQL** (or start it with `#!sql`) to run T-SQL against
Microsoft SQL Server. You get T-SQL highlighting, live syntax checking,
keyword/function completion, and results as the same interactive grid (sort,
filter, Analyze) that C# query results use.

Connections are **named** and **secret-free** — passwords never go in the
notebook. Define them with `#!sql-connect`, or use the connection button next to
the cell's language picker, which prompts for credentials and stores the
password in your OS credential store (macOS Keychain, Windows Credential Manager,
Linux libsecret):

```sql
#!sql-connect --name analytics --server sql-warehouse --database reports --auth integrated --default
```

`--auth integrated` is Windows Integrated auth on Windows and Microsoft Entra
(Azure AD) sign-in on macOS/Linux; `--auth sql --user <u>` is a SQL login whose
password comes from the secret store (or the `CLRKERNEL_SECRET_SQL_<NAME>` env
var for headless runs). A cell targets the default connection, or one named with
a leading `-- connections <name>` comment. Multiple connections can be used
across cells in one notebook. See [samples/Sql.nb.md](samples/Sql.nb.md).

#### Querying from C#

C# cells get an ergonomic query API on `Sql` — no `#!sql-connect` needed for
ad-hoc work. `Sql.Connection(server, database)` opens a connection (Integrated
Security by default), and `.Query(sql).Results()` returns rows that **render as
the interactive grid and are enumerable as dynamic rows** in the same object:

```csharp
var dw = Sql.Connection("dw.db.local", "datawarehouse");

var orders = dw.Query("select * from dbo.Orders").Results();  // grid when shown…
foreach (var o in orders) Console.WriteLine($"{o.OrderId}: {o.Total}");  // …rows in code
```

`.Results<T>()` maps rows to a record or class; `.Query(sql, new { id })` binds
parameters; `.Scalar<T>(sql)` and `.Execute(sql)` cover single values and
non-queries. A `.Table(name)` reads as a source and writes as a bulk-copy target
(`createIfMissing` builds it from the source schema), and `.Transaction()` scopes
a unit of work:

```csharp
var recent = dw.Query("select * from dbo.Orders where Year = @y", new { y = 2026 }).Results<Order>();
dw.Table("stg.Orders").BulkCopyFrom(dw.Query("select * from dbo.Orders"), createIfMissing: true);
record Order(int OrderId, string Customer, decimal Total);
```

For a SQL login use `Sql.Connection(server, db, user, "sql:secretRef")` (password
from the secret store); `Sql.AzureConnection(...)` for Entra, or
`Sql.Database("analytics")` to reuse a registered `#!sql-connect` connection. See
[samples/SqlQuery.nb.md](samples/SqlQuery.nb.md).


Headless / scheduled execution:

```bash
jupyter nbconvert --to notebook --execute --output out.ipynb etl.ipynb
papermill etl.ipynb runs/etl_out.ipynb -k clrkernel --language .net-csharp -p run_date 2026-08-04
```

A failing cell exits non-zero (job schedulers see the failure); papermill also
persists the partially-executed output notebook as a diagnostic artifact.

## Build & test

A cross-platform task runner (built on [Nuke](https://nuke.build)) drives build,
test, format, and the VS Code extension. It needs only the .NET SDK — no extra
tools to install. Use `./build.sh` on macOS/Linux, `.\build.ps1` (or
`build.cmd`) on Windows.

```bash
./build.sh --help                        # list all targets and flags
./build.sh                               # default: restore + build + test the solution
./build.sh Build                         # build the whole solution
./build.sh Test                          # run all unit tests
./build.sh Build --project ClrKernel.Http   # build one project (searches src/ then test/)
./build.sh Test  --filter Mermaid           # run a subset of tests (dotnet test --filter)
./build.sh Format                        # verify formatting  (Format --apply to fix)
./build.sh Extension                     # build the VS Code extension (npm install + tsc)
./build.sh All                           # solution build + test AND the extension
./build.sh Clean                         # delete bin/obj and the extension's out/
./build.sh --configuration Debug Build   # any target accepts --configuration
```

Targets chain their dependencies automatically (e.g. `Test` builds first), so a
bare `./build.sh` restores, builds, and tests in one go.

## Develop

```bash
./scripts/install-dev-kernel.sh    # kernel 'clrkernel-dev' running from bin/ output
                                   # iterate: dotnet build + restart kernel
./scripts/install-local-tool.sh    # pack + install the global tool from a local
                                   # feed; tests the full packaged experience
clrkernel --kernel-spec-details    # show which kernelspec the binary resolves
```

## License

Apache 2.0, preserving the upstream license. Original work © SciSharp
(Kerry Jiang, Haiping Chen, and contributors); fork maintained by
[ClrKernel](https://github.com/ClrKernel).
