# HANDOFF-28: a printed secret is masked

**Status:** Landed
**Date:** 2026-09-10
**Scope:** `Core.Primitives` (the registry), `Core.Secrets` (registers), `Core.Scripting` (two chokepoints), the four fronts' error replies, docs

## The question that started it

"If I accidentally `Console.WriteLine` the secret, won't it log in the output
somewhere when scheduled?" It did. Nothing masked anything: the value went into the
run artifact, `run.log`, the run page, the notification body if the cell threw with it,
and — in VS Code — the outputs saved into an `.ipynb`, which is to say the repo. The
"mask"/"redact" words already in the code were all about the *UI never showing a
stored value*, a different thing.

## Where it lives, and why there

The kernel is the one process that knows a secret's value, so it is the one place a
printed value can be caught — the same reasoning as GitHub Actions masking
`secrets.*`. `SecretRedaction` is in **`Core.Primitives`**, because the two projects
that need it cannot see each other: `Core.Secrets` registers values and
`Core.Scripting` redacts, and neither references the other. `Core.Secrets` gained its
first project reference for this — Primitives, netstandard2.0, nothing else in it used.

Registration happens in `SecretStore.TryResolve` and `Store`, and at engine start from
every `CLRKERNEL_SECRET_*` variable. The seed is what catches a cell that never went
near the store: `Environment.GetEnvironmentVariable` and `#!bash env` print the same
value.

## The chokepoints

Every byte of cell output leaves through one of these, and each calls `Redact`:

| Where | Covers |
|---|---|
| `ConsoleProxy.OnLineReceived` — one class, all four fronts | `Console.WriteLine`, stderr |
| `MimeBundler.Bundle` | `Display()`, trailing values, HTML, markdown, shell and PowerShell output (they return `DisplayConsoleText`, which bundles here) |
| the execute error reply in `serve`, `lsp`, Jupyter, and `run` | exception messages and stack traces — the text that becomes `ErrorSummary` and a notification body |

Studio needed no change: the run log and the artifact are built from what the kernel
sent, and that is already masked.

## Decisions

- **Eight characters or nothing.** Below that a value is not registered. Masking
  "pass" masks "password", "compass" and every "pass" in prose, which hides more than
  it protects. GitHub warns about the same thing.
- **Longest first.** A registered value that contains another registered value is
  replaced whole; masking the shorter inside the longer would leave `***-and-more`.
- **In place on the MIME bundle**, string values only. `application/octet-stream` is
  base64 and cannot match; an `application/json` payload is a string here and is
  redacted like any other.
- **A safety net, not a boundary.** Said in the docs in those words. Base64, a split,
  a JSON escape — all walk past.

## Verified

The headless-runner test prints, displays and throws one seeded value through three
cells and asserts the artifact holds `printed: ***`, `displayed: ***` and
`thrown: ***` and the value nowhere. The store test resolves a value and asserts the
same text is masked after and not before. Each chokepoint was removed in turn to
watch its assertion fail.

## Not built

- **Masking in Studio's own logs** (the scheduler's, not the run's). Studio never sees
  a value; it hands references to the kernel. Nothing to mask.
- **Masking in the VS Code extension's output channel.** It shows what the kernel
  sent, which is masked; the extension never resolves a secret itself.
