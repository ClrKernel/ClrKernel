# Feature: Jobs as files, and the Dashboard as the operational surface

## Summary

Two changes that together move ClrKernel Studio from "a notebook editor with a
jobs page bolted on" to "an editor for everything in the repo, plus one place to
watch it all run."

1. **The Jobs rail destination goes away.** Job definitions (`.yml`) are
   ordinary files in the **Files** route, edited, autosaved, pushed to `test`, and
   promoted exactly like `.nb.md` notebooks.
2. **The Dashboard becomes the operational surface**, with three views —
   **Overview**, **Monitoring**, **Notifications** — built around a unified,
   cross-project run grid with Project as the first column.

## Verified against the code — 2026-08-27

Read before estimating: three items here are already built, two claims in this
document are wrong, and two blockers change the shape of the work.

**`.yml` in this document means `*.jobs.yaml`.** Discovery globs that suffix
(`JobCatalog.cs:105`) and the distinct extension is what lets the Files tree tell
a job definition from any other YAML once the route shows every file. No rename.

### Already built

- **The evidence rule proposed under "Double Check" exists.**
  `Promotion.CheckAsync` blocks when *either* the notebook or the job's yaml
  changed since the green run's commit —
  `UnchangedBetween(latest.CommitSha, test, notebookPath, job.SourceFileRelative)`
  — and also blocks on ad-hoc overrides, a dirty tree, and in-flight runs.
  Editing a schedule does not sail through a stale gate today.
- `Run` records `CommitSha` and `WasDirty` — what exact-version rerun needs.
- `RunQuery` already carries Project, Environment, JobName, Status, Limit and
  **Offset**, so server-side paging is half-built.

### Wrong in this document

- **There is no run-mode field — and it turned out not to need one.** `Run.Trigger`
  is `Manual | Schedule | Dependency | Retry`, and that already *is* the run mode:
  `scheduled` is `Trigger.Schedule`, `manual-all` is `Trigger.Manual`, and
  `manual-cell` is not in `runs` at all — it is a `ManualRun`, kept in its own
  table so nothing can mistake it for promotion evidence. A `RunMode` column would
  have restated the first two and still not covered the third. **Built without
  one.**

  The migration the grid did need was **the actor**: `Run` recorded no one, so
  "manual, by whom" and the actor filter were both unanswerable. `actor_id` /
  `actor_name`, across all three providers.
- **`.yml` is not read-only — it is unreachable.** `Files.tsx` renders a jobs row
  as plain text with no link. Opened by URL, the editor already gives it Source,
  autosave, Push and Promote. The missing piece is the tree and the route, not the
  editor.

### Blockers — both resolved

> **Resolved.** A jobs file is now named for the notebook it schedules
> (`etl.jobs.yaml` ↔ `etl.nb.md`), the per-job `notebook:` is gone, and the
> promotion unit is the pair — promoted together, from either half. `isDeletion`
> is per file, so deleting a jobs file unschedules a notebook that survives.
> Gating is by what changed: a notebook edit needs a green run at the current sha,
> a schedule edit needs the file to be structurally sound. Parameters count as a
> notebook edit — they are inputs to it.

1. **One `.yml` can name several notebooks.** `JobsFile.Flatten` takes
   `entry.Notebook ?? sharedNotebook`, so each job entry may point at its own.
   Promotion's unit is *the notebook plus its yamls*, so "promote this `.yml`" is
   not expressible: promoting one file can carry jobs for notebooks that were
   never in the unit and have no green run. Either the unit becomes a transitive
   closure over files, or a `.yml` is constrained to one notebook.
2. **Promoting a deleted `.yml` is refused, not merely unimplemented.** Delete the
   yaml while the notebook stays and `isDeletion` is false (it keys on the
   *notebook* being gone), `testJobs` is empty, and the gate answers "No jobs are
   defined for this notebook in test — nothing proves it works." Permanently
   blocked.

### Also missing

Files shows only notebooks and `*.jobs.yaml` (`NotebookTree` filters); no
`monaco-yaml`, no published schema anywhere, no server-side YAML validation; no
rerun endpoint (`runJob` starts a fresh run by name, not a rerun of a recorded run
at a sha) — **done**: `POST /api/runs/rerun`; no run-history retention; notification rules live inside each job's
yaml and no delivery is recorded. In the Monitoring grid, project visibility is
filtered **after** the query (`runs.Where(visible.ContainsKey)`), so cross-project
paging returns short pages — that has to move into the query before paging can be
correct. **Done:** `RunQuery.Projects` is a `required` member, so a route that
forgets to say whose history it is asking for does not compile.

### Decision: `test` schedules

Answered 2026-08-27 — the scheduler's environment filter gains `test`
(`SchedulerService.cs:165`), so user branches still never schedule and `test` and
`prod` both do.

