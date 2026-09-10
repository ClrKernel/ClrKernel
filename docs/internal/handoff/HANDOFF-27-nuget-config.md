# HANDOFF-27: `NuGet.Config` beside the notebooks, and through promotion

**Status:** Landed
**Date:** 2026-09-09
**Scope:** `InteractiveScriptEngine` (one argument), `NotebookTree`/`notebook.ts` (one name), `Promotion` (the unit), docs

## What it is

A `NuGet.Config` in the notebook's folder or any folder above it is what `#r "nuget:"`
restores through — the search `dotnet restore` makes in a repo. In Studio the file is
editable on a personal branch, publishes with everything else, and a notebook is
promoted together with the `NuGet.Config` files above it.

It replaced a `#i "nuget:<url>"` plan. `#i` was documented in an engine comment and
had never worked: the line reached Roslyn, which knows only `#r` and `#load`, and
failed with CS1024 — and `Dotnet.Script.DependencyModel` 2.0.1 has no `#i` parser at
all; the only regexes in it are `#r "nuget:` and `#load "nuget:`. The choice of a file
over a directive is the user's, and it is the better one: a feed is a property of a
repo, not of a cell, and credentials have somewhere to *not* be.

## The one-argument fix, and what it turned out the comment had wrong

`GetDependenciesForCode(targetDirectory, …)` was given the temp scratch root, with a
comment saying the notebook's directory would litter it with a `dotnet-script/` tree.
Run rather than reasoned: it does not. The scratch root is the directory
`ScriptProjectProvider` is *constructed* with; the call's argument is mirrored
underneath that, and it is what `NuGetUtilities.GetNearestConfigPath` walks up from.
Passing the notebook's directory finds the repo config and writes nothing outside
temp. As a side effect two notebooks no longer share one scratch project.

> **Dotnet.Script passes the file as `--configfile`, which NuGet treats as the whole
> configuration.** Verified with `dotnet restore --configfile` on a throwaway project:
> a repo config *without* `<clear/>` still searched `library-packs, private` — the
> user-level `nuget.org` was gone, where the hierarchical restore from the same folder
> listed all three. 0.13.0 shipped that as "list nuget.org yourself", and the first
> real notebook hit it within the hour: a private package whose dependencies live on
> nuget.org. A repo `NuGet.Config` adds to the user's everywhere else in .NET, and says
> `<clear/>` when it means "only these" — the exclusive reading was ours to fix.
>
> **How it is fixed, since there is no hook.** `ScriptProjectProvider`'s methods are
> not virtual and the restorer is internal, so what Dotnet.Script passes cannot be
> changed. It can be made moot. When a repo config is in reach, the engine generates
> the same project Dotnet.Script is about to (same path — `CreateProjectForRepl` under
> the `REPL` folder `GetDependenciesForCode` adds; verified equal) and restores it
> itself with `-p:RestoreRootConfigDirectory=<notebook dir>`, which is NuGet's own
> hierarchical discovery from that folder: repo chain, then user, then machine, with
> `<clear/>` honoured and relative paths resolved against the files they are in. Every
> package lands in the global packages folder. NuGet consults that folder before any
> source, so Dotnet.Script's exclusive restore that follows finds all of it and asks
> no feed for anything. Removing the pre-restore fails both NuGet.Config tests. Cost:
> one incremental restore per `#r "nuget:"` cell, only when a repo config exists.
>
> The merged-file alternative was rejected twice over — it would write expanded
> credentials into temp, and `AddItem.Value` in NuGet.Configuration expands `%ENV%` on
> read, so a faithful copy is not available through the public API.

`%NAME%` expansion in values is NuGet's own and was verified with a folder feed
addressed as `%CLRKERNEL_TEST_FEED%`. That is the whole credentials story: the repo
file says `%CLRKERNEL_SECRET_FEED_PAT%`, Studio sets `CLRKERNEL_SECRET_*` per branch
when it starts a kernel, and the same variable is what the kernel's own secret chain
reads. Nothing secret is in the repo, and which token signs in is the branch's.

## Promotion

`Promotion.CheckAsync` adds every `NuGet.Config` from the notebook's folder up to the
root to `Paths` — nearest first, the way restore searches — spelled as the file is on
disk, found case-insensitively, in test or (for a removal) in prod. `Apply` already
handles each path by name-status, so an unchanged config is a no-op and a removed one
is removed.

A changed config is gated like a changed notebook — `needsRun` — not like a cron edit.
It changes which packages the notebook resolves, which is a change to what runs; a run
that predates it proves nothing about it. The test commits a config change after the
green run and asserts the promotion is refused.

One consequence is named in the docs rather than solved: a root `NuGet.Config` is
shared by every notebook, so promoting any one of them carries the change for all.
Promote the one whose run proved it.

## Found, not fixed: the language service sees every loaded assembly

`InteractiveScriptEngine.ReferencePaths` seeds the completion compilation from
`AppDomain.CurrentDomain.GetAssemblies()` and overlays the engine's own references. In
a one-notebook process that is the session's own closure. In `lsp` mode — one engine
per notebook in one process — it is every notebook's: a package `#r`'d in notebook A
is a metadata reference in notebook B's completion. The test suite is exactly that
shape, and it showed: the `NuGet.Config` test's package declared `SimpleLib.Greeter`
too, and `ProjectReferenceTest`'s hover on the member returned null — CS0433, two
assemblies for one type, so the member could not bind, while completion on the type
still listed it. The fixture got its own namespace; the cross-notebook leak in `lsp`
is left where it is, because narrowing `ReferencePaths` to the engine's own closure
changes what completion sees for implicitly loaded dependencies and wants its own
handoff and its own tests.

## Not built

- **Skipping the pre-restore when the cell's packages are already in the cache.** It
  is incremental and quick; the check would cost about what it saves.
- **A `NuGet.Config` at the workspace root** (beside `.repo.git`, above the worktrees).
  It would be found by the walk from every branch, but it is unversioned and not
  promotable, and nothing stops somebody putting one there. Left as is.
- **Jupyter.** That front constructs the engine with the kernel's install directory,
  so there is no notebook folder to search from and the user-level config is what
  restore sees, as before.
