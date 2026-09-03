"""The resident interpreter behind `#!python` cells.

One process per notebook session, one namespace, many cells — so a name bound in
one cell is there in the next, the way a notebook is expected to behave.

**Everything goes back on stdout as framed JSON, one object per line.** The
obvious design uses stdout for the cell's own output and stderr for protocol
messages, and it is wrong: they are separate pipes with independent delivery, so
a cell's "finished" can overtake the last line the cell printed and the host
silently drops the tail. Measured, not guessed — it is why this is one channel.

`sys.stdout` and `sys.stderr` are replaced so that `print` inside a cell becomes
a framed message in order with everything else.
"""
import ast
import base64
import importlib
import io
import json
import os
import sys
import traceback

_real_stdout = sys.stdout


def _send(message):
    _real_stdout.write(json.dumps(message) + "\n")
    _real_stdout.flush()


class _Channel:
    """A file-like that frames whatever is written to it."""

    def __init__(self, stream):
        self._stream = stream

    def write(self, text):
        if text:
            _send({"t": "out", "s": self._stream, "d": text})
        return len(text or "")

    def flush(self):
        pass

    def isatty(self):
        # Colour is worth having: the kernel renders ANSI, and the host is not a
        # terminal, so libraries that ask would otherwise strip it.
        return True

    writable = lambda self: True
    readable = lambda self: False
    seekable = lambda self: False


sys.stdout = _Channel("stdout")
sys.stderr = _Channel("stderr")

# Where `#!python-install` puts packages, ahead of everything so a notebook's own
# pandas wins over one that happens to be installed system-wide. Created here rather
# than only on install, because a path added later is a path already cached as absent.
_packages = sys.argv[1] if len(sys.argv) > 1 else None
if _packages:
    os.makedirs(_packages, exist_ok=True)
    sys.path.insert(0, _packages)

_namespace = {"__name__": "__main__", "__builtins__": __builtins__}

_ROW_LIMIT = 1000


def _kind(dtype):
    """A pandas dtype as one of the kinds the kernel's grid sorts by."""
    code = getattr(dtype, "kind", "O")
    if code in "iuf":
        return "number"
    if code in "Mm":
        return "date"
    return "string"


def _cell(value, isna):
    """One cell as display text, or None for missing.

    `str(value)` is not enough: pandas stores a missing number as NaN, so an empty
    cell would render as the literal "nan" rather than blank. `isna` raises or
    returns an array for a list-valued cell, which is not a missing value.
    """
    if value is None:
        return None
    try:
        if isna(value):
            return None
    except (TypeError, ValueError):
        pass
    return str(value)


def _table(frame):
    """A DataFrame as the kernel's table concept, or None if it is not one."""
    isna = sys.modules["pandas"].isna
    columns = [str(c) for c in frame.columns]
    head = frame.head(_ROW_LIMIT)
    # Positional throughout: a frame may have two columns of the same name, and
    # itertuples renames those while `columns` keeps the originals.
    rows = [[_cell(v, isna) for v in row] for row in head.itertuples(index=False)]
    return {
        "t": "display", "kind": "table",
        "columns": columns,
        "types": [_kind(frame.dtypes.iloc[i]) for i in range(len(columns))],
        "rows": rows,
        "total": int(len(frame)),
    }


def _png(figure):
    buffer = io.BytesIO()
    figure.savefig(buffer, format="png", bbox_inches="tight")
    return {
        "t": "display", "kind": "bytes", "mime": "image/png",
        "d": base64.b64encode(buffer.getvalue()).decode("ascii"),
    }


def _display(value):
    """The value a cell ended on, as a display concept — or None to fall back to repr."""
    module = type(value).__module__.split(".")[0]
    if module == "pandas":
        # A Series is a one-column frame; showing it as a grid beats showing its repr.
        if hasattr(value, "columns"):
            return _table(value)
        if hasattr(value, "to_frame"):
            return _table(value.to_frame())
    if hasattr(value, "savefig"):
        return _png(value)
    return None


def _drain_figures():
    """Any figure the cell drew but never returned — `plt.plot(...)` on its own is
    how most matplotlib is written, and it leaves the figure open rather than
    yielding it."""
    pyplot = sys.modules.get("matplotlib.pyplot")
    if pyplot is None:
        return
    for number in pyplot.get_fignums():
        _send(_png(pyplot.figure(number)))
    pyplot.close("all")


def _run(code):
    """Executes a cell, then reports whatever it ended on the way a notebook does:
    the last expression's value, displayed rather than discarded."""
    parsed = ast.parse(code, "<cell>", "exec")
    # A trailing semicolon suppresses the value. Not syntax — the AST is identical
    # either way — but it is what every notebook user reaches for to silence the
    # `Text(0.5, 1.0, ...)` that a matplotlib call returns, so it is checked in the
    # source the way IPython checks it.
    quiet = code.rstrip().endswith(";")
    last = parsed.body.pop() if parsed.body and isinstance(parsed.body[-1], ast.Expr) else None
    if quiet:
        if last is not None:
            parsed.body.append(last)
        last = None
    if parsed.body:
        exec(compile(parsed, "<cell>", "exec"), _namespace)
    if last is None:
        return
    value = eval(compile(ast.Expression(last.value), "<cell>", "eval"), _namespace)
    if value is None:
        return
    message = _display(value)
    _send(message if message else {"t": "out", "s": "stdout", "d": repr(value) + "\n"})

_send({"t": "ready", "version": sys.version.split()[0], "executable": sys.executable})

for _line in sys.stdin:
    _line = _line.strip()
    if not _line:
        continue
    try:
        _message = json.loads(_line)
    except ValueError:
        _send({"t": "done", "status": "error", "error": "the host sent something that is not a message"})
        continue
    if _message.get("t") == "shutdown":
        break
    if _message.get("t") == "refresh":
        # After an install: the import machinery caches each sys.path directory's
        # listing, so a package that appeared underneath a running interpreter is
        # invisible until this is called.
        importlib.invalidate_caches()
        _send({"t": "done", "status": "ok"})
        continue
    _code = base64.b64decode(_message.get("code", "")).decode("utf-8")
    try:
        _run(_code)
        _drain_figures()
        _send({"t": "done", "status": "ok"})
    except BaseException as _error:
        # Minus this driver's own frames. A cell's traceback should start at the
        # cell, the way it would if the user had run the file themselves. Dropped by
        # filename rather than by count: the plumbing above a cell is `_run` and the
        # `exec` inside it today, and a fixed `tb_next` silently leaks one the day
        # that changes — which is exactly how this was found.
        _tb = _error.__traceback__
        while _tb is not None and _tb.tb_frame.f_code.co_filename == __file__:
            _tb = _tb.tb_next
        _lines = traceback.format_exception(type(_error), _error, _tb)
        _drain_figures()
        _send({"t": "done", "status": "error", "error": "".join(_lines)})
