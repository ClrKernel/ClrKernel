# ClrKernel — Windows 1.0.0 trust run

Everything that must pass on Windows before the 1.0.0 bump, as copy/paste
blocks. Assumes **this machine already has**: SQL Server with
**AdventureWorksDW2025** restored, **SSAS running with a model deployed**, and
Windows Integrated auth verified for both. Work top to bottom; every item is
*paste this → expect this*.

**Set these three values once**, then paste everything else verbatim (all
snippets use them by name):

| Placeholder | Yours | Notes |
| --- | --- | --- |
| `SQL_SERVER` | `localhost` | Or `machine\INSTANCE` |
| `SSAS_SERVER` | `localhost` | Or `machine\TABULAR` — check SSMS's Object Explorer connection name |
| `SSAS_DB` | *(your model's name)* | SSMS → connect to Analysis Services → Databases node shows it |

> The SSAS sections assume a **Tabular** model (DAX `EVALUATE` + the DMVs the
> metadata calls use). If your deployed model is Multidimensional, `Tables()`
> and `EVALUATE` will error — deploy the AdventureWorks **Tabular** sample
> instead before running §7–8.

Tags: **⊞** = Windows-only behavior that macOS testing could not cover — these
are the items 1.0 actually hinges on.

---

## 1. Versions & install

```powershell
dotnet tool update --global ClrKernel
clrkernel --version
```

- [ ] Reports **0.9.1**.
- [ ] VS Code → Extensions → **ClrKernel Notebooks** shows **0.6.0**; its
      Changelog tab shows the 0.6.0 entry (per-notebook sessions at the top).

## 2. Build + full test suite from source ⊞

```powershell
git pull
.\build.ps1 Test
.\build.ps1 Format
```

- [ ] All three test projects pass on **all three TFMs** (net8.0 / net9.0 /
      net10.0 — nine `Passed!` lines total).
- [ ] `Format` reports no changes needed.

## 3. Live SQL tests against this machine ⊞ (SQL)

The live suite creates and drops its own fixed-name tables — point it at a
**scratch database**, not AdventureWorksDW2025:

```powershell
sqlcmd -S SQL_SERVER -E -Q "IF DB_ID('ClrKernelTest') IS NULL CREATE DATABASE ClrKernelTest;"
$env:CLRKERNEL_TEST_SQL = "Server=SQL_SERVER;Database=ClrKernelTest;Integrated Security=true;TrustServerCertificate=true"
dotnet test test\ClrKernel.Database.UnitTest -f net8.0
```

- [ ] **⊞ Integrated Security in the connection string** — this is the auth
      path macOS can't test. All tests pass, none inconclusive with the env var
      set. (Single TFM on purpose: the live tests use fixed table names, and a
      multi-TFM run races three copies against one database.)

```powershell
Remove-Item Env:CLRKERNEL_TEST_SQL
```

## 4. C# notebook core

Create `C:\temp\clrkernel test\nb1.nb.md` — **keep the space in the folder
name**; it exercises URL-encoded paths in the per-notebook session keys ⊞.
Open it in VS Code and run these as separate cells:

```csharp
var x = 41;
```
```csharp
x + 1
```

- [ ] Second cell prints `42` (REPL state persists).

```csharp
#r "nuget: Humanizer"
using Humanizer;
TimeSpan.FromDays(45).Humanize()
```

- [ ] Prints a humanized duration (nuget restore + using works).

```csharp
for (var i = 0; i < 5; i++) { Console.WriteLine(i); System.Threading.Thread.Sleep(300); }
```

- [ ] Lines stream one by one, not all at the end.

## 5. IntelliSense, documentation, and definitions

All in `nb1.nb.md`, using the cells from §4.

- [ ] **Hover with docs**: hover `Humanize` → signature in a C# code block
      **plus the `///` summary prose** underneath.
- [ ] **Completion docs**: type `Console.` and arrow down the list → each
      focused item fills in its signature + summary after a beat (lazy
      resolve). No wrong-looking pairings while moving quickly.
- [ ] **Signature help**: type `Math.Round(` → overloads with parameter docs.
- [ ] **F12 on your own symbol**: F12 on `x` in the `x + 1` cell → jumps to
      `var x = 41;` in the first cell.
- [ ] **F12 to decompiled source**: F12 on `WriteLine` → read-only decompiled
      `Console` scrolled to `WriteLine`. Same for `Humanize` (nuget package).
- [ ] **Namespace peek**: add a cell `using System.Text; // sb helpers` and
      F12 on `Text` → an overview document of the namespace's public types
      with summaries. **The trailing comment must not break it.**
- [ ] **Alt+F12 (peek)** on any of the above works inline.

### Extension methods defined in a cell

```csharp
#r "nuget: Humanizer"
using Humanizer;
/// <summary>Shouts the string.</summary>
public static class StrExt { public static string Shout(this string s) => s.Humanize().ToUpperInvariant(); }
```
```csharp
"hello world".Shout()
```

- [ ] Both cells run; second prints `HELLO WORLD` (extension class + `#r` in
      **one** cell — the hard case).
- [ ] `.Sho` completes `Shout`; the focused item shows *Shouts the string.*
- [ ] F12 on `Shout` at the call site opens its source.
- [ ] Edit the first cell (`* 1` → append `+ "!"` or similar), re-run both →
      new behavior wins (redefinition supersedes).

## 6. Per-notebook isolation & restart ⊞

The session keys are derived from Windows cell URIs (drive letters, encoded
spaces) — this section is the main reason for a Windows pass.

1. Keep `nb1.nb.md` open (with `x` defined). Create
   `C:\temp\clrkernel test\nb2.nb.md` and open it **side by side**.
2. In **nb2** run:

```csharp
x
```

- [ ] **Fails** with `CS0103: The name 'x' does not exist` — nb1's variables
      must not leak.

```csharp
var y = 99;
```

3. In **nb2** type `x` slowly / hover it:

- [ ] No completion entry, no hover — IntelliSense is isolated too.

4. Focus **nb1**, run **ClrKernel: Restart Kernel** from the Command Palette.

- [ ] The notice names **nb1** and says other notebooks are untouched.
- [ ] In nb1, `x` now fails (fresh session).
- [ ] In **nb2**, run `y` → still `99` ⊞ (nb2 survived nb1's restart — this is
      the drive-letter/URI normalization actually working).

## 7. SQL cells against AdventureWorksDW2025 (SQL)

Add a cell, set language **SQL**:

```
#!sql-connect --name aw --server SQL_SERVER --database AdventureWorksDW2025 --auth integrated --default
```

- [ ] Runs clean; no password anywhere in the file.

```sql
SELECT TOP 200 c.FirstName, c.LastName, g.EnglishCountryRegionName AS Country, c.YearlyIncome
FROM dbo.DimCustomer c JOIN dbo.DimGeography g ON c.GeographyKey = g.GeographyKey
ORDER BY c.YearlyIncome DESC;
```

- [ ] Interactive grid renders. Sort a column; type in the global filter; open
      the **Country** ▾ picker and check two values; **Analyze**; **Clear**.
- [ ] The **connection button** next to the language picker shows `aw`;
      **Edit connection…** opens the guided prompts (Esc out).

C# querying, same session (new C# cell — the `#!sql-connect` also bound an
`aw` variable):

