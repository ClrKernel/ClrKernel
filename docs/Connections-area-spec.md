# Feature: Connections area (ClrKernel.Jobs)

## Summary

A new top-level section for database work, modeled on SSMS / the VS Code mssql
extension: saved connections in a browsable tree on the left, a query editor and
results grid on the right. It is also the single store that notebook SQL cells
draw from — not a second, parallel list of connections.

New destination in the left rail (`Database` icon), sitting alongside Dashboard,
Jobs, Notebooks, and Channels.

## Layout

Left tree, right work area split horizontally: query editor on top, results
below, draggable divider between them, each independently scrollable.

This is the same split the notebook Focus Mode uses. **Reuse that component**
rather than building a second resizable-pane implementation — same splitter, same
persistence of the ratio, same `editor.layout()` handling on drag.

## Ownership and scoping

Two scopes, shown as separate groups in the tree:

- **Shared** — server-wide, created and managed by Server Admins, visible to
  everyone who can reach the Connections area.
- **Mine** — private to one user, created by that user, invisible to everyone
  else including Server Admins.

Connections are **not** project-scoped, which creates one interaction worth
handling explicitly:

> A notebook is committed to git and runs for other people and for the scheduler.
> If a SQL cell references a **private** connection, it will fail for everyone
> else and every scheduled run, with a confusing error.

So: notebooks reference connections by stable id/name, and **a notebook that
references a private connection is flagged** — a warning while editing on a user
branch, and a promotion blocker in the same panel as the existing "Not promotable
yet" checks. Committed work resolves to shared connections only.

## Credentials

- Reuse the same encrypted secret store as the project git credentials. Secrets
  are write-only from the UI: once saved, the app reports "configured" and offers
  replace, never the value.
- Support whatever auth modes each provider offers — SQL login, integrated auth,
  Entra ID, raw connection string paste.
- Offer a "don't store the password, prompt each session" option for connections
  people don't want persisted.

**One thing to make visible in the UI**: integrated auth uses the *server
process's* identity, not the signed-in browser user's. Everyone sharing an
integrated-auth connection is acting as the service account, and the database's
audit trail will say so. Label it plainly on the connection form.

## Providers

Connection types come from whatever providers ClrKernel already registers — don't
hardcode SQL Server.

Metadata browsing is the part that doesn't generalize cleanly. Define a small
per-provider metadata contract (list databases → schemas → objects → columns,
plus "script object definition"), and let the tree **degrade** when a provider
doesn't implement a level rather than showing empty folders. Not every engine has
schemas; not every one exposes procedures.

## Execution permissions

Answer chosen: admins unrestricted, members read-only. Concretely:

- **Shared connections**: Server Admins run anything. Everyone else is read-only.
- **Private connections**: governed by the database login itself, not by the app.
  It's the user's own credential against a server they could reach with SSMS
  anyway — the app isn't the security boundary there. Add an install-level switch
  to force read-only for non-admins on private connections too, for installs that
  want it.

**Do not enforce read-only by parsing SQL.** Statement inspection loses to
`EXEC sp_whatever`, `SELECT … INTO`, CTEs ending in `INSERT`, and dynamic SQL.
Real enforcement comes from the database:

- Let a connection definition carry an **optional second, least-privilege
  credential** used whenever a non-admin executes against it. That's the actual
  boundary.
- Keep any app-side statement check as defense-in-depth and a clearer error
  message, not as the mechanism. If no read-only credential is configured, say so
  on the connection and disable execution for non-admins rather than pretending.

## Browsing

- Tree: connection → databases → schemas → Tables / Views / Programmability →
  columns, keys, indexes. Lazy-load each level; cache per session with an explicit
  Refresh.
- Object context actions: Select Top N, script the definition, copy qualified
  name, refresh subtree.
- Filter box that narrows the loaded tree — large databases make an unfiltered
  tree unusable fast.
- Connected/disconnected state visible per connection, with explicit Connect and
  Disconnect.

## Query editor and results

- Monaco with the SQL language, sharing the notebook cells' editor configuration
  and theme.
- Execute on `Ctrl+Enter` / `F5`. **If there's a selection, execute only the
  selection** — SSMS muscle memory, and people will assume it.
- Cancel a running query, and show elapsed time while it runs.
- Results: virtualized grid, client-side sort, copy with headers, CSV export.
  Multiple result sets become tabs. A Messages tab carries row counts, errors, and
  `PRINT`-style output.
- **Cap rows by default** (10k or so) with a visible "showing first N" notice, and
  a per-connection query timeout. A `SELECT *` against a fact table shouldn't be
  able to take down the server or the browser tab.
- Server-side connection pooling with an idle timeout. Don't hold a session open
  per browser tab forever.

## Notebook interop

- SQL cells pick their connection from this same list; the `Connect` control on a
  SQL cell opens the same picker.
- **Open in notebook** action on the query editor, to move a scratch query into a
  notebook cell on the caller's branch.
