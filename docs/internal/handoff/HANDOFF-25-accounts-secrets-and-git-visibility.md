# HANDOFF-25 — Accounts have names, secrets have branches, and git says what it is doing

Three arcs, all from the same complaint in different clothes: **the app knew something
and would not say it.** A branch was `user/7f3a…` because a display name is not unique.
A secret was server-wide because `ISecretProvider` has no scope. A merge said "1 file(s)
changed there" because the toolbar only had a count. Each of those was a reasonable
decision that had stopped being one.

## An account has three names, and only one of them has rules

`Id` is immutable and invisible — it is the WebAuthn user handle inside every issued
passkey (`AuthService`, asserted where a ceremony completes), so it can never change.
`DisplayName` is free text, changeable, not unique. `Username` sits between them:
unique, git-safe, and changeable **only by an admin**, because changing it moves a
branch and a directory.

The charset was measured with `git check-ref-format` rather than guessed, and it is
narrower than git's: `-jeremy` is a valid ref and a hostile command-line argument, and
`Jeremy` is the same directory as `jeremy` on macOS and Windows. Stored lower-cased,
indexed on the stored form — the first unique index in the schema.

> **The rename is `git branch -m` plus `git worktree move`, never a folder move.**
> Moving the directory works for everything *inside* it — the `.git` file still names
> the admin directory — so it looks fine until `git worktree list` reports the old path
> and the next `worktree prune` unregisters a live worktree. `MigrateLegacyLayout`
> (dev → test) was the precedent and the shape was copied from it.

Kill the kernel session first. `SessionFor` keys on the absolute path, and a moved
directory orphans it.

## Identities: one account, several ways to prove it

`credentials` *was* the identity table — COSE key, sign count, AAGUID — so AD or OIDC
could not be expressed at all. `identities` is `(provider, subject)` unique, and
sign-in resolves **through it**, not off the credential.

`IAccountProvider` is `Name`, `DisplayName`, `IsConfigured`. What it takes *out* of
`AuthService` is the point: the account half no longer knows what a passkey is. It
creates accounts, links identities, resolves one to a user, issues sessions — and every
step of that is the same for a directory login.

**Deliberately not in it: the provider's routes.** A passkey needs begin/complete pairs
and an OIDC provider needs a redirect and a callback. Guessing that shape from one
implementation would put `MapRoutes` in `AuthApi`'s way for no present gain; the second
provider is what should decide it. `IsConfigured` is `true` unconditionally for
passkeys, which makes it look pointless — it earns its place the first time a provider
can be half-configured.

The heal path is worth keeping: a credential with no identity row gets one written
rather than a refusal. The credential already proves who it is, and the alternative is
locking somebody out of their own server over bookkeeping.

## Secrets are per branch, and `Core.Secrets` never learned about branches

A value lives under `clrkernel-studio:secret:<project>:<branch>:<name>`. When Studio
spawns a kernel for a branch it resolves that branch's secrets and puts them in the
child's environment as `CLRKERNEL_SECRET_*`; the kernel's own `EnvironmentSecretProvider`
answers `Resolve("OPENAI")` unchanged.

Strict scoping falls out for free — the kernel only ever sees what was injected — and
that is the whole reason for the shape. A promoted job whose branch has no key fails
saying so instead of quietly running on test's.

> **`secret_names` exists because a secret store cannot be listed.** `ISecretProvider`
> is get/set/delete and no OS credential store enumerates by service portably. So the
> table holds names, never values, and "set / not set" is answered by trying to resolve
> one. The cost, which belongs in the docs and does: a secret set from a shell does not
> appear until it is also named here.

`secretStore` is `os` | `file` | `auto` rather than a discovery chain, because a server
is not a laptop and "it silently used the environment instead" is a bad way to find out
there is no keyring. `SettingField.Choices` was added for it: the value is read back by
a `switch` that throws on its default case, so free text in a web form is a server that
will not start.

**A trap that nearly shipped:** `ForProviders` builds the chain with
`cacheLocally: false`, so a cache handed *to* it is not a cache — it is an ordinary
provider, first in the list and writable, and it swallows every write before the real
store ever sees one. Passing an `InMemorySecretProvider` there to "speed things up"
produced a store that reported success and persisted nothing. The explicit chains have
no in-memory provider for that reason; reading through is also what a server wants, so
a password changed by the web app is not served stale to a kernel.

