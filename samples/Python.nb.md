# Python in ClrKernel

A `python` cell runs in one resident interpreter per notebook, so a name bound in
one cell is there in the next — the same session model as a Jupyter kernel, and the
same one `#!pwsh` uses.

```python
x = 41
print("x is", x)
```

```python
x + 1
```

A cell ending on an expression shows it, the way a notebook does. A cell ending on a
statement shows nothing.

## No Python required

Nothing has to be installed first. If the machine has no interpreter the kernel
fetches one on the first Python cell and caches it — under
`%LOCALAPPDATA%\clrkernel\python` on Windows, `~/Library/Application Support/clrkernel/python`
on macOS, `~/.local/share/clrkernel/python` on Linux. Nothing is written outside that
directory: no PATH change, no registry, no admin prompt.

A machine that already has `python3` uses it and downloads nothing. To choose the
interpreter yourself:

| Variable | What it does |
|---|---|
| `CLRKERNEL_PYTHON` | Use this interpreter. Nothing is ever downloaded. |
| `CLRKERNEL_PYTHON_AUTO_INSTALL=0` | Never download; fail instead, naming every place looked. |
| `CLRKERNEL_PYTHON_HOME` | Where interpreters and packages are cached. |
| `CLRKERNEL_PYTHON_UV_MIRROR`, `UV_PYTHON_INSTALL_MIRROR` | Internal mirrors, for a network that blocks github.com. |
| `UV_OFFLINE=1` | Install only from what is already cached. Warm the cache once, then work with no network. |

Proxies work with no configuration — `HTTP_PROXY` and friends are read by both
halves. A network that re-signs TLS is retried against the machine's own certificate
store.

## Packages

`#!python-install` installs into a directory belonging to this notebook's folder —
not into the interpreter, so two notebooks cannot fight over a version, and nothing
lands in your repo.

```python
#!python-install pandas matplotlib
```

Install in one cell, import in the next: the interpreter is not restarted, so
everything the notebook had bound is still there.

Versions can be pinned, and for a notebook anyone will re-run later they should be —
`#!python-install pandas` resolves whatever is newest on the day it runs, so the same
notebook can behave differently six months from now with nothing in the file to
explain why:

```python
#!python-install pandas==3.0.5
```

Given no packages, `#!python-install` on its own reads `requirements.txt` beside the
notebook — the usual place to keep those pins. That file is what belongs in git; the
installed bytes never do.

## What a cell shows

A DataFrame renders as the interactive grid, with numeric columns typed as numbers
so they sort and summarise as numbers:

```python
import pandas as pd

pd.DataFrame({
    "city": ["Oslo", "Lima", "Perth"],
    "pop": [709037, 9751000, 2192229],
})
```

A matplotlib figure renders as an image. Figures the cell drew but never returned
are shown too, so the usual style works:

```python
import matplotlib.pyplot as plt

plt.plot([1, 4, 9, 16])
plt.title("squares");
```

## Restarting

```python
#!python-reset
```

Drops the interpreter. The next cell gets a fresh one with nothing defined.
