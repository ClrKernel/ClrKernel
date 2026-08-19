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
progress, the rendered notebook, and the log.

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

The history itself goes wherever you point it:

```bash
clrkernel-jobs serve --store sqlite                     # default, zero config
clrkernel-jobs serve --store files                      # no database at all
clrkernel-jobs serve --store postgres   --connection-string "Host=…;Database=clrkernel_jobs;…"
clrkernel-jobs serve --store sqlserver  --connection-string "Server=…;Database=clrkernel_jobs;…"
```

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
`stats`, and `channels`.

Set `--api-key <key>` (or `CLRKERNEL_JOBS_APIKEY`) to require an `X-Api-Key` header
on `/api/*`; `/api/health` stays open for probes. With no key configured the server
binds localhost only — if you widen `--urls`, set a key.

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

## Not there yet

Slack and Teams native channels, host/event-log notifications, OIDC sign-in,
server-sent events instead of polling, schedule backfill, and rendering
script-driven outputs (Mermaid) in the run view. Notebooks that emit them still run
fine — the run view falls back to their text form.
