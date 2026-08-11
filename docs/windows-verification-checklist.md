# ClrKernel — Windows verification checklist

Fresh-machine acceptance test for the **published** release: VS Code extension
**0.4.0** (marketplace) + the **ClrKernel 0.8.0** global dotnet tool. Work top to
bottom; each item is *do this → expect this*. Tags:

- **⊞** = Windows-specific behavior you couldn't verify on macOS — pay extra attention.
- **(SQL)** **(Oracle/ODBC)** **(Fabric)** **(SSAS)** = needs that backend.
- Optional items are marked *(optional)*.

Record P/F and notes in the sign-off table at the end.

---

## 1. Pre-flight — clean environment

- [ ] Machine is genuinely "different" — ClrKernel not previously installed. Confirm `clrkernel` is **not** already on PATH (`where clrkernel` returns nothing).
- [ ] .NET runtime **8.0+** installed: `dotnet --info` lists a `Microsoft.NETCore.App 8.x` (or newer major) runtime.
- [ ] VS Code installed and up to date.
- [ ] **⊞** Global-tools folder is on PATH: `%USERPROFILE%\.dotnet\tools`. If the tool installs but `clrkernel` isn't found, this is why — reopen the terminal / sign out-in after install.
- [ ] Network reachability to each backend from this machine: SQL Server host/port (1433), Oracle host/port (1521), your ODBC target, `*.fabric.microsoft.com` / Power BI, and the SSAS instance. Windows Firewall not blocking outbound.

## 2. Install the kernel (ClrKernel 0.8.0)

- [ ] `dotnet tool install --global ClrKernel` completes without error.
- [ ] `clrkernel --version` (or `--kernel-spec-details`) reports **0.8.0**.
- [ ] `clrkernel lsp` starts and waits on stdio (Ctrl+C to exit) — this is what the extension launches.
- [ ] *(optional, Jupyter path)* `jupyter kernelspec install "$(clrkernel --kernel-spec-path)" --user --name clrkernel` then `jupyter kernelspec list` shows `clrkernel`.

## 3. Install the VS Code extension (0.4.0)