- The private-connection warning described above.

## Audit

Record executions against shared connections — who, when, which connection, and
the statement — consistent with the manual-run audit in the branches spec. This is
the same "who ran that against production?" question in a different costume.

## Later phases (need to document, then ask when done with above to continue with these)

- **Schema-aware IntelliSense** — feed the cached metadata into a Monaco
  completion provider so table and column names complete in both the query editor
  and notebook SQL cells. This is the highest-value follow-on and the reason the
  metadata cache is worth building properly now.
- **Query history** per user, re-runnable from a panel.
- **Saved queries**, shared or private, mirroring the connection scopes.
- **Execution plans**, where the provider can produce them.
- **ER / relationship view** for a selected set of tables.
---

# Decisions (resolved 2026-08-24)

Answers to the questions the spec left open, plus the constraints in the existing
code that forced them. Recorded here rather than re-derived later.

## 1. Credentials — the server writes to the OS store, and says so when it can't

There is no encrypted secret store to reuse. Project git credentials are a *reference*
(`Project.RemoteSecret`) resolved at use time from the OS credential store or
`CLRKERNEL_SECRET_*`; nothing in Jobs has ever accepted a secret value.

So the Connections form takes a password, and the server writes it into the OS store
(`SecretStore` — keychain / libsecret / Windows Credential Manager) under a generated
reference. `connections.json` still holds only `{ "secret": "<ref>" }`, which keeps the
repo invariant intact: a password is never in a notebook or in config.

Where no OS store exists — the Jobs Docker image, where `EnvironmentSecretProvider.CanStore`
is `false` — the password field is replaced by a secret-reference field and a plain message
saying the value has to be set as `CLRKERNEL_SECRET_*`. Degrade visibly; never pretend to
have saved something.

## 2. Execution — Jobs opens the connection itself

`Cancel a running query` decides this. Neither kernel RPC surface can interrupt a running
cell: `Execute` takes no `CancellationToken` and `clrkernel/restart` only drops a dictionary
entry, so through a kernel "Cancel" means "kill the process and lose the session". Pooling is
an in-process ADO.NET feature, and no row cap exists anywhere in `Language.Sql`.

`ClrKernel.Jobs` therefore references `ClrKernel.Database` and
`ClrKernel.Database.Provider.SqlServer`, deliberately breaking the csproj's "the kernel itself
is NOT referenced" boundary for this one area. `SqlCommand.Cancel()` is the cancel,
`SqlConnectionSpec.BuildConnectionString(SecretStore)` is the connection, and ADO.NET's own
pool is the pool.

The cost, stated plainly: the `#r`-only providers (Oracle, ODBC, JDBC) cannot be **queried or
browsed** from this area, because they are loaded into a kernel session and not into this
process. They can still be **saved** — the store keeps any `$type` the descriptors declare —
so a notebook can reference an Oracle connection by name even though the Connections area
will not open it. The tree shows those connections with querying disabled rather than hiding
them.

## 3. Storage — one server-wide store, committed into every worktree

`ConnectionConfig.FindFiles` walks *up* from the notebook's directory and stops at the first
directory holding a candidate; `Project.Root` is arbitrary per project, so there is no single
directory above every worktree. "Committed to the repo" therefore means one `connections.json`
per project repo, written from one server-wide store in the data dir.

- **Shared** connections are written and committed by the jobs actor into **every** worktree
  of every project — test, prod, and each user branch — with identical content in one pass,
  so no branch diverges and nothing is ever left dirty. Every worktree, not just test and prod,
  because of one line in `ConnectionConfig.FindFiles`:

  ```csharp
  if (found.Count > 0) { return found; }   // base OR local — either one stops the walk
  ```

  A user worktree holding only `connections.local.json` returns *just that file* and never
  walks up to a shared base elsewhere. So anyone with a single private connection would lose
  every shared one. The base file has to sit in the same directory as the overlay. Do not
  narrow this back to test and prod. This is the server writing to test and
  prod outside the branch workflow, which the UI otherwise calls read-only. It is deliberate:
  a saved connection is live for the scheduler immediately, and a Server Admin who is not a
  member of a project can still manage its connections. The write is path-scoped, so no other
  file's promotion evidence moves.
- **Private** connections are never committed. A file on a user branch is readable by every
  ProjectViewer and is pushed to the remote when `PushUserBranches` is on — the opposite of
  "invisible to everyone else including Server Admins". They are materialized as
  `connections.local.json` in that user's own worktree only, which
  `ConnectionConfig.FindFiles` already reads as an overlay over the base file in the same
  directory.
- `connections.local.json` must be in a managed `.gitignore`. An untracked file makes
  `GitService`'s unscoped `git status --porcelain` report the worktree `Dirty`, which
  permanently blocks worktree pruning (`GitService.cs:497,518`).

