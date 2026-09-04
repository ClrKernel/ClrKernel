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

**A corporate network.** The machines most likely to have no Python are the ones
least likely to be allowed to download one, so this was worked through rather than
assumed. Four cases, three of which need no code:

| What is in the way | What happens |
|---|---|
| An HTTP proxy | Works. `HttpClient.DefaultProxy` reads `HTTP_PROXY`/`HTTPS_PROXY`/`NO_PROXY`, and so does uv — the child only *adds* two variables, so the environment is inherited whole. |
| TLS re-signed by the corporate root | The .NET half is fine: it validates against the OS store, where that root has to be already or `dotnet tool install` would not have worked either. **uv is not** — it trusts bundled Mozilla roots and never looks at the platform store. So it fails on precisely the machine where NuGet works. |
| github.com blocked, an internal mirror available | `UV_PYTHON_INSTALL_MIRROR` covers the interpreter with no code — uv reads it and inherits it. uv's own release URL is the only hardcoded one, hence `CLRKERNEL_PYTHON_UV_MIRROR`. |
| Nothing may be downloaded at all | `CLRKERNEL_PYTHON` at a Python IT already manages, or a pre-seeded `CLRKERNEL_PYTHON_HOME` plus `CLRKERNEL_PYTHON_AUTO_INSTALL=0`. |
| A firewall that **drops** rather than refuses | The only failure that returns nothing at all: uv retries into a black hole. A 10-minute ceiling — one budget across both attempts, not one each — stops it and kills the process tree, matching the `HttpClient` timeout on the other half. |

The TLS case is handled by retrying **once** with `UV_SYSTEM_CERTS=true` when uv's
failure looks like a certificate rejection — a retry and not the default, because
the platform store is right on a machine that re-signs TLS and wrong on a container
that has no store at all, and only one of those announces itself up front.

Shipping a CPython through NuGet instead was considered and rejected: it means
republishing python-build-standalone as five RID-specific packages at ~25 MB each
and owning CVE response for an interpreter we did not build, to serve a case that
one environment variable already covers.

Verified against a real self-signed HTTPS origin and a local server standing in for
a content filter, not reasoned about:

```
uv did not trust the TLS certificate it was shown — retrying against this machine's own certificate store.
…/uv-aarch64-apple-darwin.tar.gz.sha256 did not return a checksum. Something on the
network answered instead of the server — a proxy, a captive portal or a content filter.
```

That second one matters more than it looks: a filter that answers **200 with a block
page** would otherwise trip the checksum comparison and be reported as a corrupt
download, which sends you to the wrong place entirely.

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

## 9a. Packages, and why there is no venv

The spec above assumed a venv per notebook and then flagged the consequences:
git-ignoring `.venv/`, and stopping Studio promoting one between branches. Both
problems disappear if there is no venv.

`uv pip install --target <dir>` installs into a plain directory. The driver puts
that directory on `sys.path` at startup, so:

- **Install in one cell, import in the next.** A venv would have to *become* the
  interpreter, so installing anything mid-session would mean restarting it and
  losing every name the notebook had bound. Measured: `x` set in cell 1 is still
  there in cell 3, after an install in cell 2.
- **Nothing in the repo.** The directory lives in the kernel's cache, keyed by the
  notebook's directory and hashed so two `notebooks/` folders in different repos
  cannot collide. There is no `.venv/` for git to ignore and nothing for Studio to
  promote. What belongs in the repo is `requirements.txt` — the definition — not
  the installed bytes; the same split as a lock file against the NuGet cache.
- **It works with any interpreter**, including one the user pointed at with
  `CLRKERNEL_PYTHON`, and never writes into it.

`--python <interpreter>` is not optional: it makes uv resolve wheels for the
interpreter that will import them, and a compiled extension built for the wrong ABI
imports and *then* crashes.

The air-gapped claim in §"Air-gapped" is reachable through the directive with no
flag of ours: `UV_OFFLINE=1` is inherited like every other uv variable. Verified
both ways — a cold cache refuses, a warm one installs with the network gone.

Two notebooks in different directories were checked for leakage — installing in one
leaves the other with an `ImportError`, which is the whole point of the isolation.

## 9b. What a cell shows

A cell ending on an expression displays it, like a notebook — the driver splits the
last `ast.Expr` off, `exec`s the rest and `eval`s that one. Then:

| The cell ends on | What appears |
|---|---|
| A pandas DataFrame or Series | `DisplayTable` → the interactive grid, dtypes mapped to the kernel's column kinds so numbers sort and summarise as numbers |
| A matplotlib figure | `DisplayBytes` PNG |
| Anything else | its `repr`, as Jupyter does |
| A statement | nothing |

Figures the cell drew but never returned are drained afterwards, because
`plt.plot(...)` on its own line is how most matplotlib is written and it leaves the
figure open rather than yielding it. `MPLBACKEND=Agg` is set for the interpreter
unless the user chose a backend, since opening a GUI window from a kernel is at
best useless.

Row cap is 1000 with the true count reported, so a million-row frame renders.

## 9c. In a container

Verified by running a cell in `mcr.microsoft.com/dotnet/aspnet:10.0` as the non-root
`app` user, which is how the Studio image runs — not by reasoning about it, and it
is as well:

`Environment.GetFolderPath(LocalApplicationData)` returns **empty** there. HOME is
set to `/home/app` and is writable; `UserProfile` resolves correctly; only that one
API comes back blank. Combined with a relative path it gave `/clrkernel`, which is
absolute, well-formed, and denied at the first download with no hint why. `Home()`
now falls back through `XDG_DATA_HOME`, then `UserProfile/.local/share`, then temp.

The Studio image sets `CLRKERNEL_PYTHON_HOME=/data/python` anyway, so the ~60 MB
interpreter lands on the volume and is not fetched again every time the container is
recreated. `CLRKERNEL_PYTHON` remains the way to use one baked into an image and
download nothing.

The image is Debian, so `IsMusl()` correctly picks the gnu build; the musl branch
still has no run behind it and stays on the checklist.

## 10. What is left

The unknowns are gone; what remains is ordinary work. In rough order:

1. **One channel for the cell protocol** (see above), then `ClrKernel.Language.Python`
   with a `PythonSession` holding the resident process — modelled on Studio's
   `KernelProcess`/`NotebookSession`, which is the precedent for a long-lived child
   over stdio, *not* `PowerShellSession`, which is in-process.
2. Interpreter provisioning behind an interface, with "point at your own Python" as
   the documented escape hatch for air-gapped machines and for people who already
   have one.
3. ~~venv per notebook~~ — **done**, and there is no venv: see §9a.
4. ~~Display adapters~~ — **done**: see §9b.
5. ~~Docker~~ (§9c) and ~~offline~~ (`UV_OFFLINE`, §9a) — **done**. The Windows
   items remain, as §12a of the Windows checklist: the runtime paths that only a
   managed Windows machine can exercise.

Not built, and deliberately: completion and hover inside a Python cell
(`ICellLanguageServices` is null), which wants the interpreter's own introspection
and is a separate piece of work from running cells.