- [ ] Install **ClrKernel Notebooks** from the Marketplace; Extensions pane shows version **0.4.0**.
- [ ] Marketplace/readme page renders and the **grid screenshots load** (they're hosted from `raw.githubusercontent.com/.../main/docs/images/...`, so this also confirms the images pushed correctly).
- [ ] Extension **Changelog** tab shows the consolidated **0.4.0** entry (SQL, DAX, Fabric, Oracle/ODBC, HTTP/Mermaid/PowerShell) — and **not** a stray 0.6.x section.
- [ ] Keyword search on the Marketplace for "SQL notebook" / "DAX" surfaces the extension *(optional, confirms the new keywords)*.

## 4. First notebook — C# core

- [ ] Command Palette → **ClrKernel: New Markdown Notebook** creates an untitled notebook; a `.nb.md` file also opens as a notebook automatically.
- [ ] First run: if the tool were missing the extension offers to install it — since §2 is done, it should just connect. Controller shows **ClrKernel C#**.
- [ ] Run `Console.WriteLine("hello");` → output appears.
- [ ] REPL state: set `var x = 41;` in one cell, `x + 1` in the next → `42`.
- [ ] IntelliSense: type `Console.` → completion list; hover a method → signature; open paren → signature help. Completions include a variable you defined in a prior cell.
- [ ] **⊞ No duplicate completions** (this is the machine with C# Dev Kit): trigger completion (e.g. `"x".` ) → each member appears **once**, not twice. The cell's language shows as **ClrKernel C#**, and C# highlighting still works.
- [ ] **⊞ No false syntax squiggles**: a cell ending in a bare trailing expression (`var x = 10;` then a second cell with just `x`) shows **no red squiggle** — script-mode trailing expressions are valid and C# Dev Kit no longer flags them.
- [ ] `#r "nuget: Newtonsoft.Json"` then use `JsonConvert` → package resolves and runs.
- [ ] Streaming: a loop with `Console.WriteLine` + `Thread.Sleep` streams line-by-line, not all at the end.
- [ ] **Run All** on a notebook where cell 2 throws → execution **stops** at cell 2 (default). Set `clrkernel.stopOnCellError` to `false` → Run All now continues past the failure. Reset to true.

## 5. SQL cells + interactive grid — **(SQL)**

Connection UI:

- [ ] Set a cell's language to **SQL** (shows **MS SQL**). The **Select connection** button appears next to it (bottom-right of the cell).
- [ ] Button → **Add connection…**; walk the prompts (name, server, database, auth, encryption).
- [ ] **⊞** Choose **Integrated (Windows)** auth against SQL Server → query runs with **no password** stored. This is the key Windows-only path.
- [ ] Add a second connection using **SQL login** → run a query. Then confirm the password landed in **⊞ Windows Credential Manager** (Control Panel → Credential Manager → Windows Credentials; look for a `ClrKernel`/secret entry) and **is not** anywhere in the `.nb.md` file.
- [ ] **Encryption** step: for a local/self-signed server, pick **"Encrypt, trust the server certificate"** → connects. (Without it you'd get a certificate-chain error — worth seeing once by picking validate-certificate against the self-signed server and confirming the clear error message.)
- [ ] Connection dropdown lists **Edit connection…** → edit an existing one and re-run.
- [ ] **Save to connections.json**: after adding, the prompt offers to save. With no file nearby it lets you **choose a location**; pick one → a `connections.json` is written with `"$type": "SqlServer"` and the password as a `{ "secret": … }` **reference only** (open the file and confirm no plaintext password).
- [ ] Add a second connection → the save prompt now shows the **found** file and its existing connection names; save into it → the new entry is **merged** (the first entry is preserved).
- [ ] **Auto-load next session**: close and reopen VS Code, open a notebook in that folder, and run a `#!sql` cell whose `-- connections <name>` points at a saved connection **without** re-adding it → it resolves. (Also confirm the saved names appear in the connection dropdown.)

Query + grid:

- [ ] Run `SELECT TOP 200 * FROM <a wide table>;` → results render in the interactive grid; count label shows rows.
- [ ] **Sort**: click a header → ascending arrow; click again → descending.
- [ ] **Global filter**: type in the top box → rows narrow across all columns; count updates.
- [ ] **Per-column filter row**: type in one column's box → only that column filters; focus stays in the box as you type.
- [ ] **Value picker**: click a column's ▾ → dropdown of distinct values with a search box and **Select all / Clear**; uncheck some values → grid filters; `(null)` appears first if the column has nulls.
- [ ] Filters **combine**: a column text filter + a value picker on another column + the global box all apply together (AND).
- [ ] **Clear** button resets every filter at once.
- [ ] **Analyze** → per-column stats panel; stats reflect the currently-filtered rows.
- [ ] Multi-result: run a batch returning two result sets → two grids.

Directives & errors:

- [ ] Two `#!sql-connect` lines with one `--default`; a cell with `-- connections <name>` targets the named one; a cell with no comment uses the default.
- [ ] **Variable binding**: `#!sql-connect --name analytics ...` then in a **C#** cell `analytics.Query("select 1 as n").Results()` works (auto-bound variable).
- [ ] `--var dw` binds `dw`; `--name my-dw` (not identifier-safe) with no `--var` binds nothing but `SqlServer.Database("my-dw")` still resolves; `--no-var` skips binding.
- [ ] Bad SQL (`SELCT 1`) → **live syntax check** underlines it; running a runtime error (e.g. `SELECT 1/0`) surfaces the **server message with number/severity/line**, not a bare .NET stack trace.

## 6. Query databases from C# (Sql API) — **(SQL)**

- [ ] `SqlServer.Connection("<server>","<db>")` (Integrated) → `.Query("select ...").Results()` renders the grid.
- [ ] Same result object enumerates: `foreach (var r in results) ...` and `results[0]["Col"]` work.
- [ ] `.Results<T>()` maps to a `record`; a typed property reads correctly.
- [ ] Parameters: `.Query("... where X = @x", new { x = ... })` binds.
- [ ] `.Scalar<T>("select count(*) ...")` and `.Execute("update ...")` return the expected count.
- [ ] `.Table("stg.X").BulkCopyFrom(query, createIfMissing: true)` creates + loads; re-run is idempotent as expected.
- [ ] `.Transaction()` — commit path persists; dispose-without-commit rolls back.

### 6a. P4b gate — the fluent rebase onto `DataSource` ✅ **DISCHARGED 2026-08-11**

`SqlDatabase`/`SqlQuery`/`SqlTable` derive from the shared `DataSource` family (HANDOFF-17 §5, P4b).
This section was written when nothing had executed the rebased code. It has since been **closed by
automation** — every item below is now asserted by `FluentSqlIntegrationTest`, which ran green
(10/10, 0 skipped) against a live SQL Server:

- [x] The gated suite runs and passes, not skips:
      `CLRKERNEL_TEST_REQUIRE_LIVE=1 CLRKERNEL_TEST_SQL='…' dotnet test test/ClrKernel.Database.UnitTest/ClrKernel.Database.UnitTest.csproj -f net8.0 --filter FullyQualifiedName~Integration`
- [x] `OpenReader()` returns a `SqlDataReader` — `Table_bulkcopy_create_if_missing_and_exists` runs
      the `(SqlDataReader)base.OpenReader()` cast; an invalid cast would throw.
- [x] `Count()` returns the right number through the `count_big` override (asserted `== 2`).
- [x] `DefaultCommandTimeout` is honoured by `Execute`/`Scalar` —
      `DefaultCommandTimeout_is_honoured_by_Scalar` sets 1s, waits 5s, requires `SqlException` −2.
- [x] A transaction's `Query(…, limit)` is honoured — `Transaction_query_honours_its_row_limit`.
- [x] `Transaction()` returns `DataSourceTransaction` (compile-time, plus the rollback test).
- [x] A missing secret throws `SqlCellException` on first use, not at construction — covered offline
      by `FluentSqlInheritanceTest`.

**Pin these two, they are the ones that discriminate.** `DefaultCommandTimeout` and the transaction
`limit` were bugs in the SQL-specific code the rebase deleted, so on pre-rebase code they pass by
doing the wrong thing. If they ever start passing *without* a server, or stop being run, this gate
has gone hollow again — which is what `CLRKERNEL_TEST_REQUIRE_LIVE` exists to prevent.

**Run a single target framework.** These tests use fixed table names (`dbo.FluentOrders`,
`dbo.ClrTarget`, …) with no per-run suffix, so a multi-TFM run executes them three times in
parallel against one database and they collide. Use `-f net8.0`.

## 7. ETL — bulk / merge / pipeline / deploy — **(SQL)**  *(optional but recommended)*

- [ ] `#!sql-bulk --from <a> --query "..." --to <b> --table stg.X --truncate` streams with a **progress bar**.
- [ ] `#!sql-bulk … --table stg.NewX --create` against a **non-existent** destination → the table is created from the source schema and loaded (re-running is a no-op create). Confirm the columns/types match the source.
- [ ] `#!sql-merge --connection <b> --target dbo.X --source stg.X --on Id` reports inserted/updated/deleted.
- [ ] Cells annotated `-- step name` / `-- needs a, b` + `#!sql-run` execute as a **DAG** (independent steps parallel; a failed step skips downstream; status board updates). `-- needs` autocompletes step names from other cells.
- [ ] `#!sql-deploy --path <folder-of-.sql>` applies idempotently (re-run = no-op / CREATE OR ALTER).

### 7a. P8 — pipeline and deploy after the DataEngineering move

The step/DAG and multi-pass deploy code moved to `ClrKernel.DataEngineering` unchanged (namespace
only). The unit tests cover the logic; what they can't cover is that the magics still wire to it.

- [ ] `#!sql-run` still renders the live status board and the DAG still runs in dependency order.
- [ ] A failing step still skips its downstream steps and leaves independent branches running.
- [ ] `#!sql-deploy` still retries across passes (a file referencing an object defined in a later
      file still lands) and `--dry-run` still lists without executing.
- [ ] *(new, optional)* The table-action model from C#: build a
      `TableAction.TruncateInsert("dbo.T", TableSource.Query("src", "select ..."))` and run it
      through `new SqlServerTableTarget(registry)` → rows land, and the result's `Strategy` reads
      `TRUNCATE + SqlBulkCopy`. This is the only path in P8 that is genuinely new code.

## 8. Other providers — **(Oracle/ODBC)**

Oracle:

- [ ] `#r "nuget: ClrKernel.Database.Provider.Oracle"` resolves the driver.
- [ ] `Oracle.Connect("<host>",1521,"<service>","<user>","oracle:<secretRef>")` → `.Query("select * from ...").Results()` renders the **same grid**.
- [ ] `.Results<T>()`, `.Table()`, `.Transaction()` behave as with SQL Server.

ODBC:

- [ ] `#r "nuget: ClrKernel.Database.Provider.Odbc"` resolves.
- [ ] `Odbc.FromConnectionString("Driver={...};...")` against a configured **⊞ Windows ODBC DSN/driver** → query renders.

Config file:

- [ ] Create a `connections.json` in/above the notebook folder with an `Oracle` (and/or `Odbc`) entry using a `{ "secret": "oracle:erp" }` password ref.
- [ ] `Oracle.FromConfig("erp")` / `Odbc.FromConfig(...)` connects; confirm the file is **found up the folder tree** and the **secret resolves from Credential Manager / env var** (no plaintext password in the file).
### JDBC / OpenEdge — **experimental, from source only** ⊞

Not in the published NuGet packages (excluded from `ClrKernel.slnx` and the release
pack because IKVM is Windows-x64-centric). Test it by packing the project locally.
Needs the **DataDirect OpenEdge JDBC driver** you supply (from your OpenEdge install,
e.g. `%DLC%\java\openedge.jar`).

- [ ] Pack the provider from a repo checkout on Windows:
      `dotnet pack src\ClrKernel.Database.Provider.Jdbc\ClrKernel.Database.Provider.Jdbc.csproj -c Release -o artifacts\pkg`
      → produces `ClrKernel.Database.Provider.Jdbc.<version>.nupkg` (version matches the other packages, e.g. 0.8.0). IKVM restores automatically.
- [ ] In a notebook, add the local feed and reference it:
      `#i "nuget:<repo>\artifacts\pkg"` then `#r "nuget: ClrKernel.Database.Provider.Jdbc, <version>"` → resolves, and IKVM + `ClrKernel.Database` come with it.
- [ ] **Jar path (easiest):** `Jdbc.ConnectJar("jdbc:datadirect:openedge://host:port;databaseName=<db>", OpenEdge.JdbcDriverClass, driverJarPath: @"<...>\openedge.jar", user: "<u>", secretRef: "openedge:<ref>")` → `.Query("select * from PUB.<table>").Results()` renders the grid.
- [ ] **Compiled-assembly path (alt):** `ikvmc openedge.jar -out:OpenEdge.JdbcDriver.dll`, then `OpenEdge.Connect("<host>","<db>","<u>","openedge:<ref>", driverAssemblyPath:@"<...>\OpenEdge.JdbcDriver.dll")`.
- [ ] Confirm the **secret** resolves from Windows Credential Manager / `CLRKERNEL_SECRET_*` (same store as the other providers).
- [ ] Confirm the two known limits: **parameterless SQL only** (the JDBC bridge ignores command parameters), and it only works on **Windows x64** (IKVM `win-x64`).
- [ ] If it works, note it — this is the first real validation, and the gate for adding JDBC to `ClrKernel.slnx` + the release pack to publish it.

## 9. DAX + Analysis Services — **(SSAS)**

- [ ] Set a cell to **DAX**; the cube **Select connection** button appears.
- [ ] **Add cube…** → point at your SSAS/Azure AS instance + database. **Edit cube…** works from the dropdown.
- [ ] `EVALUATE TOPN(100, ...)` → results in the grid; DAX keywords/functions autocomplete.
- [ ] From C#: `AnalysisServices.Connect("<ssas>","<db>")` → `cube.Query("EVALUATE ...")` grid; `cube.Tables().DisplayTable()` shows model metadata.
- [ ] **⊞** `cube.ProcessPartitions(...)` / `cube.Recalculate()` against on-prem SSAS with Integrated auth (this is generally a Windows-only path) — a partition refreshes.

## 10. DAX + Fabric / Power BI semantic model — **(Fabric)**

- [ ] `#!dax-connect --fabric --workspace "<W>" --model "<M>"` (or `AnalysisServices.ConnectFabric("<W>","<M>")`) triggers **Entra** sign-in and connects.
- [ ] A DAX `EVALUATE` against the semantic model returns rows in the grid.

## 11. Fabric Warehouse writes — **(Fabric)**

- [ ] `#r "nuget: ClrKernel.Database.Provider.Fabric"`; `Fabric.Connect().Workspace("<W>").Warehouse("<DW>").WithStaging("<Lakehouse>")` → interactive **Entra** sign-in succeeds.
- [ ] `wh.BulkInsert(reader, "dbo.X", createIfMissing: true)` from a SQL Server `IDataReader` — table is created from the reader schema and rows land (verify a row count in the warehouse).
- [ ] Re-run to an existing table (createIfMissing effectively no-op) loads without error.
- [ ] `wh.ReloadBatch(requests, factory, maxParallelism: N)` deletes + reloads segments across tables in parallel; `results.DisplayTable()` summarizes.
- [ ] *(optional)* Service principal path: `Fabric.ClientSecret(tenantId, clientId, secret)` connects headlessly.

### 11a. P6 gate — shared Entra auth ✅ **credential half discharged 2026-08-11**

Credential construction and token acquisition moved out of both providers into
`ClrKernel.Database.Entra` (HANDOFF-17 §5, P6). No CI test signs in to a tenant, so **the Entra
paths below have not been executed since the move**. The two providers' credential chains were
deliberately left different — that is the thing to confirm, not to "fix".

**Run the automated half first — it needs only `az login`, no Azure resources, and writes nothing.**
Acquiring a token is an authentication call, not a resource operation.

```
az login
export CLRKERNEL_TEST_ENTRA=1 CLRKERNEL_TEST_REQUIRE_LIVE=1
dotnet test test/ClrKernel.Database.UnitTest/ClrKernel.Database.UnitTest.csproj \
  -f net8.0 --filter FullyQualifiedName~EntraLiveTest
```

- [x] All three `EntraLiveTest` cases pass — both chains acquire a token and resolve to the **same**
      identity (3/3 green, 2026-08-11). A tenant that won't issue for `database.windows.net` can be
      pointed elsewhere with `CLRKERNEL_TEST_ENTRA_SCOPE`.
- [x] The three scope strings are byte-identical to their pre-P6 values, and each call site still
      passes the right one — Fabric warehouse → `SqlDatabase`, Azure AS → `AzureAnalysisServices`,
      `ConnectFabric` → `PowerBi`. Checked against `git show d647712^`, not by eye.

**What that leaves.** Token acquisition through both chains is proven, and the scopes and their
routing are proven. The remaining items exercise the endpoint connections — AMO/ADOMD and the
warehouse's `AccessTokenCallback` — which P6 did **not** change except in where the token comes
from. They need real resources and cannot be faked locally:

- [ ] `Fabric.Connect()` → sign-in behaves as it did before: non-interactive `DefaultAzureCredential` first, browser prompt only if that yields nothing.
- [ ] `AnalysisServices.ConnectFabric("<W>","<M>")` → acquires a token and connects (scope `https://analysis.windows.net/powerbi/api/.default`).
- [ ] `AnalysisServices.ConnectAzureAnalysisServices("<server>","<db>")` against Azure AS → connects (scope `https://*.asazure.windows.net/.default`).
- [ ] A Fabric Warehouse query/`BulkInsert` still authenticates — this is the `https://database.windows.net/.default` scope, now read from `EntraScopes.SqlDatabase`.
- [ ] `Fabric.ClientSecret(tenantId, clientId, secret)` still rejects blank arguments with `ArgumentException` naming the offending one, and connects with valid ones.
- [ ] **Identity check, not just success:** confirm the account each provider signs in as is the same one as before the move. A changed credential-probe order surfaces as a *working* connection under the wrong identity, not as an error.

## 12. Other cell languages

- [ ] **HTTP**: a ` ```http ` cell with a GET → rich response card (status, timing, collapsible headers, pretty JSON). A second `###` request using `{{var}}` and a chained `{{login.response.body.$.token}}` resolves.
- [ ] **Mermaid**: a ` ```mermaid ` flowchart renders **offline** (disconnect network briefly if you want to prove no-CDN) and follows the VS Code theme (toggle light/dark).
- [ ] **⊞ PowerShell**: a ` ```powershell ` cell runs in-process; `$x = 1` persists to the next cell; IntelliSense completes a cmdlet/parameter/variable. Confirm no separate PowerShell install was needed.

## 13. Executable-markdown round-trip & samples

- [ ] Open the repo's `samples/*.nb.md` (Sql, SqlQuery, Dax, MultiProvider, FabricWarehouse, HttpRequests, MermaidDiagrams, PowerShell, AnalysisServices) — each opens as a notebook and the cells you have backends for run.
- [ ] Open a `.nb.md` in a plain text editor → it's readable Markdown with fenced blocks; edits round-trip back into cells (no serialization corruption).
- [ ] *(optional)* View a `.nb.md` on GitHub → renders as a clean document.

## 14. Headless / scheduled — **(SQL)** *(optional; the SQL Server Agent story)*

- [ ] `jupyter nbconvert --to notebook --execute --output out.ipynb <notebook>.ipynb` runs end-to-end.
- [ ] `papermill in.ipynb out.ipynb -k clrkernel -p run_date 2026-08-10` injects the parameter and runs.
- [ ] A notebook whose cell fails → process exits **non-zero** (so SQL Server Agent / a scheduler sees the failure); papermill still writes the partial output notebook.

## 15. Teardown / regression

- [ ] Restart VS Code → reopen a notebook → controller reconnects; one server per window (REPL state shared across notebooks in that window).
- [ ] Check the **ClrKernel output channel** (View → Output → ClrKernel) for clean startup and no stray errors/stack traces during the run.
- [ ] *(optional)* `dotnet tool uninstall --global ClrKernel` then reinstall → clean removal and reinstall.

---

## Sign-off

| # | Area | Backend | Result (P/F) | Notes |
|---|------|---------|-------------|-------|
| 1 | Pre-flight | — | | |
| 2 | Kernel install 0.8.0 | — | | |
| 3 | Extension 0.4.0 | — | | |
| 4 | C# core + IntelliSense | — | | |
| 5 | SQL cells + grid | SQL | | |
| 6 | Sql C# API | SQL | | |
| 7 | ETL (bulk/merge/pipeline) | SQL | | |
| 8 | Oracle / ODBC / config | Oracle/ODBC | | |
| 8b | JDBC / OpenEdge (from source) | OpenEdge | | |
| 9 | DAX + SSAS | SSAS | | |
| 10 | DAX + Fabric model | Fabric | | |
| 11 | Fabric Warehouse writes | Fabric | | |
| 12 | HTTP / Mermaid / PowerShell | — | | |
| 13 | Markdown round-trip + samples | — | | |
| 14 | Headless / scheduled | SQL | | |
| 15 | Teardown / regression | — | | |

**Top Windows-specific things to confirm (couldn't test on macOS):**
1. Windows **Integrated auth** to SQL Server and SSAS.
2. Secrets stored in **Windows Credential Manager**, never in the notebook.
3. Global tool discoverable via `%USERPROFILE%\.dotnet\tools` on PATH.
4. **SSAS processing** (`ProcessPartitions`/`Recalculate`) on-prem.
5. **ODBC DSN/driver** resolution through the Windows ODBC stack.
6. **PowerShell** cells using the in-process Windows runspace.
