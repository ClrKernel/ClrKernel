# ClrKernel Jobs

Run your notebooks on a schedule, chain them, and see the results in a browser.

`clrkernel-jobs` is a separate dotnet tool that drives the ClrKernel kernel. A job
points at a notebook (`.nb.md`, `.ipynb`, `.dib`, `.csx`), optionally with a cron
schedule, parameters, and dependencies on other jobs. Every run executes in its own
isolated kernel process, cell by cell, and leaves behind an executed `.ipynb` you
can open in VS Code or Jupyter.

> Preview. The pieces below work and are covered by tests, but the tool has not had
> production soak time yet — treat 0.9.x as "try it on real notebooks and tell us
> what breaks".

## Install

```bash
dotnet tool install --global ClrKernel          # the kernel (required)
dotnet tool install --global ClrKernel.Jobs     # the job runner
```

`clrkernel-jobs` finds `clrkernel` on PATH, then in `~/.dotnet/tools`. Point it
somewhere else with `--clrkernel <path>` if you keep a dev build around.

## Define jobs

Jobs live in `*.jobs.yaml` files **beside the notebooks they run**, so they version
with the notebooks. One file holds any number of jobs — the same notebook can be
scheduled several times with different parameters:

```yaml
notebook: ./nightly.nb.md      # shared by the jobs below; a job may override it
defaults:                      # inherited by every job in this file
  timeoutSeconds: 3600
  retryCount: 1                # one retry, 30s later
  parameters:
    env: prod
  notify:
    onFailure: [ops]           # channel names, see Notifications
jobs:
  - name: nightly-us           # names are unique across the whole tree
    cron: "0 2 * * *"
    parameters: {region: us}   # merges over the defaults
  - name: nightly-eu
    cron: "0 3 * * *"
    parameters: {region: eu}
  - name: rollup
    dependsOn: [nightly-us, nightly-eu]   # no cron: runs when both succeed
```

Check the whole tree before trusting it:

```bash
clrkernel-jobs validate --notebooks ./notebooks
```

That reports duplicate names, missing notebooks, unknown dependencies, dependency
cycles, and invalid cron expressions.

### Parameters

Parameters are injected exactly like `clrkernel run`'s papermill-style ones: a cell
of `var name = value;` declarations is inserted after a cell whose first line is
`// parameters` (or at the top if there is none), so the notebook's own defaults are
overridden by the job's values. Types are inferred — `5` is an `int`, `0.5` a
`double`, `true` a `bool`, everything else a `string`.

```csharp
// parameters
var region = "us";     // overridden per job
var env = "dev";
```

## Run one now

```bash
clrkernel-jobs list                        # what is defined
clrkernel-jobs run nightly-us              # run it, with live per-cell progress
```

```
Running nightly-us (nightly.nb.md)
[1/4] // parameters
[1/4] ok (0.6s)
[2/4] // clrkernel:injected-parameters
[2/4] ok (0.0s)
[3/4] var rows = await SqlServer.Query(…)
[3/4] ok (2.4s)
[4/4] rows.DisplayTable()
[4/4] ok (0.1s)
Succeeded: nightly-us in 3.2s
  artifact: ~/.clrkernel/jobs/artifacts/nightly-us/<run>/output.ipynb
  log:      ~/.clrkernel/jobs/artifacts/nightly-us/<run>/run.log
```

Exit code is 0 on success, 1 when a cell fails. The first failing cell stops the
run; later cells are recorded as skipped and written to the artifact unexecuted.

## Serve the scheduler and web UI

```bash
clrkernel-jobs serve --notebooks ./notebooks
```

