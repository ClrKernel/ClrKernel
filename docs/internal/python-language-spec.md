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

## 9. The spike — run 2026-09-02, macOS 26.6 arm64

Everything §5 asked, plus a resident interpreter end to end. **The approach holds.**

**Platforms.** uv 0.12.9 publishes **18 archives**, covering every RID this needs and
then some: `aarch64-apple-darwin`, `x86_64-apple-darwin`,
`{x86_64,aarch64}-unknown-linux-gnu`, both **musl** variants (Alpine, which the
Studio image would care about), and `{x86_64,aarch64,i686}-pc-windows-msvc`.

**Licence.** Apache-2.0 (dual MIT / Apache-2.0 in the repo). Redistributable.

**Provisioning, with no system Python anywhere:**

```
$ uv python install 3.13
Downloading cpython-3.13.15-macos-aarch64-none (24.0MiB)
Installed Python 3.13.15 in 1.76s
```

**Gatekeeper — the risk that did not materialise.** Neither the downloaded `uv` nor
the interpreter carries `com.apple.quarantine`; both have only
`com.apple.provenance`, which does not block execution. Both ran with no prompt.
(Caveat: fetched with `curl`. A browser download would quarantine, so the kernel
must fetch it itself — which it would.)

**venv and requirements.** `uv venv` + `uv pip install pandas` resolved and
installed in seconds. Everything was contained to a scratch directory via
`UV_PYTHON_INSTALL_DIR` and `UV_CACHE_DIR`, which is exactly the knob a
per-notebook venv needs.

**Air-gapped.** `--offline` works off the cache: a venv on a cached interpreter and
an install of a cached package both succeed. Something never fetched fails with a
message that says why — *"Packages were unavailable because the network was
disabled"*. So the escape hatch is real: warm the cache, then run offline.

**The resident interpreter.** A ~20-line `driver.py` reading JSON cells from stdin,
`exec` into one namespace, user output on stdout and protocol on stderr; a C# host
driving it. Timestamps from the run:

```
[ 0.07s] protocol:    interpreter ready, python 3.13.15
[ 8.47s] cell stdout: pandas 3.0.5
[ 8.47s] cell stdout: tick 0
[ 9.47s] cell stdout: tick 1
[10.48s] cell stdout: tick 2
[11.49s] cell stdout: x + 1 = 42          <- x was set two cells earlier
[11.49s] protocol:    cell error           <- raise ValueError
[11.49s] cell stdout: still alive after the error
```

State persists across cells, output **streams** (the ticks are a second apart, not
delivered in a lump at the end), an error is reported without killing the session,
and `Kill(entireProcessTree)` ends a run — the same cancellation mechanism the
kernel already relies on.

### The one thing the spike found that design would not have

**The done-marker raced the output.** `protocol: cell ok` for the first cell
arrived *before* that cell's `pandas 3.0.5` line. stdout and stderr are separate
pipes with independent delivery, so "the cell finished" can reach the host before
the cell's last output does. Treat `done` as "all output received" and a notebook
will silently drop the tail of a cell.

Fixes: multiplex both onto one channel (framed messages on stdout, user output as
a message type), or have the driver emit an explicit flush-and-sync marker on the
*same* stream as the output before it reports done. **One channel is the smaller
idea** and is what the next iteration should do — it also removes the JSON-on-
stderr ambiguity entirely.

### Numbers worth budgeting

| | |
|---|---|
| `uv` binary | 35 MB per platform |
| CPython 3.13 | 24 MB download, per platform |
| First `import pandas` | ~8s cold, then instant |

Fetched on first `#!python` cell, cached thereafter — the tool itself stays 67 MB.

## 10. What is left

The unknowns are gone; what remains is ordinary work. In rough order:

1. **One channel for the cell protocol** (see above), then `ClrKernel.Language.Python`
   with a `PythonSession` holding the resident process — modelled on Studio's
   `KernelProcess`/`NotebookSession`, which is the precedent for a long-lived child
   over stdio, *not* `PowerShellSession`, which is in-process.
2. Interpreter provisioning behind an interface, with "point at your own Python" as
   the documented escape hatch for air-gapped machines and for people who already
   have one.
3. venv per notebook: where it lives, git-ignoring it, and making sure Studio never
   promotes a `.venv/` between branches.
4. Display adapters — DataFrame → `DisplayTable`, matplotlib → `DisplayBytes`. This
   is the half users would actually notice.
5. Docker, offline, and the Windows checklist items.