**A scheduled test run counts as promotion evidence, exactly like a manual one.**
`Promotion.CheckAsync` takes the *latest* test run and does not look at `Trigger`;
requiring a manual run would mean a passing scheduled run replaced a green manual
one as "latest" and *blocked* promotion, which is worse than either alternative.
The guarantees that matter are already the other checks — clean tree, no
overrides, files unchanged since that commit — and none of them is about who
pressed the button. Revisit only if someone wants "a human watched this" to be
part of the gate, which it is not today.

Two consequences to plan for: test jobs consume kernel slots continuously against
the same `maxParallelism`, and every job's `notify:` rules begin firing from test
as well as prod, roughly doubling notification volume before the Notifications
work lands.

## Files route

The route show list all files/folders for the repo/project selected from a notebook list to a **file browser over the project repo**, currently it shows minimum `.nb.md` and `.yml` (which is unreachable — no link in the tree; see Verified).

The editor should be only thing that varies by extension:

- `.nb.md` → the notebook editor, with Normal and Focus modes. (Current implementation)
- `.yml` → Overview (port current form based) | YAML (a Monaco YAML editor) | Diff from produciton.
- Anything else → plain Monaco, read-only or editable as the role allows.

Everything wrapping the editor — breadcrumb, branch switcher, page toolbar, diff
tab, Saved status, Push to test, Promote — is shared chrome if possible.

## Editing job in "Overview" or wizard

> **Done:** the Overview tab. It is a form over the file's *text* — every edit
> goes through `jobsFile.ts` and comes back as YAML, so autosave, diff, push and
> promote are unchanged and a comment you wrote survives a checkbox. `parameters:`
> and `notify:` are named on each card and left to the YAML tab rather than given
> a half-editor.
>
> **Done:** the cron pills, the field highlighting and the wizard. One note on the
> second: an `<input>` cannot highlight inside itself without an overlay, and an
> overlay over a text box is a reliable way to end up a pixel out at some font
> size — so the help line under the field picks out the name of the field the
> caret is in, which answers the same question.

 - use current Overview forms intially, but want to add following:
    - Cron scheduler helper text should have:
    - put pills around next runs in cron,
    - highlight in text editor what section you're on (minute hour day-of-month month day-of-week)
    - Wizard button, that pops up interactive cron schedule helper, choose Daily, weekly, monthly interval drop down, then that would change next text boxes which is start time, repeating days, end, etc.

## Editing job in YAML view

- ~~Use **`monaco-yaml`**~~ — **it does not work with this Monaco.** It reaches the
  editor through `monaco-worker-manager`, which calls
  `createWebWorker({ moduleId, label })`; monaco-editor 0.56 replaced that with
  `{ worker }`, so the worker is never created and every request fails with
  "Missing requestHandler". 5.5.1 is the latest release and its `>=0.36` peer
  range is simply wrong. Completion is instead a small provider driven by the
  published schema, and errors come from the server — which builds that same
  schema and is the authority the push gate uses anyway. What is lost is that
  errors arrive on save rather than on keystroke, about a second behind.
- **Validate server-side too, on save and on push.** The client schema is
  convenience, not enforcement.
- Autosaving syntactically invalid YAML on a user branch is fine — it's just a
  file mid-edit. **Pushing invalid YAML to `test` is not.** Gate the push on
  server-side validation with the error surfaced inline in the editor.

### Double Check if The promotion gate needs to account for changed job files

> **Answered: it already does.** See Verified. What is missing is an entry point
> from a `.yml`, not the rule — and that runs into blockers 1 and 2.

Today's gate is "every job in this notebook has run successfully in `test`." Once
the `.yml` is itself a promotable file, a job definition can change while the
notebook it runs doesn't. Does that mean the old successful run no longer proves anything?

One possible fix: Record the **commit sha of both the notebook and the job definition** on each run
history row, and have the gate require a successful `test` run at the *current*
`test` sha for the files being promoted. Without this, editing a schedule or
changing a job's parameters sails through a gate that was satisfied by a run of
the previous definition.

## Dashboard

### Overview

A summary that links into Monitoring rather than duplicating it: what's running
right now, recent failures, upcoming scheduled runs, per-project success rate over
a chosen window. Scoped to the projects the viewer can access.

### Monitoring

The unified grid. **Project is the first column.**

Suggested columns: Project, Job, File, Branch (`test` / `prod` / user branch),
Status, Trigger (scheduled / manual, with the actor), Run mode (`scheduled` /
`manual-all` / `manual-cell` — the field the break-glass work already added),
Started, Duration.

- **Filtering and sorting are server-side.** Run history grows without bound;
  client-side sorting works until it very suddenly doesn't. Paginate on the server
  and push filters into the query.