Opens <http://localhost:5000> with a dashboard (recent runs and success rate), the
job list and editor, a notebook tree, and a run view showing live cell-by-cell
progress, the rendered notebook, and the log. With the git workflow on, dev notebooks
also get a cell editor that runs cells against a live kernel — see
[The notebook editor](#the-notebook-editor).

### Scheduling rules

- **Cron jobs** fire when their next occurrence falls inside a tick (every 10s).
  Occurrences missed while the scheduler was down are skipped, not backfilled.
- **A job already running skips its next occurrence** rather than piling up.
- **Dependencies** are evaluated on every tick: a job fires when *every* job it
  `dependsOn` has a success newer than that job's own last trigger. So fan-in fires
  exactly once, a failure upstream stops the chain, and re-running the failed job to
  success later resumes it — including a manual `clrkernel-jobs run` from another
  terminal.
- **Cron and manual triggers ignore dependencies.** If you schedule a job, you asked
  for it at that time.
- `--max-parallelism` (default 4) caps concurrent runs. On shutdown, in-flight runs
  are cancelled and their kernels killed; runs left behind by a crash are marked
  failed at the next start.

## Notifications

Channels live in `notifications.yaml` at the notebooks root; jobs reference them by
name. **Passwords and tokens are never stored here** — only a *reference* resolved at
send time from the OS credential store or a `CLRKERNEL_SECRET_*` environment
variable, so this file is safe to commit.

```yaml
channels:
  - name: ops
    type: webhook
    url: https://hooks.example.com/clrkernel
    bearerSecretRef: ops-hook-token        # -> CLRKERNEL_SECRET_OPS_HOOK_TOKEN

  - name: mail
    type: email
    host: smtp.example.com
    port: 587
    startTls: true
    from: jobs@example.com
    to: [oncall@example.com]
    user: jobs@example.com
    passwordSecretRef: smtp-password       # -> CLRKERNEL_SECRET_SMTP_PASSWORD
```

Channels can also be managed from the **Channels** tab in the web UI, which writes
this same file (and validates before it does). Jobs pick their channels with the
**Notify** checkboxes in the job editor.

The webhook payload is JSON: job, notebook, status, success, trigger, attempt,
runId, timings, error, artifactPath. Test a channel without waiting for a failure:

```bash
curl -X POST http://localhost:5000/api/channels/ops/test
```

A channel that fails never fails the run — it is logged and the other channels still
go out — but the test endpoint reports the reason.

## Where things are stored

Run history and artifacts default to `~/.clrkernel/jobs` (`--data-dir`, or
`CLRKERNEL_JOBS_DATA`). Artifacts always live on disk:
`artifacts/<job>/<run-id>/output.ipynb` plus `run.log`.

The history itself goes wherever you point it — and **`serve` requires the choice to
be explicit** (flag, `CLRKERNEL_JOBS_STORE`, or `store` in settings.json; the Docker
image sets `sqlite` via env so it works out of the box). A server that silently
defaulted its store would put your run history somewhere you didn't choose:

```bash
clrkernel-jobs serve --store sqlite                     # zero config
clrkernel-jobs serve --store files                      # no database at all
clrkernel-jobs serve --store postgres   --connection-string "Host=…;Database=clrkernel_jobs;…"
clrkernel-jobs serve --store sqlserver  --connection-string "Server=…;Database=clrkernel_jobs;…"
```

The store is validated **before the port binds**: a missing or wrong connection
string exits with a message naming where each value came from, instead of a server
that answers 500s. Database stores get ~30 seconds of retries first, so
`docker compose up` works even though the database container is slower to start.
One-shot commands (`run`, `list`, `validate`) still default to sqlite.

`files` keeps a self-describing `run.json` beside each run's artifacts, so a run
directory can be archived or copied whole. The relational backends create their
schema on first start.

The history is meant to be queried directly — tables and columns are snake_case and
statuses are stored as their names, so this works as-is in any client:

```sql
select job_name, status, trigger_type, started_at, finished_at
from runs
where status <> 'Succeeded'
order by created_at desc;

select cell_index, status, source_preview
from run_cells
where run_id = '…'
order by cell_index;
```

(The trigger column is `trigger_type` because `trigger` is a reserved word in
T-SQL and would need bracketing.)

## API

Everything the UI does is available over HTTP under `/api` — `health`, `notebooks`,
`jobs` (including create/update/delete, which edit the yaml files), `jobs/{name}/run`
and `/cancel`, `runs` with per-cell progress, `runs/{id}/artifact` and `/log`,
`stats`, and `channels` (GET/PUT, plus `channels/{name}/test`).

With the git workflow on, `envs/{env}/notebooks/` adds `content` (GET any
environment, PUT dev only), `cells` (the same file parsed into cells, and written back
from them — the browser never needs its own copy of the `.nb.md` format), `promotion`
and `promote`, plus the editor's session endpoints: `session` (POST to start, DELETE
to restart), `run`, and `session/status`. `git/diff` returns a unified diff for one
path, which is still the convenient thing over curl.