`ConnectionConfig.Upsert` is the writer; `SqlConnectionConfig.ToProperties` / `FromNode` are
the SQL Server mapping. Neither is new code.

## 4. Phase 1 scope — landed

Store, Shared/Mine scoping, the rail destination, the metadata tree, the query editor, the
results grid, and the audit. All of it is in, over four commits; the one thing not yet done is
running `ConnectionsLiveTest` against a real SQL Server, which is what covers the object-tree
queries and the reader loop.

**Materialization is phase 2, with its consumer.** Nothing in phase 1 reads `connections.json`
off disk — the area reads the store directly. The only thing that needs a materialized file in
a worktree is a notebook resolving `#!sql-connect --name warehouse`, and notebook interop is
phase 2. Writing and committing across every worktree of every project is the riskiest code in
the feature; it waits until something depends on it.

Phase 2 is then: materialization, the SQL cell picker replacing the inline wizard,
"Open in notebook", the private-connection warning and the promotion blocker.

**The prune check has been run, and it decided the shape.** `Merged` is
`git merge-base --is-ancestor <branch> test`, so a server commit on a personal branch makes
that branch permanently unprunable and falsely "ahead". So the server does **not** write into
personal worktrees. Shared connections are committed in **test and prod only**; a personal
branch gets the file the ordinary way, by descending from test or merging it.

Two further things were measured rather than assumed:

- The bare repo's `info/exclude` **is** what a linked worktree reads, so the private overlay
  can be excluded without committing a `.gitignore` and without putting anybody behind. The
  path is asked for with `git rev-parse --git-common-dir` rather than assumed to be the bare
  repo — in this layout they coincide, which is not a thing to depend on. Note `info/exclude`
  is neither versioned nor cloned: somebody who clones the repo to a laptop will see
  `connections.local.json` as untracked there.
- Committing in test leaves personal branches clean and prunable, but **one commit behind**.
  Behind is what `NotebookToolbar` turns into "Update from test" *in place of* the Push button,
  so a shared-connection edit asks everybody on the server to merge before their next push.
  That was put to the user with the alternative and they kept committing, knowingly.

## 5. Execution permission — no read-only credential, no execution

"Shared connections: everyone but a Server Admin is read-only" + "do not enforce read-only by
parsing SQL" + "if no read-only credential is configured, disable execution for non-admins"
compose to: **until an admin configures a second least-privilege credential on a connection,
no non-admin can execute anything against it.** On a fresh install that makes the whole area
admin-only for execution, and that is intended.

So a shared connection carries an optional second credential used whenever a non-admin
executes against it. Without one, the connection reads "read-only execution not configured"
and Execute is disabled for everyone below Server Admin — disabled and explained, never
enabled-and-hoping. The app-side statement check stays as a clearer error message, never as
the mechanism.

Private connections are governed by the database login itself. The install-level switch that
forces the same rule on them is `--private-connections-read-only` /
`CLRKERNEL_JOBS_PRIVATE_READONLY` / `privateConnectionsReadOnly` in `settings.json`, and it is
shown in Settings → Connections. Off by default.

**Disconnect is real, not a UI state.** It clears the ADO.NET pool for that connection string
and drops everything the tree had loaded, so "connected" and "we have its objects" stay one
fact. The pool is keyed by connection string and shared, so one person's Disconnect drops the
pooled sockets for everybody — harmless, since the next query opens a new one, and it is the
only honest meaning the word can have when the connection is pooled rather than held.

## Defaults taken without asking

- **10,000 rows** capped by default with a visible "showing first N" notice; a per-connection
  query timeout defaulting to 30s. The cap is read as `cap + 1` rows — getting the extra one
  proves truncation without a `COUNT`. That is `DisplayTable`'s existing convention
  (`TotalRows = -1` means truncated, remainder uncounted), so the grid and notebook outputs
  say the same thing.
- **Cancel is scoped to the person who started the query.** A query-id registry whose route
  does not check the owner is "stop anyone's query by guessing an id".
- The results grid is **new React**, not `InteractiveTable`. That grid renders server-side HTML
  from `DisplayTable` and has no virtualization, copy-with-headers or CSV; going direct gives
  us JSON rows and the spec wants all three. `palette.test.ts`'s `noLiterals` rule applies to
  it like every other component.
- **Audit** is a new table beside `ManualRun` in the run store — which means a migration for
  each of the three providers (Sqlite, SqlServer, Postgres).
- The area is **not project-scoped**: no project switcher in its breadcrumb, alongside
  Channels and Settings. Routes are `/connections/...`.
- `Splitter` is reused as-is; the results/editor split persists its ratio the way Focus Mode's
  does.
- The existing `ConnectionWizard` inline-directive flow on a SQL cell is **unchanged** in
  phase 1. It composes `#!sql-connect --server … --secret …` inline; the spec wants cells to
  reference saved connections by name instead. That is a behaviour change to a shipped
  component and belongs with the rest of notebook interop in phase 2.