- Filters: project, status, branch, date range, job/file, actor. Sort on any
  column.
- Row expands to output and logs, with a link to the file at that commit.
- Rows are scoped by project access — a user never sees a run from a project they
  can't reach.
- Pair this with a **retention policy** for run history and stored output, or the
  table and the disk both grow forever. *(Done: `runRetentionDays`, off by
  default, removing rows and artifact folders together. A job's most recent run
  and anything unfinished are never removed — the first because the promotion
  gate reads it.)*

### Rerunning failures

- Single rerun from a row, and bulk rerun of selected failed rows.
- **Which version reruns?** Default to the **current HEAD of that branch** — after
  a fix, that's what you want. Offer an explicit "rerun the exact failed version"
  using the recorded sha, for reproducing a failure. Getting this wrong silently
  is worse than either choice.
- Rerun obeys the existing rules: role check (rerunning in `prod` is Project Admin
  only), the per-notebook concurrency lock, and the audit record.
- Confirmation names the branch and the number of runs.
- **Throttle bulk reruns** — queue them with a concurrency limit rather than
  firing fifty executions at a database at once.

### Notifications

Draw a clear line against the existing **Channels** section, or you'll end up with
two homes for the same idea:

- **Channels** = *where* things get sent — the destination and its credentials.
- **Notifications** = *when* things get sent (rules binding events to channels)
  plus a feed of *what was sent*.

Rules cover at least: job failed, job recovered after failing, run exceeded a
duration threshold, scheduled run missed, promotion to `prod` happened. Scope
rules per project, and decide whether subscriptions are per user or per project —
per project is simpler and probably right for the first pass.

## Rail changes

> **Done.** The rail is Dashboard, Files, Connections, Channels, Settings. `/jobs`
> is gone rather than redirected, as asked. A job opens as the file that defines
> it (Overview tab) and its history is the monitoring grid filtered to it; **Run
> now**, **Cancel run** and that link moved onto the job's card. `dependsOn` moved
> onto the card too — it is one value per job, which is the card's rule.
> `parameters:` and `notify:` stay on the YAML tab.

- Remove **Jobs**.
- Resulting rail: Dashboard, Files, Connections, Channels, Settings(at bottom).

Just remove `/jobs` dont be converned with redirects since this is still not published to prod.

## Acceptance criteria

- [x] Job definitions are edited as `.yml` files in Files, with autosave, diff,
      Push to test, and Promote working identically to notebooks. *(Overview |
      YAML | Diff, both editing the same text buffer.)*
- [x] Branch, worktree, autosave, and promotion logic is implemented once at the
      file level — not duplicated in the notebook and YAML editors. *(It already
      was: the editor page is keyed on the path, and only the Notebook tab is
      notebook-specific.)*
- [x] YAML editing offers schema-driven completion and inline errors, from a schema
      published by the kernel. *(`GET /api/jobs/schema`, built from the same
      `JobsSchema` the server validates with. Completion is ours, not
      `monaco-yaml` — see below. Errors arrive on save rather than on keystroke.)*
- [x] Invalid YAML can be autosaved on a user branch but cannot be pushed to `test`.
- [x] User branches never schedule anything; `test` and `prod` do. *(One named
      predicate, `SchedulerService.Schedules`, so the rule can be tested rather
      than read out of a `Where` clause.)*
- [x] Promoting a changed job definition requires a successful `test` run at the
      current sha, not a stale run of the previous definition. **Already true** —
      but only reachable notebook-first; see blockers 1 and 2.
- [x] Promoting a deleted `.yml` stops its schedule, naming each schedule it
      switches off and when it would next have fired.
- [x] The Monitoring grid shows Project first, filters and sorts server-side, and
      shows only accessible projects. *(Scoping moved **into** the query, which is
      what makes a page of fifty fifty rows. Sorting is a whitelist, and duration
      is not on it: it is `FinishedAt - StartedAt`, null in flight, and subtracted
      differently by each provider.)*
- [x] Rerun defaults to branch HEAD, offers exact-version rerun, respects role and
      concurrency rules, and is audited. *(Exact-version checks the whole tree out
      at that commit, and is refused when the recording would not be faithful — no
      sha, a dirty tree, or ad-hoc parameters that were not kept. The audit is the
      run row: `causedByRunId` + actor + the commit it actually ran. The prod-admin
      rule is now shared with the plain run button, which previously needed only
      Project Member.)*
- [x] Bulk rerun is throttled. *(By machinery that was already there:
      `SchedulerService.Launch` waits on the same `MaxParallelism` semaphore every
      scheduled run does. The route caps one request at 100 rows; it does not
      queue, because the scheduler already does.)*
- [ ] Notifications configures rules and shows a delivery feed; Channels remains
      destinations only.