# HANDOFF-26: `#r "project:"` — reference a local .csproj from a notebook

**Status:** Landed — `src/ClrKernel.Core.Scripting/ProjectReference.cs`, wired at
`InteractiveScriptEngine.PrepareStatement`; tests in `ProjectReferenceTest` over
`test/fixtures/projects/`. The spec below is as written; **§Landed** at the end records
where the code differs from it and why.
**Date:** 2026-09-08
**Scope:** ClrKernel.Core (directive + resolver), ClrKernel.LanguageServices (lit up for free), docs

## Summary

Add a `#r "project: <path>.csproj"` directive that builds a local .NET project with the
.NET SDK and references its output in the session, alongside the existing
`#r "nuget:"` and `#r "path/to/local.dll"`. Re-running the directive rebuilds and reloads.

The contract is one sentence: **whatever `dotnet build` produces for this project, reference it.**
ClrKernel never reads `.cs` files, never runs generators, never interprets the project file
beyond asking MSBuild to evaluate a few properties.

## Why shell out to MSBuild (and not compile sources in-process)

| | Shell out to `dotnet build` | Parse .csproj + Roslyn in-process |
|---|---|---|
| Source generators, T4, `Exec` targets, Directory.Build.props, multi-targeting | Free | Reimplement one feature at a time, forever |
| CLI code generators (Kiota, NSwag, `dotnet ef scaffold`, protoc) | Output files are ordinary `.cs` by build time — no special handling | Same, but everything else above still breaks |
| Restore / transitive deps | Implicit restore; `-o` dir contains the full closure | Reimplement NuGet resolution against the project graph |
| Build time | Incremental build via MSBuild | Faster on paper, but only for trivial projects |
| Surface area ClrKernel owns | Directive parsing, one process launch, load + reference | Everything |

Decision: shell out. The scope stays tight because MSBuild is the boundary.

## Directive grammar

```
#r "project: <path-to.csproj>[, Key=Value]*"
```

| Option | Values | Default | Notes |
|---|---|---|---|
| `Configuration` | any | `Debug` | Passed as `-c` |
| `Framework` | a TFM | auto (see below) | Passed as `-f`; required only when auto-pick fails |
| `NoBuild` | `true`/`false` | `false` | Skip the build; reference the last-built output. For CI where a prior step built the project |

- `<path>` resolves relative to the notebook's directory, same rule as `#!import`.
- Only `.csproj` (and `.fsproj`/`.vbproj` if they build to a DLL — no reason to block them). **Not** `.sln`. Multiple projects = multiple lines.
- Keys are case-insensitive. Unknown keys are an error, not ignored.

Examples:

```csharp
#r "project: ../src/MyLib/MyLib.csproj"
#r "project: ../src/MyLib/MyLib.csproj, Configuration=Release, Framework=net9.0"
#r "project: ../src/MyLib/MyLib.csproj, NoBuild=true"
```

## Pipeline

### 1. Evaluate (no build)

```
dotnet msbuild <proj> -nologo -getProperty:TargetFramework -getProperty:TargetFrameworks -getProperty:TargetName -getProperty:OutputType
```

- If `TargetFrameworks` is non-empty (multi-targeted): choose the highest TFM whose major version ≤ the kernel's runtime major (`Environment.Version.Major`). Re-evaluate with `-p:TargetFramework=<chosen>` if further properties are needed.
- If the only TFM is newer than the kernel runtime → fail with a clear message ("project targets net11.0; kernel is running net9.0"). Do not let this surface later as `BadImageFormatException`.
- If `OutputType` is `Exe`/`WinExe`: allow it (the DLL still exists and is referenceable) but warn. Don't run it.
- Take `TargetName` for the output assembly name.

### 2. Build to a private output directory

```
dotnet build <proj> -c <Configuration> -f <tfm> -o <ProjectOutputRoot>/<hash>/<n>/ --nologo -v:q
```

- `<ProjectOutputRoot>` = the kernel's existing temp/cache root (wherever NuGet resolution and extension-method cell DLLs already go) + `/projects/`.
- `<hash>` = stable hash of the absolute project path.
- `<n>` = monotonically increasing per session per project. **Never build into the user's `bin/`**: on Windows a loaded DLL is locked and the next rebuild would fail.
- Stream stdout/stderr to the cell as it arrives. On non-zero exit, surface MSBuild's output verbatim as an error — do not try to interpret generator or compiler errors.
- Implicit restore is fine. Do **not** run `dotnet tool restore`; if a project depends on a local tool manifest, the user runs that themselves (documented, see Non-goals).

