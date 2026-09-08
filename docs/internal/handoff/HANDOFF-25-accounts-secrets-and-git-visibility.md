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

Windows git prints worktree paths with forward slashes and puts `\r` on the end of each
line. Both halves of that have bitten: a test comparing a `Path.Combine` result to
`git worktree list` could never match there, and its negative twin passed for the wrong
reason. `Trim()` on every parsed line is not defensive, it is required.

## The graph is two lanes on purpose

The merge preview draws test above, your branch below, joined where they parted, with
test curving *down into* your branch. Hand-rolled SVG, no dependency: the general case
is an arbitrary DAG with lanes assigned by walking parents, and this picture answers one
question that has exactly two lanes.

The thing people get backwards is which branch moves, which is why it is drawn *and*
said in words — and why the uncommitted files are listed under a heading saying they are
committed **on your own branch**.

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
- **Opening a file that exists only on test.** Its Diff tab sits on `Loading…` rather
  than saying the file is not on your branch yet.
- **`IAccountProvider` route contribution**, and a second provider to shape it.