### Since: a secret set while a kernel runs (2026-09-10)

Reported as a bug, and it read as one: two secrets on a personal branch, the older
resolved, the newer "not found — looked in memory, keychain, env". The kernel had been
handed the branch's secrets as `CLRKERNEL_SECRET_*` when it started, and a process
cannot be handed another variable later; its own keychain provider uses the kernel's
namespace, not `clrkernel-studio:secret:…`, so it could not see the new one either.
The docs said "restart the kernel". Nobody reads that at the moment it matters.

The secrets routes now `DropUnder` the branch's worktree on set and on delete: every
open notebook on that branch loses its kernel, and its next cell run starts one that
has the value. The reply carries `restarted`, and the Secrets page says how many —
because what it costs is the kernel's variables, and the person will otherwise meet
that as their state being gone. Delete drops too: a kernel that still has the old
value still has the secret.

The browser check that proves it found something else on the way: Studio had been
spawning the *installed* `clrkernel` in every browser check, because nothing passed
`--clrkernel` and `serving()` in the harness had its own `Popen` that `studio()`'s
arguments never reached. See HANDOFF-28.

## Refusals belong on a form, not in a ceremony

An invite now carries the display name **and** the handle, chosen by the admin. Both
used to be typed by whoever opened the link, with the handle derived from the name —
which put every refusal at the worst possible moment, and `users.username` is unique, so
a collision that got past the derive was a 500 with a passkey already created.

Checked three times, because the world moves between issuing an invite and opening it:
on the form, as the ceremony begins, and once more before the row is written. An admin
can still rename somebody onto a reserved handle inside that window.

Invites predating this carry neither name and are **refused** with a sentence saying so.
Not given a derived handle: a second naming path is one nobody notices still firing. The
one place a handle is still derived is first-run setup, which has no admin to ask and
nothing to collide with.

## "Behind test" is about a file; the merge is about the branch

The toolbar had one number — `behind` for the whole branch — and drew it over whatever
file was open, so a file that exists only on your branch, that test has never seen, said
*Update from test*.

`BranchStanding.BehindFiles` is `git diff --name-only mine...test`. **Three dots**: what
test changed since the branches parted, never what you changed. Two dots also lists your
own files and calls them something test moved on.

The branch-level control moved to the explorer, beside the branch picker. The file-level
one stayed in the toolbar. And Push-to-test stopped being *replaced* by Update — it is
drawn disabled with the reason, because a button that is missing answers nothing, and
"why is there no Promote to Test?" was that button being gone.

Promote is on `test` only now. From `mine` it shipped whatever test held, under a label
that read like it was about the file in front of you.

## Reading git

`git log --format=%x01%H%x00%h%x00%an%x00%aI%x00%s%x00%P`. The record separator is
`\x01` and the fields are NUL-separated because **a commit subject is whatever somebody
typed** — a pipe, a tab or a newline in a message is what breaks the obvious delimiters,
and it breaks them by dropping commits rather than failing.

> **`prod` is the environment; `main` is the ref.** Anything handing a branch name to
> git has to translate (`RefFor`), and anything handing it to `PathFor` must not. Without
> that, a prod listing attributes nothing and reads as a folder nobody has ever changed.

Windows git prints worktree paths with forward slashes. That has bitten: a test comparing
a `Path.Combine` result to `git worktree list` could never match there, and its negative
twin passed for the wrong reason. `Trim()` on every parsed line is not defensive, it is
required.

> **The `\r` on the end of each line was ours, not git's.** `TryRunAs` captured stdout
> with `OutputDataReceived`, which hands over lines with their terminators removed, and
> re-joined them with `AppendLine` — so every git command's output came back with its
> line endings rewritten to `Environment.NewLine`, and anything with no trailing newline
> gained one. Invisible to the parsers, which split and trim. Not invisible to `FileAt`,
> whose whole job is to hand a diff the bytes that are in the commit: on Windows a file
> committed with LF was served as CRLF. It reads to the end of both streams now — two
> tasks, because draining one while the other fills its pipe deadlocks, which is why the
> line-based version existed. `A_files_bytes_survive_the_trip_out_of_git` commits CRLF
> and no trailing newline, so the failure reproduces on any platform rather than only in
> CI. The same call now decodes as UTF-8 explicitly: a redirected stream otherwise uses
> the console's encoding, which on Windows is an OEM code page.

