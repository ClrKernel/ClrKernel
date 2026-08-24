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
var env = "test";
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
progress, the rendered notebook, and the log. With the git workflow on, test notebooks
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

Everything the UI does is available over HTTP under `/api`. Server-wide:
`health`, `projects` (GET, plus POST/PUT/DELETE and `projects/{slug}/init` for
Server Admins), `jobs` (every project's, each carrying the project it belongs
to), `runs` with per-cell progress, `runs/{id}/artifact` and `/log`, `stats`, and
`channels` (GET/PUT, plus `channels/{name}/test`).

Anything that reads or writes notebooks names a project and a branch:
`/api/projects/{project}/branches/{branch}/…`, where `{branch}` is `test`, `prod`,
or **`mine`** — your own. There is no spelling that reaches somebody else's branch,
which is what makes "nobody edits another user's branch" a property of the routes
rather than a check to remember. A slug nobody registered answers
**404, not 403** — a project you have no access to is meant to be
indistinguishable from one that does not exist. Under that prefix: `jobs`
(including create/update/delete, which edit the yaml files), `jobs/{name}/run` and
`/cancel`, and `notebooks/content` (GET any branch, PUT test only). With the git
workflow on it also carries `notebooks/cells` (the same file parsed into cells, and
written back from them — the browser never needs its own copy of the `.nb.md`
format), `notebooks/promotion` and `notebooks/promote`, plus the editor's session
endpoints: `notebooks/session` (POST to start, DELETE to restart), `notebooks/run`,
and `notebooks/session/status`. `/api/projects/{project}/git/diff` returns a unified
diff for one path, which is still the convenient thing over curl, and
`/api/projects/{project}/branch` (plus `/branch/push` and `/branch/update`) is where
your own branch stands and how it reaches test.

A run can take one-off parameters that override the job's own for that run only —
the `*.jobs.yaml` is untouched. The same thing is behind "Run with parameters…" in
the job editor:

```bash
curl -X POST http://localhost:5000/api/projects/default/branches/test/jobs/nightly-us/run \
  -H 'Content-Type: application/json' \
  -d '{"parameters": {"region": "eu", "backfillDate": "2026-08-01"}}'
```

## Accounts and passkeys

Everything needs a signed-in account. Credentials are **passkeys** — there are no
passwords, and no password to leak or reset.

The first person to reach a server with no accounts gets `/setup`, registers a
passkey, and becomes its **Server Admin**. That page only accepts requests from the
machine the server runs on, and it stops existing — 404, not a message — the moment
an account exists. Everyone after that joins through an invite the admin creates
under *Settings → Users*: pick a role, copy the link, send it however you like. There
is no email in this system. Invites are single-use and expire after seven days
(`--invite-days`).

### Roles

Two tiers. **Server roles** are what an account is across the whole server:

| | Server Admin | Server Viewer | Server User |
|---|---|---|---|
| Manage accounts, invites, settings, channels | yes | **no** | **no** |
| Register and forget projects | yes | **no** | **no** |
| Sees, by default | every project | every project, read-only | **nothing** |

**Server User is the one to hand out.** An account that can read every project makes
per-project grants pointless — nothing is ever private to a project — so Server
Viewer is the auditor's role rather than the default, and a new invite starts at
Server User.

**Project roles** are grants on one project, set under *Settings → Projects →
Members* by that project's admins or by a Server Admin:

| Within one project | Viewer | Member | Admin |
|---|---|---|---|
| Read notebooks, jobs, runs, output | yes | yes | yes |
| Edit and save on your own branch | **no** | yes | yes |
| Run cells, Run All, restart the kernel | **no** | yes | yes |
| Push your branch to `test` | **no** | yes | yes |
| Promote to production | **no** | **no** | yes |
| Configure the project, manage its members | **no** | **no** | yes |

A grant is the **higher** of the two: a Server Admin is an admin of every project
whether granted or not, and an explicit grant raises someone's access on that one
project and never lowers it. A project cannot be left with no Project Admin of its
own.

A project you have no access to is **invisible, not refused**: it is absent from the
switcher and from every list, and its id answers **404, not 403** — otherwise the
name of every project on the server leaks to anyone willing to guess.

None of this is enforced by hiding buttons. Running a cell is arbitrary code
execution on the machine hosting the server, so calling
`…/branches/mine/notebooks/run` directly without the role returns 403. Writing to
`test` or `prod` is refused for **every** role including Server Admin — that check is
on the branch, not on who is asking, so there is no account that could satisfy it. A viewer's
editor is read-only, and Focus Mode still works — it is a reading layout too.