```csharp
var top = aw.Query("SELECT TOP 5 EnglishProductName, ListPrice FROM dbo.DimProduct WHERE ListPrice IS NOT NULL ORDER BY ListPrice DESC").Results();
top
```

- [ ] Grid of 5 products. Then typed + parameters:

```csharp
record Product(string EnglishProductName, decimal ListPrice);
var bikes = aw.Query("SELECT EnglishProductName, ListPrice FROM dbo.DimProduct WHERE ListPrice > @p", new { p = 3000m }).Results<Product>();
bikes.First().EnglishProductName
```

- [ ] Prints a product name (typed mapping + parameter binding).

Bulk copy + MERGE round trip (writes only to tables it creates):

```
#!sql-bulk --from aw --query "SELECT ProductKey, EnglishProductName, ListPrice FROM dbo.DimProduct" --to aw --table dbo.ClrKernelVerify --create --truncate
```

- [ ] Progress bar; row count matches DimProduct (~600).

```
#!sql-merge --connection aw --target dbo.ClrKernelVerify --source dbo.DimProduct --on ProductKey
```

- [ ] Reports inserted/updated/deleted counts (0 inserted on the second run —
      run it twice to see idempotence). Clean up:

```sql
DROP TABLE dbo.ClrKernelVerify;
```