A run can take one-off parameters that override the job's own for that run only —
the `*.jobs.yaml` is untouched. The same thing is behind "Run with parameters…" in
the job editor:

```bash
curl -X POST http://localhost:5000/api/jobs/nightly-us/run \
  -H 'Content-Type: application/json' \
  -d '{"parameters": {"region": "eu", "backfillDate": "2026-08-01"}}'
```

Set `--api-key <key>` (or `CLRKERNEL_JOBS_APIKEY`) to require an `X-Api-Key` header
on `/api/*`; `/api/health` stays open for probes. With no key configured the server
binds localhost only — if you widen `--urls`, set a key.

## Dev → prod with git

Opt in with one command on your notebooks folder (stop `serve` first):

```bash
clrkernel-jobs git init --notebooks ./notebooks
```

The folder becomes a **workspace**: a bare repo at `.repo.git` and two folders backed
by branches — `dev/` (branch `dev`, where you edit) and `prod/` (branch `main`, what
the scheduler runs). Existing notebooks are adopted into dev and promoted, so
everything keeps working. `gitEnabled: true` is written to settings.json.

The loop:

1. **Edit** in the web UI (dev notebooks get an *edit* link in the tree — see
   [The notebook editor](#the-notebook-editor), where you can also run cells against a
   warm kernel) or in your own editor inside `dev/`. Every UI save is a commit on the
   dev branch.
2. **Run** the notebook's jobs in dev — manually or via the API. Dev jobs never run
   on a schedule; cron and chaining fire only in prod. Each run records the dev
   commit it executed and whether the tree was dirty.
3. **Promote** from the editor page. The button unlocks only when *every* enabled
   job on the notebook has a latest dev run that succeeded, as written (no ad-hoc
   parameter overrides, no uncommitted content), with the files unchanged since that
   run — and only if the promotion would leave prod's dependency graph valid.
   Blocked promotions list every reason. Promotion is one commit on `main` naming
   the evidence runs; the prod scheduler picks it up on its next tick.

Deleting a notebook in dev is promotable the same way (it removes the files and the
jobs from prod). Promotion carries the notebook **and** its jobs files as a unit —
sibling jobs share the notebook, so nothing smaller would be honest.

To mirror the workspace elsewhere, set a push remote (Settings → Git workflow, or
`--git-push-remote`). Pushes are best-effort after each commit/promotion — a failing
remote never blocks a promotion, but the last push status shows in `/api/health`.
Credentials come from the environment (ssh agent, token in the url); the server
stores none.

Notes: `notifications.yaml` and `settings.json` stay at the workspace root,
unversioned — they are runtime config. Environments are part of run history keys, so
dev and prod runs of the same job never mix. In Docker, mount `/notebooks` writable
(owned by uid 1654) when git is enabled; worktree paths are repaired automatically
when the volume is mounted at a different path.

## The notebook editor

Dev notebooks get an **edit** link in the tree. The editor is a notebook, not a text
box: each cell is a Monaco editor with syntax highlighting, a language picker fed by
whatever the kernel declares (so a `#!sql` cell highlights as SQL and a shell cell as
shell), and controls to add, delete and reorder cells. A **Source** tab shows the raw
file when you want to see exactly what is on disk, and **Diff vs production** shows
what promoting would ship, side by side.

Cells run against a **warm kernel** — one per notebook, started on the first run and
kept alive so variables persist between cells and between runs, exactly as they do in
VS Code. Per cell: ▶ runs it, **▶ above** runs everything before it, **▶ below** runs
it and everything after. The toolbar adds **Run All** and **Restart kernel**.

- A run stops at the first failure and marks the rest skipped — the same papermill
  semantics a scheduled run uses, so what you see here predicts what the job will do.
- Output appears as the kernel produces it, rendered exactly as the executed artifact
  will be. Edit a cell and its output dims rather than disappearing: it is still what
  ran, just no longer what the code says.
- Sessions are dropped after **30 minutes idle**, at shutdown, and when a fifth
  notebook needs a slot (four at a time). Restart kills the kernel and starts a fresh
  one — it is also the only way to stop a cell that will not finish, because no kernel
  RPC surface can cancel one mid-flight.
- One cell at a time per notebook. A second run while one is in flight is refused
  rather than queued.
- A scheduled run of the same notebook is fine — it executes the committed file in its
  own kernel, and the editor says so while it is in flight.

Cells have **IntelliSense**: completion, hover, signature help, and live diagnostics,
answered by the notebook's own kernel. A `#!sql` cell is syntax-checked as you type and
the squiggle clears when you fix it. It sees the live session, so a variable a cell defined and ran is
a variable the next cell completes against — and a `#!sql` cell gets SQL completion
rather than C#'s, because the kernel dispatches on the cell's language. Opening a
notebook starts its kernel for this reason; before, a session appeared only on the first
run, which is the wrong way round when the point of completion is that you have not run
anything yet.

The editor's kernel is `clrkernel lsp`, the same server the VS Code extension drives, so
a cell behaves here the way it does there and language features arrive in both at once.
Scheduled runs stay on `clrkernel serve`, which has no language features and does not
need any. Both are fronts for the same engine and take the same code path through it,
but they are two hosts rather than one: a difference in execution behaviour between the
editor and a scheduled run would be a bug in one of them, and is worth reporting as such.

**Runs from the editor are never promotion evidence.** They write nothing to run
history: no run rows, no cell rows, no trigger updates. Nothing you do here can make a
notebook look promotable, and nothing you do here can un-promote one. Promotion still
requires a real green run of every job on the notebook, launched from the Jobs page or
the API. This is a property of the code — the session has no access to the run store —
not a rule someone has to remember.

Saving is a commit on dev, so a save that changes nothing is skipped: a needless commit
would invalidate the "unchanged since that run" half of the promotion check.

Execution is refused unless the git workflow is on, the file is in `dev/`, and the path
resolves inside the dev worktree. It is refused outright when `--urls` reaches beyond
localhost and no `--api-key` is set — running arbitrary code for anyone who can reach
the port is not a default worth having. Editing and diffing still work; only running
is gated.

## Docker

```bash
docker build -f src/ClrKernel.Jobs/Dockerfile -t clrkernel-jobs .

docker run -p 5000:5000 \
  -v "$PWD/notebooks:/notebooks:ro" \
  -v clrkernel-jobs-data:/data \
  -e CLRKERNEL_JOBS_APIKEY=choose-a-key \
  clrkernel-jobs
```

The image carries both the job runner and the matching kernel. Mount your notebooks
at `/notebooks` and a writable volume at `/data`.

It runs as the non-root `app` user (uid 1654). A named volume picks up that
ownership automatically; if you bind-mount a host directory at `/data` instead,
either `chown 1654` it first or pass `--user`.

## Local development

One command brings up both halves with live reload:

```bash
./dev/jobs-dev.sh                    # sample notebooks in dev/notebooks
./dev/jobs-dev.sh ~/my-notebooks     # your own tree
```

Open **<http://localhost:5173>** — the Vite dev server, which proxies `/api` to the
backend, so the whole app works from that one URL. Ctrl+C stops both.

- **Edit a `.tsx`/`.css`** → the browser updates in place (Vite HMR), no reload.
- **Edit a `.cs`** → `dotnet watch` applies it live ("Hot reload succeeded"); edits
  it can't hot-patch restart the API instead. Either way, refresh and it's there.
- **Edit a notebook or a `*.jobs.yaml`** → picked up on the next scheduler tick
  (10s) or the next API request; nothing to restart.

Prefer two terminals? That's all the script does:

```bash
# terminal 1 — API + scheduler, restarts on C# edits
dotnet watch --project src/ClrKernel.Jobs run -- serve \
  --notebooks "$PWD/dev/notebooks" --data-dir "$PWD/dev/data" \
  --clrkernel "$PWD/src/ClrKernel/bin/Debug/net8.0/ClrKernel"

# terminal 2 — the UI, hot reload
npm --prefix src/ClrKernel.Jobs/webapp run dev
```

Point `--clrkernel` at your local build (as above) when you are changing the kernel
too; drop the flag to use the installed `clrkernel` tool.

Useful while iterating:

```bash
npm --prefix src/ClrKernel.Jobs/webapp run test:watch   # renderer unit tests
dotnet test ClrKernel.slnx --filter ClassName~SchedulerTest
rm -rf dev/data                                         # reset run history
```

### Testing the SQL backends

The run-store contract suite covers sqlite and files everywhere. To run the same
tests against real servers, bring them up and point the tests at them:

```bash
docker compose -f dev/docker-compose.dbs.yml up -d postgres sqlserver

CLRKERNEL_TEST_REQUIRE_LIVE=1 \
CLRKERNEL_JOBS_TEST_POSTGRES="Host=localhost;Port=55432;Database=clrkernel_jobs;Username=postgres;Password=devonly" \
CLRKERNEL_JOBS_TEST_SQLSERVER="Server=localhost,51433;Database=clrkernel_jobs;User Id=sa;Password=DevOnly!Passw0rd;TrustServerCertificate=True" \
dotnet test test/ClrKernel.Jobs.UnitTest/ClrKernel.Jobs.UnitTest.csproj --filter ClassName~RunStoreContractTest
```

Without those variables the SQL cases are skipped; `CLRKERNEL_TEST_REQUIRE_LIVE=1`
turns a missing server into a failure, so a run meant to verify them cannot report
success without touching a database. **The databases are scratch** — every test
empties the tables first. CI runs exactly this against service containers.

Two gotchas worth knowing:

- **Paths must be absolute.** `dotnet run --project` sets the app's working
  directory to the *project* folder, so `--notebooks ./dev/notebooks` resolves under
  `src/ClrKernel.Jobs/`. Use `"$PWD/…"`, as the script does.
- **On macOS, `localhost:5000` is also AirPlay Receiver.** The app binds fine, but
  when it is *not* running you get a `403` from AirPlay instead of a connection
  error — which looks like a broken API. Use `API_PORT=5099 ./dev/jobs-dev.sh` (or
  turn AirPlay Receiver off in System Settings → General → AirDrop & Handoff).

## Configuration reference

Every setting takes a CLI flag, an environment variable, or a key in
`settings.json` in the data directory — in that order of precedence.

| Flag | Environment variable | Default |
| --- | --- | --- |
| `--notebooks <dir>` | `CLRKERNEL_JOBS_NOTEBOOKS` | current directory |
| `--data-dir <dir>` | `CLRKERNEL_JOBS_DATA` | `~/.clrkernel/jobs` |
| `--store <kind>` | `CLRKERNEL_JOBS_STORE` | `sqlite` |
| `--connection-string <cs>` | `CLRKERNEL_JOBS_CONNECTION` | — |
| `--clrkernel <path>` | `CLRKERNEL_JOBS_CLRKERNEL` | PATH, then `~/.dotnet/tools` |
| `--urls <urls>` | `CLRKERNEL_JOBS_URLS` | `http://localhost:5000` |
| `--api-key <key>` | `CLRKERNEL_JOBS_APIKEY` | none (localhost only) |
| `--max-parallelism <n>` | `CLRKERNEL_JOBS_MAX_PARALLELISM` | 4 |

## Settings

The **Settings** page shows every server setting with where it came from (flag, env
var, settings.json, or default). Web-editable values persist to settings.json;
anything pinned by a flag or environment variable is locked in the UI. Security and
execution settings (API key, kernel path, store, connection string, roots) are
host-only by design — a browser can never change what the server executes or lock
you out.

## Not there yet

Slack and Teams native channels, host/event-log notifications, OIDC sign-in,
server-sent events instead of polling, schedule backfill, and rendering
script-driven outputs (Mermaid) in the run view. Notebooks that emit them still run
fine — the run view falls back to their text form.
