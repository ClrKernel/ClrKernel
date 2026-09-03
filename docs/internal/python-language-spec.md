# Python as a cell language — what it would take

*Written 2026-09-02, before any work. The findings in §1 are from a spike on this
machine, not from reading package descriptions; they change the plan, so they come
first.*

## 1. The two packages named in the request

**`pythonnet` 3.1.0 is fine.** It is the C# ↔ CPython bridge and it is not the
problem.

**`Python.Included` 3.13.6 is Windows-x64 only, and fails badly elsewhere.** The
whole package is one 11 MB assembly whose entire payload is a single embedded
resource:

```
$ strings Python.Included.dll | grep -i embed
Python.Included.Resources.python-3.13.12-embed-amd64.zip
```

That is the **Windows embeddable distribution**. There is no Linux, macOS, x64 or
arm64 counterpart in the package. Run on macOS arm64 it does not refuse — it
unpacks the Windows distribution into `~/Library/Application Support/` and then
dies initialising:

```
host: macOS 26.6.2 Arm64
install dir: /Users/…/Library/Application Support/python-3.13.12-embed-amd64
FAILED: TypeInitializationException: The type initializer for 'Delegates' threw an exception.
```

So `Python.Included` delivers the "no Python on PATH" promise on Windows and
nothing at all on the two platforms this project cares about most: the maintainer's
macOS, and the Linux container Studio runs in. It cannot be the answer on its own.

## 2. The size budget rules out embedding

The shipped tool is **67 MB** (`ClrKernel.0.11.0.nupkg`). A redistributable CPython
is 40–60 MB per platform, and "all-inclusive" means five of them
(win-x64, linux-x64, linux-arm64, osx-x64, osx-arm64). Embedding puts a
`dotnet tool install --global` past 300 MB for a feature most notebooks never use.

**So: provision on first use, cache, never embed.** That keeps the promise — the
user installs nothing and has no system Python — while the cost lands only on
people who run a `#!python` cell.

## 3. What a language costs here, structurally

This part is cheap and known. Per HANDOFF-19, a cell language is *one
implementation plus one registration*: implement `ICellLanguage` in a new
`ClrKernel.Language.Python`, register it in `src/ClrKernel/CellLanguages.cs` and the
two test mirrors. Fence parsing, completions, the RPC language list, the VS Code
picker and Studio's file→cell rules are all **derived** — including, as of 0.11,
Studio running a bare `.py` file as one cell, which falls out of the tag.

`ClrKernel.Language.PowerShell` is the precedent to copy: `Microsoft.PowerShell.SDK`
embeds a runtime, `PowerShellSession` holds one Runspace so variables persist
across cells, and `ExecuteAsync` is thin. A Python session is the same shape with a
different engine behind it.

The work is not the language contract. It is everything in §4 and §5.

## 4. Decision one: in-process or beside it

**In-process (pythonnet).** One CPython in the kernel process.
- *For*: C# and Python cells could share objects — the thing that makes a polyglot
  notebook more than two notebooks in a trenchcoat.
- *Against*: the GIL against the engine's async; a segfault in a native wheel takes
  the notebook's kernel with it; `Runtime.PythonDLL` must be set before first
  initialise and cannot change afterwards, so the interpreter is chosen once per
  process.

**Out-of-process (a resident interpreter over a pipe).** The shape `Shell` already
uses, but long-lived.
- *For*: a crash kills a subprocess, not the notebook. Cancellation is a process
  kill — which is already the only mechanism that works in this kernel, so it fits
  what `NotebookSessionManager` does. Each notebook gets its own venv without
  arguing with a process-wide `sys.path`.
- *Against*: values cross as bytes, so C#↔Python sharing means a serialisation
  format rather than a reference.

**Recommendation: out-of-process first.** It is shippable, it is safe, and the
protocol can be designed so an in-process mode replaces it later if object sharing
turns out to be what people want. Starting in-process and retreating is the
expensive order.

## 5. Decision two: where the interpreter comes from

The candidate is **`uv`** (Astral): one static binary that installs CPython builds
(`uv python install`), makes venvs (`uv venv`) and resolves requirements
(`uv pip install`) far faster than pip. It covers the five platforms and is the
same mechanism `Python.Included` is reaching for, done cross-platform.

The alternative is fetching `python-build-standalone` releases directly and running
`python -m venv` — fewer moving parts, more of the platform matrix to own.

**To verify in a spike before committing** (none of this is checked yet):
- which RIDs `uv` publishes a binary for, and its licence terms for redistribution;
- whether a downloaded interpreter runs on macOS without a Gatekeeper prompt —
  quarantine and codesigning on arm64 is the likeliest nasty surprise;
- the offline story. An air-gapped machine cannot download an interpreter, so
  "point at your own Python" has to exist as an escape hatch even though the goal
  is not needing one.

## 6. venv and requirements

Per **notebook**, beside the notebook, not global — the same instinct as
`connections.json` resolving by walking up from the notebook. A `requirements.txt`
next to a `.nb.md`, or a directive:

```
#!python-install pandas matplotlib
```

Studio makes this concrete: a notebook's venv belongs in its worktree, which means
it must be git-ignored, and promotion must not carry a `.venv/` between branches.
Worth settling before the first line of code.

## 7. Where the value actually is

Running Python is table stakes. What would make it worth doing:

- **A DataFrame renders as the interactive grid.** `DisplayTable` already exists and
  every formatter is overridable; a pandas frame → `DisplayTable` is a small adapter
  and the single biggest thing a Python user would notice.
- **matplotlib → `DisplayBytes`**, so a plot appears in the cell like a Mermaid
  diagram does.
- **A SQL result into Python.** `#!sql` already produces tabular data; handing that
  to a Python cell is the cross-language step people would actually use, and it does
  not need object sharing — an Arrow or CSV handoff would do.

## 8. Rough shape of the work

| | |
|---|---|
| Interpreter provisioning + cache + escape hatch | the risky part; spike first |
| `ClrKernel.Language.Python` + registration | small, and the pattern is established |
| Resident interpreter protocol, streaming stdout, cancellation | the real engineering |
| venv per notebook, requirements, git-ignore, promotion | design before code |
| Display adapters (DataFrame, matplotlib) | where the value is |
| Docker image, offline mode, Windows checklist items | do not discover these late |

**Not a weekend.** The language contract is a day; the interpreter lifecycle,
venvs and the display work are the rest of it.

## 9. What I would do next

A spike that answers §5's three questions on macOS, Linux and Windows, and runs
`print("hello")` through a resident interpreter with streamed output. That is the
part that can fail for reasons no amount of design settles — and if `uv` covers the
platforms, everything after it is ordinary work.