### Before anyone else signs in: set the domain

A passkey is bound to a **relying party id**, which is a domain, and a credential
**cannot be moved between domains**. Anything registered against the default
`localhost` stops working the day the server answers to a real hostname, and everyone
re-registers. WebAuthn also refuses any origin that is not HTTPS or `localhost`, so a
multi-user server means a hostname and TLS.

```bash
clrkernel-jobs serve --rp-id jobs.example.internal \
  --origins https://jobs.example.internal --urls http://0.0.0.0:5000
```

`--rp-id` (`CLRKERNEL_JOBS_RPID`) is the domain; `--origins`
(`CLRKERNEL_JOBS_ORIGINS`) lists the origins the browser may present and defaults to
`--urls`, which is what you override when TLS is terminated by a proxy in front.

While the relying party is the default `localhost`, **any loopback origin is
accepted** whatever its port. That is what makes `./dev/jobs-dev.sh` work: the page
comes from Vite on :5173 while the server listens on :5000, and a relying party is a
domain — the port is not part of it, and the browser scopes the credential the same
way. Set `--rp-id` to a real hostname and this stops applying; every origin then has
to be listed.

### Locked out

Self-hosted with no email means a lost device is otherwise permanent. On the box:

```bash
clrkernel-jobs new-admin-invite --data-dir /var/lib/clrkernel-jobs
```

That prints one single-use Server Admin invite. Anyone with a shell there could do
worse already, so this is not a new exposure.

**There is no API key any more, and no machine-callable credential.** Passkeys are
interactive by definition; if you need a script to drive `/api`, that needs per-user
API tokens, which do not exist yet.

## Projects

A **project** is one repo, one folder on disk, and its own notebooks, jobs and
branches. A server that has never registered one still has exactly one — the folder
`--notebooks` points at, named after that folder, with the slug `default`. That slug
is not cosmetic: it is what every run row written before projects existed already
says, so history keeps answering after the upgrade with nothing rewritten.

Register more from **Settings → Projects** (Server Admins only): a name and an
absolute path to a folder already on the server. If the project uses the workflow
and its folder is not a workspace yet, the same page offers to make it one —
the same thing `clrkernel-jobs git init` does, adopting whatever is already there.
Registering by cloning a repo url is not there yet; put the clone on the server
first and point at it.

The project you are looking at is the first thing in the breadcrumb, and switching
there switches the notebooks, jobs, runs and branches under it. Anything with a
link of its own carries its project in the URL — `/jobs/finance/test/nightly` —
because two projects may each have a job called `nightly`, and a link that meant
whichever one you had selected would mean two different jobs.

**Forgetting** a project unregisters it and touches nothing on disk: the repo, the
worktrees and the run history all stay, and registering the same folder under the
same slug brings all of it back.

The file behind all of this is `projects.json` in the data directory, and you can
write it by hand:

```json
[
  { "slug": "default", "name": "Notebooks", "root": "/srv/notebooks", "gitEnabled": true },
  { "slug": "finance", "name": "Finance",   "root": "/srv/finance",   "gitEnabled": true,
    "remoteMode": "ServerAuthoritative", "remote": "origin", "remoteSecret": "FINANCE_GIT_TOKEN" }
]
```

Once the file exists it is the list, and `--notebooks` no longer decides. A
project's **slug and folder are fixed** once registered — the slug is written into
every run row and the folder is where the history those rows describe happened.
Everything else is editable. Two projects may not overlap on disk: both would find
the same `*.jobs.yaml` and schedule each job twice.

Each
project keeps its own worktrees, its own promotion gate, and its own run history —
two projects may each have a job called `nightly` and they never collide, because
the project is part of every key.

`remoteSecret` is the **name** of a secret, never a credential: it resolves at use
time from the OS credential store or `CLRKERNEL_SECRET_*`, the same rule every
connection in this repo follows. Nothing writes a token to this file.

## Test → prod with git

Opt in with one command on your notebooks folder (stop `serve` first):

```bash
clrkernel-jobs git init --notebooks ./notebooks
```

The folder becomes a **workspace**: a bare repo at `.repo.git` and two folders backed
by branches — `test/` (branch `test`, what has been pushed and is ready to prove
itself) and `prod/` (branch `main`, what the scheduler runs). Existing notebooks are
adopted into test and promoted, so
everything keeps working. `gitEnabled: true` is written to settings.json.

