# HANDOFF-23 — The query editor is a file, and notebooks can be moved

The Connections query editor was a `useState` string. It went away when you clicked
another connection, went away again when you reloaded, and the only way out of it was
**Open in notebook**, which appended your query to the end of some other notebook and
left nothing behind. The observation that fixed it was the user's: *"it's really just a
1 cell notebook."*

So it is one now — a real `.nb.md` on your own branch — and the two things you do to a
notebook's name, **Save a copy as** and **Move or rename**, are on both this page and
the notebook editor's toolbar.

## Where the scratch lives, and why that answered three questions at once

`.scratch/query-<connectionId>.nb.md`, in the caller's own worktree.

A per-user temp area outside the worktree was the other candidate and would have needed
two new routes to read and write it. The worktree needed **none**: `notebookContent`
GET/PUT already resolves any path under the branch root, `EditableTarget` already gates
it on "your own branch, and it must be a notebook", and `SafeResolve` already permits a
leading dot. The dot-directory then does two more jobs for free —
`NotebookTree.BuildDirectory` skips dot-directories, so scratch files never appear in
Files or the explorer.

The one thing it did *not* get for free is git, and this is the part worth remembering:

> A file in the worktree that nothing ignores makes the branch **permanently dirty**.
> `StandingOf` runs a bare `git status --porcelain`, so the Push button never clears;
> and `CommitAs` with no pathspec runs `add -A -- . :(exclude)**/.*.saving`, so the
> next push sweeps the scratch into test.

Two different code paths, one fix: a `.scratch/` line in the **bare repo's**
`info/exclude`. Git reads that file from the *common* directory, so one line covers
test, prod and every personal worktree at once — and unlike a `.gitignore` it is not
itself a tracked file that would have to be committed and promoted. `GitService` already
had `EnsureExcluded(pattern)` for `connections.json`; `ExcludeScratch()` is one line
calling it, from `Repair()` (which runs at startup for every existing workspace) and
from the fresh-init path.

I did not reuse it at first — I wrote a second implementation that resolved the bare
repo path itself. It passed the unit test and **failed against a real server**: the
line never appeared in the file. The shared helper resolves the path with
`git rev-parse --git-common-dir` rather than assuming, and works. The lesson is the
ordinary one and it cost an hour: look for the helper before writing the helper.

`A_scratch_file_leaves_no_trace_in_git` covers both halves — standing is not dirty, and
a push does not carry the file. It was verified by deleting the `ExcludeScratch()` calls
and confirming it fails.

## The ownership ref, which is the only subtle line in the client

The buffer belongs to a connection. When you click another one, the text on screen is
still the old query and it has to be flushed **to the old file**. Naively that is a
race: `selected` changes on the click, the load is async, and anything keyed on the
current selection writes the old bytes to the new path.

`owner` is a ref holding `{ id, name, path }` that moves **only when a load completes**,
never when the selection changes. Between the click and the file arriving, `sql` is
still the old query and `owner` still names the old file — so they agree, and the flush
that happens on the way past lands where it should. The browser check proves it by
typing and switching immediately, then reading both files off disk.

`useAutosave` is the notebook editor's, unchanged: same debounce, same flush on hide and
unload, same ⌘S, same status chip. That chip on the Connections toolbar is most of what
makes the page feel like the editor.

## The regression the file check could not see

Backing the buffer with a file broke `into()` — the tree's **Select Top 1000 Rows** and
**Script as**, which are reachable for a connection *other* than the one selected. The
old code navigated and then inserted into the editor. With a load effect behind the
navigation that becomes: insert into A's editor → route changes → flush, which
`owner` still points at A, so **the script is written into A's file** → the loaded text
replaces the buffer, so **the script is wiped off the screen**. You ask a table under
another connection for a script, land on that connection, and nothing appeared.

The fix is the same ownership idea one step further: when the connection is not the
selected one the script does not touch the editor at all. It goes into a `pending` ref
and travels with the navigation; the load effect consumes it on the far side, once the
file it belongs to has arrived. `savedSql` stays what is on disk, so the carried script
leaves the buffer dirty and autosaves itself a moment later. `pendingInsert` is pure and
unit-tested; the wiring is browser-tested against a real SQL Server, because reaching
`into` cross-connection needs a tree with real metadata in it. `check_carry.py` was
verified by reverting the fix — it reproduces exactly the two symptoms above.

`.jobs.yaml` was the other thing the File menu reached that nobody had thought about:
the Editor opens one on the Source tab, and `notebookPath` did not know the extension,
so renaming one produced `nightly.jobs.yaml.nb.md`. `notebookPath` now takes the
extension to keep, defaulted so New notebook cannot make a jobs file by accident.

## Save a copy as / Move

**Save as needed no endpoint.** It is `createNotebook`'s shape — read first so it cannot
silently empty an existing file, then write. **Move needed exactly one**:
`POST /notebooks/move`, with the destination through the *same* `EditableTarget` gate as
the source, so a move cannot write anywhere a save could not. It refuses to land on an
existing file: a move is not a save, and the one thing nobody means by it is "replace
that other notebook".

The split between the two verbs is about where you end up. **Save a copy** keeps you on
the connection — you kept the query because it was worth keeping, not because you were
finished. **Move** ends the scratch and takes you to the notebook editor.

`moveNotebookTo` warns first when a `*.jobs.yaml` names the notebook by its old path,
and says which jobs. Nothing rewrites the jobs file, so those jobs really are broken
until someone points them at the new path — and that failure would otherwise surface at
the next scheduled run, in a log nobody is watching. It costs one request.

A warm kernel session keyed on the old path is left to idle out, so a move loses the
variables in it. Marked `ponytail:` at the route.

## The toolbar numbers moved, again

`notebookToolbar.ts` says *"re-measure if the controls change"*, and the File menu is a
control. Every tier grew by its one glyph. Measured with **Push to test on the bar** —
the state you actually work in, and the omission that made these numbers wrong last
time: `{ narrow: 900, collapse: 950, tight: 1210, compact: 1290 }` →
`{ narrow: 940, collapse: 980, tight: 1240, compact: 1320 }`. After the change no tier
overflows anywhere above the documented 888px floor, where the bar is allowed to scroll
rather than grow a second row.

Two buttons rather than a menu would have cost about 90px and pushed every tier again;
the menu costs 34.

## Not built

Making the Connections page the actual `CellEditor` running through the kernel session.
It would cost Cancel, the per-session password, the `canExecute` gate, the connection
audit and the result-set tabs, all of which are this page's own. Everything here is a
prerequisite for it rather than an alternative to it, so it can land on top if the
one-cell-notebook idea is ever meant literally.