### 3. Load + reference

- Collect the reference set from `<out>/<TargetName>.deps.json` (preferred) or, simpler for v1, glob `<out>/*.dll`.
- Filter out anything already loaded in the default `AssemblyLoadContext` by simple name — otherwise Roslyn reports duplicate references. Log a warning when the on-disk version differs from the loaded version (this is the "project wants Newtonsoft 13.0.1, kernel has 13.0.3" case; default ALC unifies, which is acceptable).
- Hand the remaining paths to the **same code path `#r "local.dll"` uses**: `MetadataReference.CreateFromFile` for the script compilation + load into `AssemblyLoadContext.Default`.
- Register one `AssemblyLoadContext.Default.Resolving` handler (once per session) that probes every registered project output dir for transitive deps the kernel doesn't already have.
- Add a `using <RootNamespace>;` automatically? **No** — leave that to the user, consistent with `#r "nuget:"`.

### 4. Re-run semantics

Re-executing the directive rebuilds into `<n+1>`, loads the new assembly, and swaps the metadata reference. This is the same behavior the extension-method cell already has when edited and re-run: live variables holding old types keep their identity; new cell code sees the new types. Document, don't fight.

### Caching

After the build, compare the **MVID** of the output assembly (read via `PEReader`, no load) to the last-loaded MVID for this project. Equal → skip the reload, reuse the existing reference, don't create `<n+1>`. Not equal → proceed. MVID rather than file timestamp so a non-deterministic build (generator stamping a GUID) reloads correctly and a deterministic no-change build doesn't.

## Proposed components (ClrKernel.Core)

Adjust names/locations to match where the NuGet and local-DLL resolvers actually live — I don't have the tree in front of me.