## The graph is two lanes on purpose

The merge preview draws test above, your branch below, joined where they parted, with
test curving *down into* your branch. Hand-rolled SVG, no dependency: the general case
is an arbitrary DAG with lanes assigned by walking parents, and this picture answers one
question that has exactly two lanes.

The thing people get backwards is which branch moves, which is why it is drawn *and*
said in words — and why the uncommitted files are listed under a heading saying they are
committed **on your own branch**.

## A file's history is the file's, across renames

`History(branch, path:)` adds `--follow`, and the reason it can is that
`--name-status` then names the path the file had **at each commit** — so the diff
under the list is fetchable for the commits from before the rename, which is the only
thing that made following worth doing. Half of it is worse than none: a followed list
whose diffs are all fetched at today's path shows an empty history for everything
before the rename and never says why.

Three parts, and each is load-bearing:

- `CommitFile.OldPath` — git's tab-separated `R100 old new` has three fields where everything else
  has two, and the middle one is the only record of where the file came from.
- `FileChange(sha, path, oldPath)` — the left side of a rename is read at
  `oldPath`. Without it `git show <sha>^:<new path>` finds nothing, and **the commit
  that moved a file reads as the commit that created it**. That is the assertion in
  `A_files_history_follows_it_across_a_rename`, pinned rather than described.
- The client never falls back to the address-bar path. A commit whose name-status is
  empty is a refusal, not a default — the default is what silently re-creates the
  half-following failure.

> `--follow` is git's own guess and it can cross into unrelated history: delete a
> path, rename a different file onto it later, and the walk follows. Living with git's
> answer; the alternative is a rename index of our own.

The rail beside the list is **not** a lane graph, unlike the merge preview's. This list
is filtered to one file, so two adjacent rows are usually not parent and child, and
lanes drawn across them would claim a shape the data does not have. A dot per commit, a
merge drawn hollow, and nothing else asserted.

## A commit is a page

The branch History used to expand a commit in place. That put the answer to "what
did this change" inside a row in a list — nowhere to go from there, and no address
to send anybody. It is `/files/:project/:branch/commit/:sha` now, with the chosen
file appended.

`commit` sits in the same URL slot as the notebook views without being one, so
three predicates have to agree it is not: `viewOf` (which reads segment 3),
`isEditorPath`, and `legacyFilesPath`. `NOTEBOOK_VIEWS` carries a note that the
name is taken — adding `commit` to that array would hand every commit URL to the
editor, and all three would flip at once. `routes.test.ts` pins the four answers.

The **URL carries all forty characters** while the list shows eight. An
abbreviation is unambiguous until the repository grows, and a link that stops
working at some size is a worse trade than a long address.

`GitService.Commit(sha)` is its own lookup rather than a search through a history
list: the sha in a URL may be older than any list the app would have fetched.
Unknown returns null and the API turns that into Not found — an empty commit
object would render as a commit that changed nothing, which is the silent-blank
failure again.

**The tab is in the query** (`?tab=history`) where the folder beside it is not.
The difference is the round trip: you come *back* from a commit, and landing on
Contents loses the list you were working through. A folder has no such return.

Dates in the list are written out — `09/08/2026 12:00 PM`, not "2d ago". A history
is a record, and "which afternoon was that" is a question relative time cannot
answer at all. `stamp` lives beside `timeAgo` rather than replacing it; a row you
are scanning still wants the relative form.

`changedTree` is in `commitTree.ts` and not in the component, because the component
imports `DiffView` and therefore Monaco, and Monaco touches `window` at import time
— a vitest suite in the node environment cannot load it. Pure functions go in a
pure module; that is what makes them testable here at all.

## Publish is a dialog because being blocked is the interesting case

The push was a text field on the toolbar. You typed a message into eleven
characters of space, pressed Push, and got back
`2 jobs files have problems — fix them before pushing to test` — no file, no
line, no problem. The one thing needed in order to act on it was the one thing it
did not say, and it arrived *after* the message was typed.

So: a dialog, opened before anything is typed, that reads the problems up front.
`/branch/incoming?problems=true` returns them — behind a flag because the merge
preview shares that endpoint and a recursive walk plus a YAML parse per jobs file
is not an answer it shows. Each file links to itself, each problem carries its
line.