> **Upgrading from 0.9**, where the editable branch was called `dev`: the first start
> renames the branch and the worktree in place and rewrites the run history to match.
> Nothing is copied and no commits move. A configured remote keeps its old `dev`
> branch — delete it there yourself when you are ready; a shared remote is not this
> process's to prune.

Nobody edits `test` or `prod`. Each person gets a **branch and worktree of their
own** — `user/<account id>`, checked out at `user-<id>/` beside `test/` and `prod/`
— created the first time they edit something in that project. Most people never
touch most projects, so keeping an empty checkout per person per project against
that possibility is not worth the disk.

The loop:

1. **Edit** in the web UI (see [The notebook editor](#the-notebook-editor), where you
   can also run cells against a warm kernel) or in your own editor inside your
   `user-<id>/` folder. There is nothing to press: the editor **writes as you
   work**, about a second after you stop typing, and again when you leave the tab,
   run a cell, or navigate away. That is a **file write, not a commit** — nobody
   else sees it and nothing runs from it. `Saved` is a status, not a button:
   *Saved*, *Unsaved*, *Saving…*, or *Save failed* — the last of which is the only
   one you can click, and it retries. `⌘S` / `Ctrl+S` writes now rather than in a
   moment.
2. **Push to test** from the editor's toolbar. That is the commit: everything you
   have saved becomes one commit on `test`, under a message you write, authored as
   you. If `test` has moved since you branched, the push is refused and the button
   becomes **Update from test** — the merge belongs in your own worktree where you
   can look at it. Conflicts come back as a list of files with the markers left in
   them; nothing is ever auto-resolved.
3. **Run** the notebook's jobs in test — manually or via the API. Test jobs never run
   on a schedule; cron and chaining fire only in prod. Each run records the test
   commit it executed and whether the tree was dirty.
4. **Promote** from the editor page. The button unlocks only when *every* enabled
   job on the notebook has a latest test run that succeeded, as written (no ad-hoc
   parameter overrides, no uncommitted content), with the files unchanged since that
   run — and only if the promotion would leave prod's dependency graph valid.
   Blocked promotions list every reason. Promotion is one commit on `main` naming
   the evidence runs; the prod scheduler picks it up on its next tick.

Deleting a notebook in test is promotable the same way (it removes the files and the
jobs from prod). Promotion carries the notebook **and** its jobs files as a unit —
sibling jobs share the notebook, so nothing smaller would be honest.

Every write lands atomically — into a file beside the target, then renamed over
it. The editor writes every few seconds, so "crashed halfway through writing" stops
being a thought experiment, and half a notebook is not a notebook. If a crash does
leave a staging file behind, a push will not carry it into test.

To mirror the workspace elsewhere, set a push remote (Settings → Git workflow, or
`--git-push-remote`). Pushes are best-effort after each commit/promotion — a failing
remote never blocks a promotion, but the last push status shows in `/api/health`.
Credentials come from the environment (ssh agent, token in the url); the server
stores none.

Notes: `notifications.yaml` and `settings.json` stay at the workspace root,
unversioned — they are runtime config. Environments are part of run history keys, so
test and prod runs of the same job never mix. In Docker, mount `/notebooks` writable
(owned by uid 1654) when git is enabled; worktree paths are repaired automatically
when the volume is mounted at a different path.

## Getting around

Navigation is a fixed 48px icon rail on the left — Dashboard, Jobs, Notebooks,
Channels, and Settings at the foot — with the label on hover. The bar across the top is
a context strip and nothing else: a breadcrumb saying where you are, a search box, and
the theme picker. What you can *do* lives on the page, not in the chrome.

The search box filters what is in front of you — the run table on the Dashboard, the
job table on Jobs — and it keeps the query in the URL as `?q=`, so a filtered view is
something you can bookmark or paste to someone else. It only appears on those two
pages; elsewhere there is nothing for it to filter.

The theme picker offers five accents on one shared neutral base — green (the default),
blue, violet, amber, rose. Only the accent changes, so the app does not look like five
different apps, and the choice is remembered in your browser. Environment and run-status
colours never follow it: `prod` is green and a failure is red whichever accent you pick.
Light only for now.

Your own account — display name, registered passkeys, and the way out — is under
*Settings → Your account*. Add a passkey per device: a laptop and a phone means
losing either is an inconvenience rather than a lockout, which is why the last one
cannot be removed.

## The notebook editor

Test notebooks get an **edit** link in the tree, and the editor carries a file explorer
of its own down the left: the notebook tree for one environment, with the file you have
open highlighted. Drag its right edge to resize it, or collapse it to a thin strip and
click that strip to bring it back — the width and the collapsed state are remembered
across notebooks.

The editor is a notebook, not a text box: each cell is a Monaco editor with syntax highlighting, a language picker fed by
whatever the kernel declares (so a `#!sql` cell highlights as SQL and a shell cell as
shell), and controls to add, delete and reorder cells. A **Source** tab shows the raw
file when you want to see exactly what is on disk, and **Diff vs production** shows
what promoting would ship, side by side.

Everything the page can do is on one toolbar row: the tabs on the left, then the kernel
status, the **Normal | Focus** switch, **Run All**, **Restart kernel**, **Save** and
**Promote to production**. It stays put while you scroll, and it sheds labels rather
than wrapping when the window is narrow — below about 1024px the execution controls
fold into a single menu. The execution controls belong to the Notebook tab and are
hidden on Source and Diff; saving and promoting are about the document and stay
everywhere.

Two ⓘ buttons sit in that row. One beside **Save** explains what saving does — every
save writes to your own branch, and cells you run here never count towards
promotion. The other appears beside **Promote to production** when promotion is
blocked, and gives the reasons: usually a job on this notebook that has not had a green
run yet. Either opens a notice in the corner that fades on its own or closes on
**Dismiss**. Both used to be permanent banners, one above the notebook and one below
it; neither changes while you work, so both cost a strip of the screen to repeat
themselves every time you scrolled past.

**Source** and **Diff vs production** fill the window and scroll inside themselves, so
a long file does not push the toolbar off the top.

Cells run against a **warm kernel** — one per notebook, started on the first run and
kept alive so variables persist between cells and between runs, exactly as they do in
VS Code. Per cell: ▶ runs it, **▶ above** runs everything before it, **▶ below** runs
it and everything after; the toolbar adds **Run All** and **Restart kernel**.

**Focus Mode** gives one cell the window — its editor above, its output below, with the
notebook's contents as a tree on the left — for when a notebook is long enough that
scrolling to find a cell is the slow part. **Normal** is the usual scrolling list of
cells. The switch is per notebook and is remembered.

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

Cells have **IntelliSense**: completion, hover, signature help, live diagnostics, and
Go to / Peek Definition, answered by the notebook's own kernel. A `#!sql` cell is
syntax-checked as you type and the squiggle clears when you fix it. **Peek Definition**
(⌥F12, or right-click) shows a definition inline without leaving the cell — including
decompiled source for framework symbols, so peeking `Console` shows `System.Console`
with its documentation. **Go to Definition** (F12) jumps to the cell a symbol was defined
in; on a framework symbol, where there is no cell to jump to and no editor tab to open one
in, it shows the same peek instead of doing nothing. It sees the live session, so a variable a cell defined and ran is
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

Saving writes to your own branch, so a save that changes nothing is skipped: a needless commit
would invalidate the "unchanged since that run" half of the promotion check.

Execution is refused unless you are a Server Admin, the git workflow is on, the file is
in your own branch, and the path resolves inside your worktree. Reading and diffing still
work for everyone; only running and saving are gated.

## Docker

```bash
docker build -f src/ClrKernel.Jobs/Dockerfile -t clrkernel-jobs .

docker run -p 5000:5000 \
  -v "$PWD/notebooks:/notebooks:ro" \
  -v clrkernel-jobs-data:/data \
  -e CLRKERNEL_JOBS_RPID=jobs.example.internal \
  -e CLRKERNEL_JOBS_ORIGINS=https://jobs.example.internal \
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
| `--rp-id <domain>` | `CLRKERNEL_JOBS_RPID` | `localhost` |
| `--origins <url;url>` | `CLRKERNEL_JOBS_ORIGINS` | the value of `--urls` |
| `--invite-days <n>` | `CLRKERNEL_JOBS_INVITE_DAYS` | 7 |
| `--session-days <n>` | `CLRKERNEL_JOBS_SESSION_DAYS` | 14 |
| `--max-parallelism <n>` | `CLRKERNEL_JOBS_MAX_PARALLELISM` | 4 |

## Settings

The **Settings** page shows every server setting with where it came from (flag, env
var, settings.json, or default). Web-editable values persist to settings.json;
anything pinned by a flag or environment variable is locked in the UI. Security and
execution settings (passkey domain, kernel path, store, connection string, roots) are
host-only by design — a browser can never change what the server executes or lock
you out.

## Not there yet

Slack and Teams native channels, host/event-log notifications, OIDC sign-in,
server-sent events instead of polling, schedule backfill, and rendering
script-driven outputs (Mermaid) in the run view. Notebooks that emit them still run
fine — the run view falls back to their text form.
