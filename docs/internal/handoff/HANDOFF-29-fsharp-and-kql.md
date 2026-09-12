# HANDOFF-29: F# and KQL cells

**Status:** Landed
**Date:** 2026-09-11
**Scope:** `Language.FSharp`, `Language.Kql` + `Database.Provider.Kusto`, registration in the three roots, `bundledLanguages`, samples, README

## Why now

`.dib` conversion landed (the commit before this) and the first real `.dib`
files had `#!fsharp` and `#!kql` sections. They converted correctly — as fences
tagged `fsharp` and `kql` — and then did nothing, because no language claimed the
tag. Two languages was the smaller change compared with explaining that.

## F#

`FSharp.Compiler.Service` in-process, one `FsiEvaluationSession` per notebook
(lazily: a notebook that never runs F# never pays the ~1 s start). Same session
model as PowerShell and Python: bindings persist, nothing flows to or from the C#
script state. `EvalInteractionNonThrowing` gives the value and the diagnostics
together; errors become `FSharpCellException` with fsi's own text, warnings go to
stderr, a `unit` result shows nothing. The trailing value is returned raw, so a
list of records is a grid through the ordinary formatter path.

**Two traps, both in naming.** The project's namespace ends in `FSharp`, so inside
it `FSharp.Compiler.…` resolves to `ClrKernel.Language.FSharp.Compiler` — the
usings say `global::`. And `Shell` is an F# module, not a namespace: `using static`.

Fsi captures its writers at creation, so `Console.Out` at that moment would be
whatever it was then, not the kernel's proxy during a later cell. The writers
forward to the *current* `Console.Out` on every write; the test swaps the console
and checks `printfn` lands in it.

Not built: F# editor services (completion/hover), `#r "nuget:"` inside F# cells
(needs the dependency-manager dll beside the compiler), and `--gui` off means no
WinForms event loop, which nobody wanted.

## KQL

The DAX shape exactly — a language project owning the cell and the session, a
provider project owning the connection — because Kusto is the same kind of thing
Analysis Services is: a query language over a non-ADO.NET endpoint with Entra
sign-in. `Microsoft.Azure.Kusto.Data`, `KustoConnectionStringBuilder
.WithAadAzureTokenCredentialsAuthentication(TokenCredential)` over the shared
`EntraAuth` chains. A query's primary result loads into a `DataTable`, which the
renderer shows as the grid since the DataTable fix two commits back. A cell
starting with `.` is a management command and goes through the admin provider.

**The entry point is `KustoDb`, not `Kusto`.** `Kusto` is the root namespace of
the client library, and a C# cell that references the assembly resolves the bare
name to the namespace before any `using`-imported type. `Fabric` gets away with
it because the Fabric SDK lives under `Microsoft.Fabric`.

Not built: `connections.json` backing (`IConfigBackedConnections`) — the catalog
lists, adds, removes and sets a default, which is what the editors need; saving a
Kusto connection to the file is the next step and follows `SsasSession.Config.cs`
line for line. Also no editor services. The live test wants
`CLRKERNEL_TEST_KUSTO=https://help.kusto.windows.net` and an Entra sign-in.

## The seam that had to move

A language's result used to leave the engine as-is unless it was a display
concept, and the fronts render only bundles — so an F# cell's `385` and a KQL
`DataTable` reached the headless runner and were dropped on the floor; the engine
test saw the raw 42 and passed. The language path now bundles a raw value the
way the C# path bundles a trailing value (`DisplayObject` → `MimeBundler`).
`PluginRegistrationTest`'s toy language returned raw strings and pinned the old
behaviour; it reads the bundle's text now. The F# sample run headlessly is the
check: `385` and `Sale × 3 items` are in the artifact.

## Verified

F#: value, persistence, console capture, compile error and raised exception (with
the session surviving), and engine routing through `#!fsharp` and `#!fs`. KQL:
directive parsing including the forbidden `--client-secret`, the comment
selector, the registry's default handling, and the descriptor. Extension: the
`.dib` reader now makes `#!kql` a KQL cell. Both samples round-trip through the
markdown serializer byte for byte, as every sample must.