## 8. Analysis Services — C# API ⊞ (SSAS)

New C# cell. Connect (Integrated auth is the default — the ⊞ path):

```csharp
var cube = AnalysisServices.Connect("SSAS_SERVER", "SSAS_DB");
cube.Tables().DisplayTable()
```

- [ ] A grid of the model's tables with row counts / refresh times. **Pick any
      table name from this grid** and use it wherever `'Date'` appears below
      (AdventureWorks Tabular has `Date`, `Product`, `Internet Sales`, …).

```csharp
cube.Partitions().DisplayTable()
```

- [ ] Partition metadata renders (name, table, last processed).

```csharp
cube.Query("EVALUATE TOPN(10, 'Date')")
```

- [ ] Ten rows in the interactive grid.

```csharp
var rows = cube.QueryRows("EVALUATE ROW(\"Rows\", COUNTROWS('Date'))");
rows[0]["[Rows]"]
```

- [ ] Prints the table's row count (matches the `Tables()` grid).

Processing (mutates the model — fine on the sample):

```csharp
cube.Recalculate();
```

- [ ] Completes without error; re-run `cube.Tables().DisplayTable()` → refresh
      times updated.

```csharp
cube.ProcessTables("Date");
```

- [ ] Completes; the `Date` table's last-processed time is now.
- [ ] *(optional, full refresh)* `cube.ProcessModel(SsasRefresh.Full);` — takes
      longer, everything reprocesses.

## 9. DAX cells ⊞ (SSAS)

Cell language **DAX**:

```
#!dax-connect --name awcube --server SSAS_SERVER --database SSAS_DB --default
```

- [ ] Runs clean. The **cube connection button** appears by the language
      picker and lists `awcube`.

