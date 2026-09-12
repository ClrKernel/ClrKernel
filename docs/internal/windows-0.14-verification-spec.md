# Windows verification spec — kernel 0.14.0, extension 0.11.0

**Audience:** the Claude Code session running on the Windows VM. Read this whole
file first. It is the brief, the checklist and the reporting format in one.

**Scope:** everything the product does on Windows against on-prem backends —
SQL Server and Analysis Services on the same network, integrated login with `sa`
rights, nothing in them is production. **Out of scope, do not attempt:** anything
Entra / Azure / Fabric / Power BI / Kusto-live — `Fabric.*`, `AnalysisServices
.ConnectFabric`, `--fabric`, `--azure-as`, `#!kql` against a real cluster,
`EntraLiveTest`. Those need a tenant; they are verified elsewhere.

**Repo:** `https://github.com/ClrKernel/ClrKernel`, branch `main` at tag `v0.14.0`
(the extension is `ext-v0.11.0`, same commit). Everything below is relative to the
clone root. `docs/internal/handoff/HANDOFF-NN-*.md` is the design record — read the
relevant one before deciding something is a bug: HANDOFF-26 (`#r "project:"`), 27
(NuGet.Config), 28 (secret masking), 29 (F#, KQL, `#!share`).

## 0. How to work

1. **Record as you go** in `docs/internal/windows-0.14-results.md` (create it; format
   in §R at the end). One line per checkbox: pass / fail / skipped-with-reason, and
   for a failure the exact command, the exact output, and what you concluded. A
   result nobody can reproduce from the file is not a result.
2. **Do not stop at the first failure.** Finish the section, then the document.
   Order the sections as written; each later one assumes the earlier ones.
3. **A check that cannot fail is not a check.** Where the spec says *break it*,
   do: revert the thing, watch the check go red, restore it. Say in the results
   that you did.
4. **Fix or report.** A failure whose cause you can see and whose fix is a few
   lines with a test: fix it on a branch `windows/<short-name>`, run the suite,
   commit with a message that says what CI could not have seen, and note the
   branch in the results. Anything bigger: report it with the evidence and move on.
   Never commit to `main`.
5. **Verification runs set `CLRKERNEL_TEST_REQUIRE_LIVE=1`.** With it, a live test
   whose backend variable is missing *fails* instead of skipping, so "Passed!" means
   the backend was reached. Set it only for the runs in §B and §F where every gated
   backend in the filter is configured, or the Entra/Kusto tests will fail by design.
6. **Never `taskkill /IM dotnet.exe` wholesale** to clean up: it kills the test host
   too. Kill the specific `ClrKernel` / `ClrKernel.Studio` process.
7. **Secrets** go in the Windows Credential Manager or `CLRKERNEL_SECRET_*`
   variables, never in a notebook, a `connections.json`, or this results file.
8. Timebox: a section that will not go green in an hour of honest effort gets
   recorded as failed with what you learned; the rest of the document still matters.

## 1. Machine prerequisites

Install with `winget` where possible; record the versions you ended up with.

| What | Why | Install |
|---|---|---|
| Git | clone, and Studio's git workflow shells out to it | `winget install Git.Git` |
| .NET SDK 10 **and** .NET SDK 8 (which brings the 8.0 runtime), .NET 9 runtime | the kernel is net8.0; test projects multi-target net8/9/10 and run each on its own runtime | `winget install Microsoft.DotNet.SDK.10`, `Microsoft.DotNet.SDK.8`, `Microsoft.DotNet.Runtime.9` |
| Node.js 20 LTS | VS Code extension and Studio web app builds/tests | `winget install OpenJS.NodeJS.LTS` |
| Python 3.12 + pip | test harnesses (`test/tools/*.py`), Jupyter | `winget install Python.Python.3.12` |
| Playwright + Chromium | `studio_ui_test.py`, `studio_screenshots.py` | `pip install playwright` then `playwright install chromium` |
| Jupyter + nbconvert + jupyter_client + pyzmq | Jupyter-mode kernel test, nbconvert smoke | `pip install jupyter nbconvert jupyter_client pyzmq` |
| PowerShell 7 | `#!pwsh` uses the in-process runspace, but the harness scripts and WinRM checks want pwsh | `winget install Microsoft.PowerShell` |
| VS Code + the **ClrKernel Notebooks** extension 0.11.0 from the Marketplace | §D onwards | `winget install Microsoft.VisualStudioCode`, then Extensions → ClrKernel Notebooks |
| **Polyglot Notebooks** VS Code extension (optional but wanted) | §K: the "which extension opens `.dib`" prompt only exists when both are installed | Extensions → Polyglot Notebooks |
| ODBC Driver 18 for SQL Server | §H ODBC provider against the same SQL Server | `winget install Microsoft.msodbcsql.18` |
| Docker Desktop (optional) | PostgreSQL / Oracle live tests; skip if the VM cannot run it and mark those skipped | `winget install Docker.DockerDesktop` |
| Java 17+ JRE (optional) | JDBC provider (experimental) | `winget install EclipseAdoptium.Temurin.17.JRE` |
| GitHub CLI (optional) | pulling CI logs if you need them | `winget install GitHub.cli` |

**Not needed:** SSMS, SQL Server locally (the server is on the network), an Azure
account.

**Network:** the SQL Server and Analysis Services host must resolve by name and
accept integrated (Kerberos/NTLM) login from the VM's account. Confirm before
anything else:

```powershell
sqlcmd -S <server> -E -Q "SELECT @@VERSION"      # sqlcmd ships with the ODBC driver's tools, or: winget install Microsoft.Sqlcmd
Test-NetConnection <server> -Port 2383           # SSAS default instance
```

Record `<server>`, the SQL instance name, the SSAS instance, and one database +
one tabular model you will use throughout. Create a scratch database
`ClrKernelVerify` on the SQL Server and use it for every write.

## 2. Environment

Set for the **user** (so VS Code and Studio inherit them), then restart any VS
Code / terminal you had open. `Machine`-scope is not needed.

```powershell
[Environment]::SetEnvironmentVariable("CLRKERNEL_TEST_SQL", "Server=<server>;Database=ClrKernelVerify;Integrated Security=true;TrustServerCertificate=true", "User")
[Environment]::SetEnvironmentVariable("CLRKERNEL_STUDIO_TEST_SQL", "<same connection string>", "User")
[Environment]::SetEnvironmentVariable("CLRKERNEL_STUDIO_TEST_SQLSERVER", "<same connection string>", "User")     # the run store on SQL Server
[Environment]::SetEnvironmentVariable("CLRKERNEL_STUDIO_TEST_ODBC", "Driver={ODBC Driver 18 for SQL Server};Server=<server>;Database=ClrKernelVerify;Trusted_Connection=yes;TrustServerCertificate=yes", "User")
# Leave unset: CLRKERNEL_TEST_ENTRA, CLRKERNEL_TEST_KUSTO, CLRKERNEL_TEST_ORACLE, CLRKERNEL_STUDIO_TEST_POSTGRES (unless Docker), CLRKERNEL_TEST_SSH, CLRKERNEL_TEST_PSREMOTE (see §L).
```

`CLRKERNEL_TEST_REQUIRE_LIVE` is set **per command** in the sections that want it,
not globally.

## A. Clone, build, unit suite

```powershell
git clone https://github.com/ClrKernel/ClrKernel.git; cd ClrKernel; git checkout v0.14.0
dotnet --list-sdks; dotnet --list-runtimes
dotnet restore ClrKernel.slnx
dotnet format ClrKernel.slnx --verify-no-changes --no-restore
dotnet build ClrKernel.slnx -c Release --no-restore
dotnet test ClrKernel.slnx -c Release --no-build
```

- [ ] Format clean, build clean (record warning count; a `warning CS` in a `Language.FSharp`/`Kql`/`Kusto` project is worth a line).
- [ ] All four tiers pass. Record the Passed/Skipped counts per tier. Skipped should be the live-gated tests only.
- [ ] `build.ps1 Test` (the Nuke wrapper) also works and produces the same result.
- [ ] Extension: `cd editors\vscode; npm ci; npm run compile; npm test`. Web app: `cd src\ClrKernel.Studio\webapp; npm ci; npx tsc --noEmit -p tsconfig.json; npx vitest run`.
- [ ] Multi-target: `dotnet test test\ClrKernel.Language.UnitTest -f net9.0` and `-f net10.0` pass (CI ran only net8.0 through the solution; the F# compiler in-process on 9 and 10 on Windows has never been exercised).

## B. Live tests against SQL Server (from the suite)

```powershell
$env:CLRKERNEL_TEST_REQUIRE_LIVE = "1"
dotnet test test\ClrKernel.Database.UnitTest -f net8.0 --filter "FullyQualifiedName~SqlIntegrationTest|FullyQualifiedName~SqlEtlTest|FullyQualifiedName~SqlPhase2bTest|FullyQualifiedName~FluentSqlTest|FullyQualifiedName~MultiProviderTest"
dotnet test test\ClrKernel.Studio.UnitTest --filter "FullyQualifiedName~RunStoreContractTest|FullyQualifiedName~ConnectionsLiveTest|FullyQualifiedName~OdbcLiveTest"
Remove-Item Env:CLRKERNEL_TEST_REQUIRE_LIVE
```

- [ ] Every test in those filters *ran* (none inconclusive) and passed. Run on **one** framework (`-f net8.0`): the live tests use fixed table names and a multi-TFM run races three copies against one database.
- [ ] `RunStoreContractTest ("sqlserver")` cases passed — Studio's run store on SQL Server, including its EF migrations from empty.
- [ ] `WindowsCredentialTest` (Studio tier): read its three results. The `cmdkey` one is the open question from the last checklist — record found-and-equal / inconclusive / different verbatim.
- [ ] Break it: point `CLRKERNEL_TEST_SQL` at a wrong database name, re-run `SqlIntegrationTest`, confirm it fails loudly, restore.

## C. Installed tools and versions

Two kernels matter: the **released** one (what users get) and the **tree** one
(what you fix). Install both.

```powershell
dotnet tool install --global ClrKernel --version 0.14.*
dotnet tool install --global ClrKernel.Studio --version 0.14.*
clrkernel --version; clrkernel-studio --version
.\scripts\install-local-tool.sh   # bash script; on Windows do the equivalent by hand:
dotnet pack src\ClrKernel\ClrKernel.csproj -c Release -o artifacts
```

- [ ] Both report **0.14.0**. VS Code → Extensions → ClrKernel Notebooks shows **0.11.0**, changelog tab has the 0.11.0 entry at the top.
- [ ] Open any `.nb.md`: **no** kernel-version warning (0.11.0 pins `0.14.*`).
- [ ] Break it: `dotnet tool install --global ClrKernel --version 0.13.0` → reopen VS Code → the warning appears with an **Update Kernel** button that works. Reinstall 0.14.

Unless a step says otherwise, VS Code uses the released kernel. Studio in §O is
started from the tree with `--clrkernel <path to src\ClrKernel\bin\Release\net8.0\ClrKernel.exe>`
so that a fix you make is what you test.

## D. C# core in VS Code

Open `samples\hello.nb.md`, then a new notebook (`ClrKernel: New Markdown Notebook`).

- [ ] Variables, classes, `using`s persist across cells. A trailing expression displays; `Console.WriteLine` streams.
- [ ] Completion on a variable from an earlier cell; hover shows `///` docs; F12 on a BCL type opens decompiled source; F12 on a `using` line opens the namespace overview.
- [ ] `DisplayAs` live update: a progress bar / timer updates in place.
- [ ] **New in 0.14 — a trailing `DataTable` renders as the grid**: `var t = new System.Data.DataTable(); t.Columns.Add("a", typeof(int)); t.Rows.Add(1); t.Rows.Add(2); t` shows two rows, not an empty cell.
- [ ] Grid: sort, per-column filter, value picker, Clear, Analyze — on a `SqlServer.Connection(...).Query(...).Results()` of ≥ 1000 rows (§F).
- [ ] Restart kernel clears state; a second notebook does not see the first's variables.
- [ ] `#!import` of a `.csx` and of a `.dib` library file works; `--register` prefix works.

## E. Packages, projects, NuGet.Config

- [ ] `#r "nuget: Humanizer.Core, 2.14.1"` restores and the type completes.
- [ ] `#r "project: <path to a small class library you create with dotnet new classlib>"` builds and references it; edit the library, re-run the line, the new member is there (HANDOFF-26). Windows paths with spaces and backslashes in the directive.
- [ ] **NuGet.Config hierarchy (HANDOFF-27):** make a folder feed with `dotnet pack` of that classlib, a `NuGet.Config` *above* the notebook naming only the folder (no `<clear/>`), and a cell with `#r "nuget: <YourPackage>"` **and** `#r "nuget: Humanizer.Core, 2.14.1"` — both resolve in one cell. Then add `<clear/>` → Humanizer is refused with NU1101 naming the file, the private one still resolves. (A `<clear/>` feed on a machine that has never restored the runtime packs also refuses `Microsoft.NETCore.App.Runtime.win-x64`; that is correct, note whether you saw it.)
- [ ] `%CLRKERNEL_TEST_FEED%` as the source value expands (set the variable to the feed path).

## F. SQL Server — cells, fluent API, ETL, Studio wizard

Use `samples\Sql.nb.md`, `SqlQuery.nb.md`, `SqlEtl.nb.md`, `SqlPipeline.nb.md` with
the connect line changed to your server and **integrated** auth:

```sql
#!sql-connect --name verify --server <server> --database ClrKernelVerify --auth integrated --default
```

- [ ] `#!sql` cell returns the grid; two SELECTs give two result tabs; `PRINT` and errors appear as messages; a 1001-row query is capped and says so.
- [ ] The connection button: Add connection wizard writes the same directive; Edit; Set default. Save to `connections.json` (no password in the file — integrated has none; also try a SQL login with `--secret <ref>` stored in Credential Manager and confirm the file holds only the reference).
- [ ] Completion: table and column names from the live schema; `-- connections` names; `#!sql-connect --` flags.
- [ ] Fluent: `SqlServer.Connection("<server>", "ClrKernelVerify")` + `.Query(...).Results()`, `.Results<T>()`, `.Table("x").BulkCopyFrom(...)` with `createIfMissing`, `.Truncate()`, transactions roll back on dispose.
- [ ] ETL magics: `#!sql-bulk`, `#!sql-merge` (with delete), `#!sql-run` pipeline order, `#!sql-deploy` idempotent — the samples, against `ClrKernelVerify`.
- [ ] **Dotted table names (0.14):** create `[dbo].[A.B.C]` and bulk-copy into it by that name with `createIfMissing`; `TableName.Quote` behaviour is what the SQL Server `CREATE TABLE` path now uses.
- [ ] `#!ansisql` on the same connection runs; `#!oraclesql` on it is refused with the editor flagging it first.
- [ ] ODBC: `#!sql-connect --name viaodbc --provider Odbc --connection-string "<the ODBC string from §2>"` and a query runs through the ODBC provider.

## G. Analysis Services — on-prem SSAS, integrated

`samples\AnalysisServices.nb.md` and `samples\Dax.nb.md`, connect line:

```dax
#!dax-connect --name cube --server <ssas-server> --database <tabular model> --integrated --default
```

- [ ] `#!dax` `EVALUATE` returns the grid; `-- connections cube` selects; DAX keyword/function completion and hover; the cube connection button (Add / Edit cube).
- [ ] C# API: `AnalysisServices.Connect("<server>", "<model>")` → `.Query(...)`, model metadata (tables, measures), `ProcessPartitions` on a small table (it is not production).
- [ ] `connections.json` round trip under `"$type": "AnalysisServices"` with `auth: integrated`.
- [ ] Studio (§O) connection wizard for AnalysisServices.

## H. Other providers

- [ ] ODBC covered in §F. Record the driver version.
- [ ] JDBC (experimental, optional): with Java installed, `#r "nuget: ClrKernel.Database.Provider.Jdbc"` and the Microsoft JDBC driver jar against the same server; record whether it opens.
- [ ] PostgreSQL / Oracle: only if Docker runs on the VM (`docker compose -f dev/docker-compose.dbs.yml up -d`); then `CLRKERNEL_STUDIO_TEST_POSTGRES` / `CLRKERNEL_TEST_ORACLE` and the corresponding filtered runs from §B. Otherwise mark skipped: not Windows-specific, covered on macOS/Linux.

## I. F# cells (0.14, HANDOFF-29) — never run on Windows

`samples\FSharp.nb.md` in VS Code, then in Studio (§O).

- [ ] Every cell runs; `385`, `Sale × 3 items`, the `printfn` lines, `ADA, BO`, and the shared-threshold sum show; the deliberate compile-error cell reports `FS0001` with line/col and the next cell still sees `square`.
- [ ] `#!share --from csharp` into F# (an `int`, a `List<string>`, a `DateTime` — the value-type path is the one that needed a workaround) and `#!share --from fsharp` into C# (an F# list: `xs.Sum()` works, so FSharp.Core got referenced). A missing variable, an unknown language, and `#!share` in a `#!mermaid` cell each give the one-line error.
- [ ] Editor services: `List.ma` completes `map`; a binding from an earlier cell completes; hover on a function shows its signature; a type error is a red squiggle before running; `#!share --` completes `--from`.
- [ ] Headless: `clrkernel run samples\FSharp.nb.md -o out.ipynb` → exit 1 (the error cell) and the values are in `out.ipynb`.
- [ ] Windows-specific: the first F# cell's startup time (record it); `printfn` with non-ASCII text; a path with spaces in a `#r` of a local dll from F#.

## J. KQL — offline parts only

No cluster. What can be verified without one:

- [ ] `#!kql-connect --name x --cluster https://help.kusto.windows.net --database Samples` registers (no network call); the connection button lists it; `#!kql-connect --` completes `--cluster`; `// connections ` completes `x`; after a `|` the operators complete; hover on `summarize`.
- [ ] `--client-secret` on the line is an editor diagnostic and a run-time refusal.
- [ ] Save to `connections.json` → `"$type": "Kusto"` with `secret` as a reference only; reopen the notebook → it loads.
- [ ] `KqlLiveTest` stays inconclusive (do **not** set `CLRKERNEL_TEST_KUSTO`).

## K. `.dib` and `.ipynb` (0.14)

Make a `.dib` by hand (or take one from your migration set — sanitized) with
`#!meta`, `#!markdown`, `#!csharp`, `#!fsharp`, `#!sql`, `#!kql`, `#!value`
sections.

- [ ] VS Code, **Polyglot not installed**: the `.dib` opens as a ClrKernel notebook; the prompt offers Convert / Keep; Keep → cells run, save writes a `.dib` back (diff it: sections in order, `#!meta` header, no duplicated selector lines); Convert → `name.nb.md` beside it, original untouched, second Convert refuses to overwrite.
- [ ] VS Code, **Polyglot installed**: opening a `.dib` asks which extension; *Open With… → Configure default* sticks.
- [ ] `#!fsharp` and `#!kql` sections became F# / KQL cells; `#!value` stayed a cell under its own tag and survived a save.
- [ ] `.ipynb` with ClrKernel's kernelspec opened by the Jupyter extension → the prompt appears; one with another kernelspec → no prompt; `ClrKernel: Convert Notebook to .nb.md` works on any open notebook.
- [ ] `clrkernel convert x.dib` and `x.ipynb` on Windows paths; `-o` elsewhere; refuses overwrite.
- [ ] Studio (§O): both formats open as cells, save back as themselves (an `.ipynb` loses stored outputs on save — by design, the banner says so), the banner's Convert button writes the `.nb.md` and navigates; on `test` branch the banner says copy to your branch first.

## L. PowerShell, shell, remoting, HTTP, Mermaid, Python

- [ ] `samples\PowerShell.nb.md`: state persists, `Write-Host` captured, terminating error fails the cell, completion of cmdlets and session variables.
- [ ] `samples\Shell.nb.md`: on Windows `#!bash` needs a bash — Git Bash is on PATH after `winget install Git.Git`; record which shell ran and that cwd/env persist; `#!zsh` is expected to fail with a clear "not found".
- [ ] **WinRM PSRemoting to localhost** (carried from the last checklist, still never green on a real box): elevated `Enable-PSRemoting -Force`; `CLRKERNEL_SECRET_PWSH_LOCALWINRM` user variable; `#!pwsh-connect --name localwin --host localhost --winrm --user <user> --secret pwsh:localwinrm`; `#!pwsh --connection localwin` + `hostname` and state across cells. Remove the variable after.
- [ ] `samples\HttpRequests.nb.md`, `samples\MermaidDiagrams.nb.md` (renders offline, follows theme).
- [ ] `samples\Python.nb.md` — the **open** item from the last checklist: with no `python3` on PATH does the kernel provision one under `%LOCALAPPDATA%\clrkernel\python` (uv + CPython) on this VM? Record the download path, time, and whether `#!python-install pandas` then works. If the VM has corporate TLS interception, record the exact failure.

## M. Jupyter

`scripts\install-dev-kernel.sh` is bash. On Windows write the kernelspec by hand:

```powershell
dotnet build src\ClrKernel\ClrKernel.csproj -c Release
$dll = (Resolve-Path src\ClrKernel\bin\Release\net8.0\ClrKernel.dll).Path
$stage = Join-Path $env:TEMP "clrkernel-dev"; New-Item -Force -ItemType Directory $stage | Out-Null
@{ argv = @((Get-Command dotnet).Source, $dll, "jupyter", "{connection_file}"); display_name = "ClrKernel (dev build)"; language = "csharp" } | ConvertTo-Json | Set-Content (Join-Path $stage "kernel.json")
jupyter kernelspec install $stage --user --name clrkernel-dev
```

- [ ] `jupyter nbconvert --to notebook --execute test\notebooks\smoke.ipynb --ExecutePreprocessor.kernel_name=clrkernel-dev` passes; `smoke-fail.ipynb` exits non-zero; no orphaned `ClrKernel` process after (`Get-Process ClrKernel`).
- [ ] `python test\tools\jupyter_completion_test.py` passes.
- [ ] The released tool as a kernelspec (`clrkernel jupyter` in argv) works the same.

## N. The two RPC surfaces

```powershell
python test\tools\server_harness.py src\ClrKernel\bin\Release\net8.0\ClrKernel.dll
python test\tools\lsp_harness.py src\ClrKernel\bin\Release\net8.0\ClrKernel.dll
```

- [ ] Both pass. Record any check that fails identically on macOS per HANDOFF-19 ("known pre-existing") versus one that fails only here.
- [ ] `initialize` / `capabilities.experimental.clrkernel.languages` list includes `fsharp` and `kql`.

## O. Studio on Windows

Follow `docs\studio.md`. Use a throwaway workspace, not the repo. Start it from
the tree so fixes are testable:

```powershell
dotnet build src\ClrKernel -c Release
$kernel = (Resolve-Path src\ClrKernel\bin\Release\net8.0\ClrKernel.exe).Path
mkdir C:\verify\nb, C:\verify\data
Copy-Item samples\*.nb.md, samples\*.ipynb, samples\*.yaml C:\verify\nb\
clrkernel-studio git init --notebooks C:\verify\nb --data-dir C:\verify\data
clrkernel-studio serve --notebooks C:\verify\nb --data-dir C:\verify\data --store sqlite --clrkernel $kernel
```

Then the same with `--store sqlserver --connection-string "<CLRKERNEL_STUDIO_TEST_SQLSERVER>"`.

- [ ] First-run sign-in with a passkey (Windows Hello) creates the admin; a second user via invite; sign out / in.
- [ ] Files: tree, Contents / History tabs, the commit page (graph column, `MM/dd/yyyy hh:mm AM/PM`, changed-files pane with deleted-strikethrough, side-by-side diff on a file), breadcrumb order `ClrKernel Studio / Files / Project▾ / branch▾ / file`.
- [ ] Editor: Normal / Focus, run a `.nb.md` on the warm kernel, Source / History / Diff vs test, autosave, rename, move out of scratch.
- [ ] **Publish dialog**: blocking jobs-file problems listed with file + line; stage a subset of files; publish → `test` has them.
- [ ] Promotion gate: a green run on test is required; a changed `NuGet.Config` above the notebook re-requires a run (HANDOFF-27).
- [ ] Jobs: a `*.jobs.yaml` scheduling `Sql.nb.md` against the server (connection in the project's `connections.json`, integrated); a manual run; live per-cell progress; the artifact `output.ipynb` and `run.log`; notifications (webhook to a `python -m http.server` or similar; SMTP if you have one).
- [ ] **Secrets per branch (HANDOFF-25/28)**: set `DEMO` on your branch, use it from a cell, set a second secret while the kernel runs → the reply says the session restarted and the next run sees it; `Console.WriteLine(secret)` shows `***` in the cell output, the artifact, and `run.log`; prod does not see test's value.
- [ ] **Rename a user** (the Windows 500 from 0.13.0): Settings → rename → their branch and worktree move, the editor still opens, no `git rev-list` error. Rename to an existing name is refused. Kill Studio half way through a rename and restart: it completes.
- [ ] Connections page: SqlServer (integrated **and** a SQL login whose password lands in **Credential Manager → Windows Credentials** as `ClrKernel:<ref>` — look at it in the UI), AnalysisServices, Odbc; the tree, Select Top 1000, scripting, SQL completion from the live schema.
- [ ] `.dib` / `.ipynb` banner and convert (§K).
- [ ] F# notebook runs in Studio (§I) and its cells complete.
- [ ] Dark mode, thumbnails on Contents, the `read-only` state on someone else's branch.
- [ ] Browser checks: `python test\tools\studio_ui_test.py --list`, then run it all. It builds the tree kernel itself. Record every failing check with its message; one that only fails on Windows paths is a finding.
- [ ] Screenshots: `python test\tools\studio_screenshots.py --no-database --only editor-normal,editor-convert` produces images (do not commit them).
- [ ] Windows service / long-running: leave `serve` up through a scheduled run overnight if the VM allows; record memory of `ClrKernel.Studio` and any kernel process before and after.

## P. Headless

- [ ] `clrkernel run samples\hello.nb.md -o out.ipynb` → exit 0, outputs present. `-p name=value` parameters land as the injected cell.
- [ ] `clrkernel run` of `Sql.nb.md` (connections.json beside it) reaches the server.
- [ ] `clrkernel-studio run <job> --env test` from the workspace above.
- [ ] Exit codes: a failing cell → 1; the cells after it are written unexecuted.

## Q. Secret masking end-to-end (HANDOFF-28)

- [ ] A secret stored via the SQL wizard (Credential Manager) and one via `CLRKERNEL_SECRET_X` (user variable, VS Code restarted): `Console.WriteLine`, a trailing string, `throw new Exception(secret)`, `#!pwsh Write-Host $env:CLRKERNEL_SECRET_X`, and `#!bash env` all show `***` in VS Code, in `clrkernel run` output, and in Studio's artifact.
- [ ] A 7-character secret is **not** masked (by design, `MinimumLength` 8) — record that you saw it.
- [ ] The `.ipynb` VS Code saves after running (Jupyter extension path) holds `***`, not the value.

## R. Results file format

`docs/internal/windows-0.14-results.md`:

```markdown
# Windows verification — 0.14.0 / 0.11.0 — <date>
VM: <Windows version, CPU/RAM>; SDKs: <dotnet --list-sdks>; SQL: <@@VERSION one line>; SSAS: <version>
Extension: 0.11.0; Polyglot: <version or none>; VS Code: <version>

## A. Clone, build, unit suite
- [x] Format clean … (counts)
- [ ] FAIL Multi-target net10.0 — `dotnet test … -f net10.0` → <exact error>; concluded: …; branch windows/… (or: reported only)
…

## Findings (bugs, ordered by severity)
1. <title> — section, repro, evidence, fix branch or none
## Open questions
## Sign-off
Sections complete: A B C … ; skipped: H(Postgres/Oracle), …
```

Commit the results file on a branch `windows/0.14-results` and push it; do not
open a PR — the person reading it will.