**Staging.** `PushToTest` takes paths and forwards them to `CommitAs`, which
already took a pathspec. Ticked files become the commit; unticked ones stay saved
on the branch. All-ticked sends **no** paths at all rather than the full list —
that is the server's own sweep, and it is the branch that excludes the
`.name.saving` half-files a crashed save leaves.

`Uncommitted` gained `-uall`. Git collapses a wholly-untracked folder to one
`reports/` line, which is fine for "is there work" and useless for "tick the ones
to send". It also drops `.saving` files now: `CommitAs` excludes them from every
commit it makes, so offering one as a thing to commit was offering a tick that
could not do anything.

> **Validation stays whole-branch, deliberately.** A broken jobs file blocks the
> publish whether or not it is staged. The ff-merge moves test to the branch
> *head*, so anything already committed here travels regardless of the ticks — and
> `UpdateFromTest` commits uncommitted work without passing through this check at
> all. Staging chooses what gets committed now; it does not choose what test ends
> up with. The dialog says so rather than letting the rule look like a bug.

The button is **Publish** everywhere now — toolbar, `promotionSteps`, the autosave
line, the merge preview's "that is Publish, and it is still yours to press",
`docs/studio.md`, and the two harnesses. A rename that lands in four places out of
seven is worse than no rename.

## The breadcrumb narrows, left to right

`Files / project / branch / file`, which is the order `/files/:project/:branch/…`
already put them in. It read `Studio / project / Files / file [branch]` — the
project pinned in front of the trail by `TopBar` rather than placed in it, and the
branch hanging off the file name as a pill, where it looked like a property *of
the file* instead of the scope the file is being read in.

`Crumb` gained `slot?: 'project' | 'branch'`: a step rendered as a switcher rather
than as text. That is what moved the ordering into `breadcrumbFor`, where it is a
pure function and a test can assert the *sequence* — `map(c => c.slot ?? c.label)`.
A test that only checked which crumbs were present passed against the old order,
which is the whole reason the shape is asserted rather than the contents.

No branch on the Files shell. The explorer's own picker is two inches below it, and
two controls for one thing is one more thing to keep in agreement. `Files` is
therefore the unlinked current page there — everything after it is a control, not a
place, so nothing else can carry `aria-current`.

## Browser checks, and the trap that prose could not fix

`test/tools/studio_ui_test.py` is the things only a browser can answer: whether a tooltip
appeared, whether a caret survived a hover, whether the sidebar moved 28px when you
opened a file. Anything that is a pure function belongs in the vitest suite beside the
app — `isFullBleed('/files/default')` is a unit test; the geometry either side of it is
not.

> **The stale-bundle guard is in `serving()`, not in the skill.** The trap was already
> documented and still cost three debugging rounds in one session: a break-test compiles
> a deliberately broken component, the next `--no-build` run serves *that*, and a
> working feature reads as broken. The check now compares the newest mtime under
> `webapp/src` against the bundle in `bin/` and refuses to start. Note there are two
> hops — `./build.sh Web` writes `src/…/wwwroot`, but the server serves the copy under
> `bin/`, written at C# build time.

Every check here was verified by breaking the thing it covers. Four were wrong until
that was done, and two of those passed against the bug they were written for.

## Not built

- **A username field on first-run setup.** Invite has one; setup derives. A taken-name
  error mid-passkey-ceremony has nowhere to go, and the empty-server case has nothing to
  collide with.
- **A full commit DAG.** The graph is two lanes. History is a list.
- **Per-file last-commit beyond one folder.** `Contents` walks the log once per listing
  and stops when every row is attributed; a repo with thousands of files in one folder
  would want a cache keyed on the branch head.
- **`IAccountProvider` route contribution**, and a second provider to shape it.
- **Publishing from the Files shell.** The dialog is opened from the editor's toolbar,
  so publishing means opening a file first — which is odd now that the shell is where a
  branch switch lands you.
- **A branch switcher on the commit page.** It sits in the URL slot the views use but is
  not one, and the switcher builds an editor path; switching from a commit would need a
  destination of its own (the branch's shell) rather than a file that may not be there.

Fixed since this was written, and no longer on this list: opening a file that exists
only on test used to sit on `Loading…` for ever rather than saying the file is not on
your branch yet.