New DAX cell (swap `'Date'`/`'Internet Sales'` for tables from §8's grid):

```dax
EVALUATE TOPN(20, 'Date')
```

- [ ] Grid renders.

```dax
EVALUATE ROW("Sales", SUM('Internet Sales'[Sales Amount]))
```

- [ ] One-row grid with the total. (Column name differs per model — F12/typo
      errors here are the *model's* names, not a kernel fault; take a measure
      or column you can see in SSMS.)
- [ ] In a DAX cell, Ctrl+Space completes DAX functions and the `#!dax-*`
      magics; typing `#!dax-connect --` completes flags.

## 10. PowerShell & shell cells

Cell language **PowerShell**:

```powershell
$PSVersionTable.PSVersion.ToString()
```
```powershell
Get-Process | Sort-Object CPU -Descending | Select-Object -First 3 Name, CPU
```

- [ ] Both run in one persistent runspace (set `$a = 1` in one cell, read it in
      the next). Completion works on cmdlets and parameters.

Cell language **Shell Script** ⊞:

```bash
echo "hello from $PWD"
```

- [ ] Runs. On Windows without Git Bash this lands on the PowerShell fallback
      (bash → sh → pwsh → powershell); with Git Bash installed it's real bash.
      Either way: output appears, and a second cell keeps the working
      directory.

## 11. WinRM PSRemoting to localhost ⊞ (the never-tested path)

In an **elevated** PowerShell (one-time machine prep — skip if already on):

```powershell
Enable-PSRemoting -Force
```

Store the password as a secret reference (env var route — no UI needed;
`pwsh:localwinrm` resolves from `CLRKERNEL_SECRET_PWSH_LOCALWINRM`). Set it for
VS Code's environment, then **fully restart VS Code** so the kernel inherits it:

```powershell
[Environment]::SetEnvironmentVariable("CLRKERNEL_SECRET_PWSH_LOCALWINRM", "<your Windows password>", "User")
```

PowerShell cell:

```powershell
#!pwsh-connect --name localwin --host localhost --winrm --user <DOMAIN\user or user> --secret pwsh:localwinrm
```
```powershell
#!pwsh --connection localwin
hostname; $env:COMPUTERNAME
```

- [ ] Both print this machine's name **via a real WinRM session**.
- [ ] State persists remotely: `#!pwsh --connection localwin` + `$r = 5` in one
      cell, `$r` in the next → `5`.
- [ ] Afterward, remove the env var:
      `[Environment]::SetEnvironmentVariable("CLRKERNEL_SECRET_PWSH_LOCALWINRM", $null, "User")`

*(optional, SSH variant)* If OpenSSH Server + the sshd `Subsystem powershell`
line are configured: `$env:CLRKERNEL_TEST_PSREMOTE = "user@localhost"` then
`dotnet test test\ClrKernel.Language.UnitTest -f net8.0 --filter "Name~Live_psremoting"`.

## 12. HTTP & Mermaid cells

Cell language **HTTP**:

```
GET https://api.github.com/repos/ClrKernel/ClrKernel
Accept: application/json
```

- [ ] Response card: status, timing, collapsible headers, highlighted JSON.

Cell language **Mermaid**:

```
flowchart LR
  A[nb1] -->|isolated| B[session 1]
  C[nb2] -->|isolated| D[session 2]
```

- [ ] Renders offline, follows the editor theme.

## 13. Connections file round trip

- [ ] After §7's connection-button add (or `Edit connection…` → re-save), use
      **Save to connections.json** → file written next to the notebook with
      `"$type": "SqlServer"` and **no plaintext password** (open it and look).
- [ ] Close VS Code entirely, reopen `nb1.nb.md`, run a `#!sql` cell that names
      the saved connection → it resolves without re-adding (auto-load).
- [ ] **⊞ Credential Manager**: *(only if you also created a SQL-login
      connection)* Control Panel → Credential Manager → Windows Credentials
      shows the ClrKernel entry; the notebook and json never contain the
      password.

## 14. Headless

```powershell
clrkernel run "C:\temp\clrkernel test\nb1.nb.md" -o "C:\temp\clrkernel test\out.ipynb"
```

- [ ] Exit code 0; `out.ipynb` contains executed outputs (open it in VS Code).
- [ ] *(optional, Jupyter installed)* `jupyter kernelspec install "$(clrkernel --kernel-spec-path)" --user --name clrkernel`
      then `jupyter nbconvert --to notebook --execute` on a `.ipynb` works.

---

## Sign-off

| # | Area | The 1.0 gate it discharges | P/F | Notes |
| --- | --- | --- | --- | --- |
| 2 | Source build + tests on Windows | Platform parity | | |
| 3 | Live SQL, Integrated auth | Windows auth path | | |
| 5 | Docs/definitions/extension methods | New IntelliSense features on Windows paths | | |
| 6 | Per-notebook isolation + restart | Windows URI/drive-letter session keys | | |
| 7 | SQL cells + C# + bulk/merge | Data core against a real database | | |
| 8–9 | SSAS C# + DAX cells | The unverified live-backend gate (§11a of the old checklist) | | |
| 11 | WinRM | The never-tested transport | | |
| 13 | Credential Manager + connections.json | Secret invariant on Windows | | |
| 14 | Headless run | Scheduler story | | |

Still outside this run (decide before 1.0: verify, or label experimental):
**Fabric / Azure AS live connection** (needs a tenant), **Oracle/ODBC/JDBC
providers**, and marketplace-install UX on a machine that never had the tool.