| Component | Responsibility |
|---|---|
| `ProjectReferenceDirective` (parser) | Parse `project:` payload + `Key=Value` options; resolve relative path; validate extension; produce a `ProjectReferenceRequest` |
| `ProjectReferenceResolver` | Steps 1–3 + caching. Public surface: `Task<ProjectReferenceResult> ResolveAsync(ProjectReferenceRequest, CancellationToken)` returning `{ AssemblyPath, ReferencePaths[], Mvid, BuildOutput, Warnings[] }` |
| `DotnetCli` (thin wrapper, if one doesn't exist) | Locate `dotnet` (PATH, then `DOTNET_ROOT`), run with args, stream output, honor cancellation (kill process tree on interrupt) |
| `ProjectOutputStore` | Owns `<ProjectOutputRoot>/<hash>/<n>` allocation, per-session MVID map, the ALC `Resolving` handler registration |
| Session wiring | Route `#r "project:"` through the resolver and into the existing add-reference path used by `#r "local.dll"` |

Session-level state: `Dictionary<string projectPath, (int n, Guid mvid, string outDir)>`.

Interrupt: a kernel interrupt during build must kill the `dotnet` process (and children — MSBuild node reuse can leave workers behind; pass `-nodeReuse:false` or `--disable-build-servers` if that becomes a problem).

## Edge cases to handle in v1

- Project path doesn't exist / isn't a project file → error before any process launch.
- `dotnet` not on PATH → error naming the env var to set.
- Multi-target with no compatible TFM → error listing the project's TFMs and the kernel's runtime.
- Build fails → verbatim MSBuild output, non-zero cell status (headless runs must fail the job).
- `NoBuild=true` but no prior output in the store → error telling the user to run once without `NoBuild` (do **not** silently fall back to the project's `bin/`).
- Same project referenced from two notebooks in one kernel session → shared `<hash>` dir, shared `n` counter. Fine.
- Project-to-project references → free; MSBuild builds the graph and `-o` collects the outputs.

## Non-goals (say no to these on the tracker)

- **Running CLI code generators or any other pre-build step.** Answer: put it in a `BeforeBuild` target, or run it from a shell/PowerShell cell in the same notebook. Both already exist.
- **`dotnet tool restore`.** User runs it once in a terminal.
- **`.sln` references.** One `#r` line per project.
- **Watch mode / rebuild on file change.** Re-running the cell is the trigger.
- **Unloading old builds** (collectible ALC). Roslyn scripting state pins types; unloading is a trap.
- **Auto-`using` the root namespace.**
- **Safety against untrusted projects.** A build can run arbitrary code via `Exec`; so can a cell. Don't market this as safe for untrusted input.

## Test plan

Unit (ClrKernel.Core.Tests):
- Directive parsing: relative path resolution, each option, case-insensitive keys, unknown key error, missing/wrong-extension path error.
- TFM selection: single TFM, multi-target pick-highest-compatible, none-compatible error.
- MVID cache: deterministic rebuild with no changes → no reload; source change → reload; non-deterministic build → reload.
- Reference filtering: assembly already loaded in default ALC is excluded; version mismatch produces a warning.

Integration (fixture projects under `tests/fixtures/projects/`):
- `SimpleLib` — one class, one TFM. Reference, call a method, assert result.
- `MultiTarget` — `net8.0;net9.0`. Assert the right one is chosen under the test runtime.
- `WithSourceGenerator` — references a trivial generator package; assert generated type is usable.
- `WithP2P` — references `SimpleLib`; assert both types usable, both DLLs in the output dir.
- `WithDepConflict` — references an older version of a package the kernel already loads; assert warning emitted and cell still runs.
- `BuildFails` — deliberate compile error; assert MSBuild output surfaces and cell status is error.
- Re-run: modify fixture source between runs (copy to temp, edit, rebuild) → new method visible; old instance still works.
- `NoBuild=true` before any build → error; after a build → reuses output without launching `dotnet build`.
- Interrupt mid-build → `dotnet` process tree is gone, cell reports interrupted.
- Headless: `clrkernel run` with a notebook containing a failing `#r "project:"` exits non-zero.

Language services:
- After `#r "project:"`, completion lists the project's public types; hover shows XML docs if the project emits them (`GenerateDocumentationFile=true` in the fixture).

## Docs

- README "Use" section: add `#r "project: path/to/proj.csproj"` next to the nuget/dll line.
- docs-site page `reference/directives` (or wherever `#r` is documented): grammar table above, the four-step pipeline in two sentences, the re-run semantics, and the Non-goals list phrased as "How do I…?" answers (generators → BeforeBuild or a shell cell, etc.).
- Add the regenerate → re-run `#r` → new types loop as a worked example; it's the feature's main selling point.

## Files to stage (this handoff)

```
docs/handoff/HANDOFF-26-project-reference.md
```

Suggested commit message:

```
docs: handoff 26 — spec for #r "project:" local project references
```

No version bump, non push.

---

## Landed — where the code differs from the spec, and why

Decisions taken before starting (asked and answered: "defaults"):

1. **Relative paths resolve the way `#!import`'s do** — against the importing file's
   directory inside an import, else the engine's directory. Not `#r "x.dll"`'s rule:
   that one resolves against `AppContext.BaseDirectory`, the kernel's install folder,
   and is a latent bug this does not inherit. The Jupyter front constructs the engine
   with no notebook directory at all, so there a relative path is the install folder;
   the README says to use an absolute one.
2. **`NoBuild` copies the project's own output** (`-getProperty:TargetPath`) into the
   store and references the copy. The spec's version was self-contradictory: "for CI
   where a prior step built it" and "never fall back to `bin/`" and "the last build in
   the store" — a CI step builds into `bin/`, never the store. The copy is what keeps
   the next `dotnet build` in a terminal from finding its output locked.
3. **No interrupt.** `ExecuteAsync` takes no token and no front delivers one — Jupyter's
   `CancelKeyPress` handler swallows Ctrl+C. A hung build is killed by `BuildTimeout`
   (10 min); the interrupt test in the plan does not exist. Cancellation plumbing is
   its own handoff. `-nodeReuse:false` is always passed, because a Studio kernel lives
   for one run and an MSBuild worker node would outlive it.
4. **The Studio Docker image has no SDK** (`aspnet:10.0`), and it stays that way. The
   runtime-only failure — "Could not execute because the specified command or file was
   not found" — is caught and reworded, because as printed it reads as a missing
   project.
5. **References are filtered by what the kernel *ships*** (the shared framework
   directory and its own install directory), not by what is loaded so far. The latter
   changes as cells run and would make the reference set depend on execution order.
   No new `Resolving` handler: the cell-library `AssemblyResolve` hook already probes
   every engine reference path, so adding `<out>/*.dll` as references is enough.

Two things the spec could not have known without running it:

> **Roslyn resolves a script's references by identity, not by path.** Every earlier
> submission carries the assembly it compiled against, and a rebuilt
> `SimpleLib, 1.0.0.0` at a new path is ignored in favour of the loaded one — the
> re-run compiled with `CS0117: 'Greeter' does not contain a definition for 'Wave'`.
> "Swap the metadata reference" is not a thing the compiler offers. The cell-library
> code avoids this by content-hashing the *assembly name*; a project's name is not
> ours to change (a global `AssemblyName` would rename every project in the graph
> to the same thing). So the build passes `-p:AssemblyVersion=<major.minor.build>.<n>`
> — Roslyn prefers the higher version — and it is a global property, so the whole
> graph moves together.
>
> **That breaks MVID caching**, since a deterministic build hashes the version in.
> The fix is two-phase: build at the version *already loaded*, compare MVIDs — equal
> means byte-identical, nothing to do, delete the directory; different means a real
> change, so build again one revision up, which is the identity Roslyn will take.
> One build per unchanged re-run, two per change; the second is incremental.
> `An_unchanged_rebuild_is_not_a_reload` and `Rerunning_after_an_edit…` each fail
> when their half is removed.

> **A multi-targeted project evaluated without a framework has no `TargetName`** —
> that is the outer build. Inside the repo it looked fine only because
> `Directory.Build.props` supplies a `TargetFramework`; the fixture copied to temp
> showed `""`. So evaluation is two `dotnet msbuild` launches: the targets, then the
> rest with `-p:TargetFramework` pinned — exactly the "re-evaluate if further
> properties are needed" the spec allowed for, made unconditional.

Names, since the spec did not have the tree: the resolver is `ProjectReferences`, the
parse is `ProjectReferenceRequest.TryParse`, the host wrapper is `DotnetCli`
(`Locate` tries the host that started this process first — a GUI-launched VS Code on
macOS has no `dotnet` on PATH — then `DOTNET_ROOT`, then PATH). Tests are in
`test/ClrKernel.Core.UnitTest`, fixtures under `test/fixtures/projects/`, and `#r` is
documented in the README's Use section, which the docs site splits into guide pages;
there is no `reference/directives` page.

The directive line is **blanked, not rewritten** to `#r "…/Out.dll"`: references go
into the session options the way a NuGet package's do, so Roslyn never sees it — not
at execution, and not in the language service's replay of the submission, which
would otherwise hit CS0006 on every keystroke. An empty line keeps diagnostics'
line numbers.

> **"The `-o` directory contains the full closure" is true of applications only.**
> The `WithDepConflict` fixture found it: a *library* build sets
> `CopyLocalLockFileAssemblies=false`, leaves its packages in the NuGet cache and
> writes only their names to `deps.json`. The project dll loaded, and nothing in the
> output was there to warn about — the first test run passed the "shipped assembly is
> left out" assertion for the wrong reason and failed on the warning. The build now
> passes `-p:CopyLocalLockFileAssemblies=true` (global, so the whole graph). NoBuild
> cannot, since it copies a build it did not make; it reads the `deps.json` instead
> and refuses when a runtime asset is neither in the copy nor shipped by the kernel,
> naming the property to set. `MissingRuntimeAssemblies` is tested over a hand-written
> `deps.json` rather than a fixture — the missing-package case would need a package the
> kernel does not ship, and every candidate is a network restore.

The `WithSourceGenerator` fixture needs no package at all: `System.Text.Json`'s
generator ships in the SDK, and the test adds a second `[JsonSerializable]` between
two runs of the `#r` — the generated member it then asks for did not exist at the
first build. That is the regenerate → re-run → new types loop, end to end and offline.

Not built, beyond the spec's own list: a `NoBuild` re-run after an *external*
rebuild, which cannot bump the version and so cannot be reloaded — a kernel restart
is the answer, and the README's option table says so.
