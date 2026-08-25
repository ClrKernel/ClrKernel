# Feature: Projects, per-user branches, and the promotion pipeline (ClrKernel.Jobs)

## Roadmap

These notes are several features wearing one coat. Handing them over together
will produce something half-wired. Phases 1–4 are the core and are specified in
detail below; phases 5+ are the follow-ons, sketched at the end of this document
so the earlier work doesn't paint them into a corner.

1. **Projects** — multiple repos registered in the app, with a project switcher.
   Everything else needs this container to exist first.
2. **Branch model** — rename `dev` → `test`, establish `test` → `prod` promotion.
   Single shared branch still, no per-user anything.
3. **Project roles + per-user dev branches** — worktree per user, ownership
   rules, push to test.
4. **Autosave + commit-on-push** — the editing ergonomics.
5. **Review and approval flow** — see Later phases.
6. **Conflict resolution UI** — see Later phases.
7. **History, blame, and cherry-pick** — see Later phases.
8. **Finer-grained permissions** — see Later phases.
9. **Repo edge cases** (multiple remotes, submodules, LFS) — see Later phases.

The rest of this document is organized by topic, not by phase.

## Terminology

- **Project** — one git repo, one on-disk root, its own jobs, notebooks, and
  branch set. One repo per project.
- **Branches per project**: `prod` (production, default name configurable — some
  repos use `main`), `test` (what the current `dev` becomes), and one user branch
  per editing user, named `user/<username>`.
- **Promotion chain**: `user/<name>` → `test` → `prod`. A user branch can never
  promote straight to `prod`; it has to land in `test` and run there first.
- `test` and `prod` are **never editable in the app**. All editing happens in your
  own user branch. This is the biggest behavioral change from today, where `dev`
  is edited in place. Non-editable is not the same as non-runnable — see
  **Running in test and prod** below.

## On-disk layout

Use **git worktrees**, one per branch that needs a checkout:

```
<data-root>/projects/<project-slug>/
  repo/                    # the clone (or bare repo + worktrees)
  worktrees/
    prod/
    test/
    user-<userId>/
```

This is the load-bearing decision. Without a worktree per user, autosave and
branch switching collide constantly — one person's unsaved edits block another's
checkout, and a scheduled run in `test` picks up whatever happens to be in the
working tree. Separate worktrees make each user's uncommitted state genuinely
independent.

- Create a user worktree **lazily**, on first edit in that project, not at
  account creation. Most users won't touch most projects.
- Prune worktrees after configurable inactivity (default 30 days). **Refuse to
  prune a worktree with uncommitted changes** unless an admin confirms.
- Scheduled and production jobs execute only from the `test` and `prod`
  worktrees. Never run a scheduled job out of a user worktree.

## Remote modes

Configurable per project, with an install-level default (config file or launch
flag). Three modes:

- **`local`** — no remote. The server's repo is the only copy. Push/fetch are
  disabled and their UI is hidden, not shown-and-failing.
- **`server-authoritative`** — a remote exists and the server pushes `test` and
  `prod` to it, but the server's copy wins. Pushes may use
  `--force-with-lease`; never bare `--force`.
- **`remote-authoritative`** — the remote is the source of truth. Fetch before
  every promote and merge; a non-fast-forward push fails loudly and tells the
  user to update rather than being force-resolved.

Also per project:

- `pushUserBranches` (default **false**) — whether `user/*` branches reach the
  remote at all. Most installs won't want dozens of personal branches on a shared
  remote.
- Credentials (PAT or SSH key). Store encrypted at rest, write-only from the UI —
  once entered, never render the value back, only a "configured / not configured"
  state and a replace action.

## Roles

Two independent tiers.

**Server roles** (from the auth spec) apply to the account and to server-wide
concerns — user management, install settings, registering projects.

**Project roles** are grants on one specific project:

- **Project Admin** — everything within that project: edit and run their own
  branch, push to `test`, promote `test` → `prod`, configure the project's remote
  and branch names, manage the project's members, prune worktrees.
- **Project Member** — owns a user branch in that project: edit it, run it, push
  it to `test`. Can view everything else in the project read-only.
- **Project Viewer** — read-only across the whole project. No branch of their own.

### How the two tiers compose

A user's **effective role on a project is the higher of** what their server role
implies and any explicit project grant:

- **Server Admin** → implicit Project Admin on every project.
- **Server Viewer** → implicit Project Viewer on every project.
- An explicit project grant can raise that, never lower it. A Server Viewer who is
  granted Project Member on Project X is a Member there and a Viewer everywhere
  else.

**A gap worth closing now**: as the auth spec stands, every account is either
Server Admin or Server Viewer, and Server Viewer implies read access to *every*
project. That makes per-project grants nearly pointless — nothing is ever private
to a project. Add a baseline server role — **Server User** — that implies no
project access at all. Then:

- Server User → sees only the projects they've been explicitly granted.
- Server Viewer → the "read everything" role, useful for auditors, not the default.
- Server Admin → unchanged.

Projects a user has no access to should not be enumerable — they don't appear in
the project switcher and their IDs 404 rather than 403.

| Action (within one project) | Viewer | Member | Admin |
|---|---|---|---|
| View notebooks, jobs, run history | ✓ | ✓ | ✓ |
| View any branch, including others' user branches | ✓ | ✓ | ✓ |
| Edit and run own user branch | | ✓ | ✓ |
| Push own branch → `test` | | ✓ | ✓ |
| Run cells / Run All in `test` | | ✓ | ✓ |
| Run cells / Run All in `prod` (manual) | | | ✓ |
| Edit anything in `test` or `prod` | | | |
| Promote `test` → `prod` | | | ✓ |
| Configure remote, credentials, branch names | | | ✓ |
| Manage this project's members | | | ✓ |
| Prune or delete another user's worktree/branch | | | ✓ |
| Edit another user's branch | | | |

**Nobody edits another user's branch** — Project Admin and Server Admin included.
An admin can delete or prune a stale user branch, but not write into it. The
invariant stays simple and there's no "who changed my notebook" mystery.

Project membership is managed by that project's Admins and by Server Admins.
A project cannot be left with zero Project Admins — block the last removal or
demotion, the same way the auth spec protects the last Server Admin.

Enforce all of this server-side on every route. Hidden buttons are not a
permission model.

## Autosave

- Debounced write of the editor buffer to the file in the user's own worktree —
  ~800ms idle, plus on blur, on cell run, and on navigating away. **This is a
  file write, not a commit.**
- Write to a temp file and atomically rename, so a crash mid-write can't corrupt
  a notebook.
- The current `Saved` button becomes a **status**: Saved / Saving… / Unsaved /
  Save failed. Keep `Ctrl+S` as a manual flush for peace of mind.
- Autosave applies only to your own branch. `test` and `prod` views are read-only,
  so there's nothing to autosave there.
- A run already in flight uses the file as it was when the run started; autosave
  during a run doesn't retroactively change it.

## Push to test

The "commit" moment. User clicks **Push to test**, types a message, and the app:

1. Commits the working tree in their worktree with that message, authored as them.
2. Checks whether `test` has moved since their branch diverged.
3. Fast-forwards `test` if possible; otherwise creates a merge commit.
4. Pushes, if the remote mode calls for it.

**Conflicts are the part that will bite.** In this phase, keep the handling
mechanical and leave the real merge UI to phase 6:

- If `test` has diverged, **refuse the push** and offer **Update from test**,
  which merges `test` into the user's branch inside their own worktree.
- If that merge conflicts, list the conflicted files and let the user resolve them
  in the editor with conflict markers, then retry. The notebooks being markdown
  means the markers sit in readable text and Monaco's existing diff editor already
  renders the before/after usefully.
- Do not attempt automatic conflict resolution. Do not silently take one side.

## Promotion to prod

- Existing rule stands, restated per project: every job in the notebook must have
  run successfully **in `test`** before `test` can promote to `prod`. The current
  "Not promotable yet" panel keeps its shape and lists the blockers.
- Project Admins only.
- Promotion is a merge of `test` into `prod` plus a push in the two remote modes
  that have one.

## Running in test and prod

Read-only means **you cannot change the code**, not that you cannot execute it.
Splitting write from execute is deliberate: when a scheduled job fails at 2am on
something transient, or fails at cell 7 of 12 and needs the rest driven by hand,
the fix is to run — not to edit production.

- **Who**: Project Members and Project Admins can run in `test`. Only Project
  Admins can run in `prod`. (Members need `test` runs because the promotion gate
  depends on them; nothing about production needs a Member's hand on it.)
- **What's available**: individual cell run, Run All, and Restart kernel. The
  editor is present but `readOnly: true`, and the cell language selector, add/
  delete cell, and Save are absent.

### Guard rails

1. **Autosave is disabled by worktree, not by UI state.** The client must not
   have a save path for `test`/`prod`, *and* the server must reject any write
   targeting those worktrees from every role. Two independent checks — a stray
   autosave into the prod worktree would leave uncommitted changes in production
   that nobody knows about.
2. **No edit-then-run.** If a cell needs changing, the change belongs in a user
   branch. Provide a **Copy to my branch** action that opens the same notebook on
   the caller's branch at the current `test`/`prod` state, so the instinct to
   "just tweak this one line" has somewhere legitimate to go.
3. **Parameter overrides instead of edits.** Most "I need to tweak it first"
   moments are really "I need to run it for a different date/tenant." Add a
   **Run with parameters** dialog that overrides the values in the parameters cell
   for that execution only, in memory, without touching the file. This is what
   keeps rule 2 from being annoying enough to route around.
4. **Concurrency lock** per (project, branch, notebook). A manual run must not
   overlap a scheduled run of the same notebook — refuse with a clear message, and
   warn if a scheduled run is due within a few minutes.
5. **Separate kernel session.** Manual runs in `test`/`prod` get their own kernel
   session, disposed afterward, so hand-run state never leaks into the next
   scheduled execution.
6. **Confirmation for prod.** Running in `prod` prompts once, naming the project,
   branch, and notebook. Production side effects are real even when the file is
   unchanged.

### Audit

The auth spec defers an audit log. Narrow that: **every manual run in `test` or
`prod` is recorded** — who, when, which cells, parameter overrides used, and the
outcome. This is the one place where "who ran that against production?" is a
question someone will actually ask.

### Does a manual run satisfy the promotion gate?

It has to be answered explicitly or the gate becomes meaningless:

- A manual **Run All** in `test` that succeeds **counts** toward promotability.
- **Individual cell runs do not.** Otherwise someone can hand-run cells one at a
  time until everything shows green while the job has never worked end to end.
- Record the run mode (`scheduled` / `manual-all` / `manual-cell`) on the run
  history row so the gate can distinguish, and show it in the history UI.

### UI

- Viewing `test` or `prod` shows a persistent banner: read-only, runnable, which
  branch. Not just an absence of Save.
- Give the `prod` toolbar a distinct treatment — a warning-toned border or badge
  using the status tokens — so nobody misreads which branch they're executing
  against. This is the one place the design brief's "color is semantic" rule earns
  its keep.

## UI

- **Project switcher** in the breadcrumb, as its own dropdown at the root:
  `MyProject › Notebooks › demo.nb.md`. The rail is icon-only and can't carry it.
- **Branch indicator and switcher** next to the file name in the breadcrumb, e.g.
  `… › demo.nb.md [user/jeremy ▾]`. The page toolbar is already dense; don't add
  it there.
- Branch switcher lists: your branch, `test`, `prod`, and other users' branches
  under a "Read-only" group.
- **Diff tab becomes a diff target selector**, rendered with Monaco's diff editor
  (`monaco.editor.createDiffEditor`) against the two markdown texts. Three
  comparisons matter now: yours vs `test` (what you'd push), `test` vs `prod`
  (what you'd promote), and yours vs `prod`. The single "Diff vs production" tab
  no longer covers it.
- **Projects page** under Settings: register a project (Server Admin) — name,
  slug, repo URL or local path, remote mode, credentials, branch names for test
  and prod. Per-project configuration, member management, worktree status, and
  prune are available to that project's Admins too.
- When viewing another user's branch, make the read-only state obvious at the
  breadcrumb, not just by absent buttons.

## Migration from today

- The existing repo becomes project #1. Its `dev` branch is renamed `test`,
  locally and on the remote if one is configured.
- **Job run history rows referencing `dev` must be migrated too.** The
  promotability check asks "has this job run in `dev`?" — if those rows keep the
  old branch name, every notebook becomes un-promotable the moment the rename
  lands, with no obvious cause. This is the most likely way the migration goes
  wrong quietly.
- Existing users get a `user/<name>` branch cut from `test` on first edit, not up
  front.
- Anything else storing a branch name — schedules, channel configs, saved links —
  needs the same sweep.

## Later phases

Sketches, not specs — enough that phases 1–4 don't foreclose them. Each gets its
own write-up when it comes up.

**One Monaco caveat that shapes phases 5 and 6**: the standalone `monaco-editor`
package ships a **two-way** diff editor. VS Code's three-way merge editor is not
part of it. Anything merge-shaped has to be assembled from diff editors plus an
editable result model — don't plan around an API that isn't in the package.

### Phase 5 — Review and approval flow

Push to test becomes a reviewable request rather than an immediate merge, per
project (`requireReviewForTest`, default off so phase 3 behavior is preserved).

- The request shows the Monaco diff editor, yours vs `test`.
- Inline comments anchored to lines: Monaco view zones for the comment threads,
  decorations for the gutter markers. This is the same mechanism VS Code's own
  review UI uses, and it's available in standalone Monaco.
- Approve merges; request-changes bounces it back with the thread intact.
- A Project Admin can always self-merge their own request.

### Phase 6 — Conflict resolution UI

Replace "resolve the markers by hand" with a real merge view:

- Three read-only Monaco models — base, yours, theirs — plus one editable result
  model. Two side-by-side diff editors (base↔yours, base↔theirs) above the
  editable result.
- Per-conflict actions rendered as Monaco **CodeLens** entries above each hunk:
  Accept mine / Accept theirs / Accept both / Edit.
- Decorations to color conflict regions and a "N conflicts remaining" counter that
  gates the Resolve button.

### Phase 7 — History, blame, and cherry-pick

- Commit history per notebook, with any two commits diffable in the Monaco diff
  editor.
- Blame as a gutter decoration — author and commit on hover, click to open that
  commit's diff.
- Cherry-pick a single commit between user branches, previewing the change in the
  diff editor before applying.
- "Restore this version" from a history entry, which writes into your branch as an
  ordinary edit rather than a git operation.

### Phase 8 — Finer-grained permissions

Per-notebook or per-folder grants within a project, for the case where one project
holds work that not every member should see. Deliberately deferred: it multiplies
the permission checks in every route, and project-level grants cover most of it.

### Phase 9 — Repo edge cases

Multiple remotes per project, submodules, and git LFS. All three are real but none
are needed by the workflow described here; each is worth adding only when an actual
repo demands it, since each adds failure modes to clone, fetch, and promote.

## Still out of scope

- Rebasing. Merge only. Rewriting history under a UI that autosaves is a bad
  combination, and the promotion chain doesn't need a linear history.

## Acceptance criteria

- [ ] Multiple projects can be registered; switching projects switches notebooks,
      jobs, and branch list.
- [ ] Each project independently configures its remote mode; `local` projects show
      no push/fetch affordances at all.
- [ ] `test` and `prod` are non-editable for every role, including Server Admin —
      the save endpoint rejects writes to those worktrees regardless of caller.
- [ ] A Project Admin can run cells and Run All in `test` and `prod`; a Project
      Member can do so in `test` only; a Project Viewer in neither.
- [ ] Run with parameters overrides the parameters cell for one execution without
      modifying the file on disk.
- [ ] A manual run cannot overlap a scheduled run of the same notebook.
- [ ] Manual runs in `test`/`prod` use a disposable kernel session and are recorded
      with actor, cells, parameters, and outcome.
- [ ] A successful manual Run All in `test` satisfies the promotion gate;
      individual cell runs do not.
- [ ] A Project Member editing in project A gets a worktree created on first edit,
      and their uncommitted changes are invisible to other users' worktrees.
- [ ] User B can view user A's branch and cannot write to it — including by
      calling the save endpoint directly (403).
- [ ] Neither a Project Admin nor a Server Admin can write to another user's branch.
- [ ] A Server User with no grant on project A cannot enumerate it; its ID 404s.
- [ ] An explicit project grant raises a user's access on that project only.
- [ ] A project cannot be left with zero Project Admins.
- [ ] Autosave persists edits without committing; the diff against `test` reflects
      unsaved-but-written state.
- [ ] Push to test commits with the user's message and merges into `test`.
- [ ] A diverged `test` blocks the push and offers Update from test; conflicts are
      surfaced per file, never auto-resolved.
- [ ] Promotion to prod remains blocked until all jobs have run in `test`, and is
      restricted to Project Admins.
- [ ] After migration, previously-promotable notebooks are still promotable — run
      history survived the `dev` → `test` rename.
- [ ] Scheduled jobs never execute from a user worktree.