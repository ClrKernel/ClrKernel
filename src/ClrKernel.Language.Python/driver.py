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
import builtins
import importlib
import inspect
import io
import json
import keyword
import os
import re
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


# --- editor features -------------------------------------------------------
#
# From the live namespace, not from static analysis: after a cell runs, `df` IS a
# DataFrame and `dir()` knows every method it has, including ones a type checker
# could never infer. The same reason `#!pwsh` completes from its runspace.

_CHAIN = re.compile(r"([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\.([A-Za-z_][A-Za-z0-9_]*)?$")
_WORD = re.compile(r"[A-Za-z_][A-Za-z0-9_]*$")
_CALLEE = re.compile(r"([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)\s*$")


def _lookup(chain):
    """A dotted name resolved against the live namespace — or None.

    `getattr` only, never `eval`: completing after `fetch_all().` must not run
    `fetch_all()`. That is why the regexes above accept a plain name chain and
    nothing else — no calls, no subscripts.
    """
    parts = chain.split(".")
    if parts[0] in _namespace:
        obj = _namespace[parts[0]]
    elif hasattr(builtins, parts[0]):
        obj = getattr(builtins, parts[0])
    else:
        return None
    for part in parts[1:]:
        try:
            obj = getattr(obj, part)
        except Exception:
            return None
    return obj


def _kind(obj):
    if inspect.ismodule(obj):
        return "module"
    if inspect.isclass(obj):
        return "type"
    if callable(obj):
        return "function"
    return "variable"


def _detail(name, obj):
    """What the item is, preferring its signature — the useful half of a hover."""
    if callable(obj):
        try:
            return name + str(inspect.signature(obj))
        except (TypeError, ValueError):
            pass  # many builtins have no introspectable signature
    return type(obj).__name__


def _members(obj, prefix):
    items = []
    for name in dir(obj):
        # Dunders and privates only when they were asked for by name.
        if name.startswith("_") and not prefix.startswith("_"):
            continue
        if not name.startswith(prefix):
            continue
        try:
            member = getattr(obj, name)
        except Exception:
            # A property that raises is still a name worth offering.
            items.append({"label": name, "kind": "variable", "detail": ""})
            continue
        items.append({"label": name, "kind": _kind(member), "detail": _detail(name, member)})
    return items


def _complete(code, offset):
    head = code[:offset]
    dotted = _CHAIN.search(head)
    if dotted:
        prefix = dotted.group(2) or ""
        obj = _lookup(dotted.group(1))
        items = _members(obj, prefix) if obj is not None else []
        return {"start": offset - len(prefix), "length": len(prefix), "items": items}

    word = _WORD.search(head)
    prefix = word.group(0) if word else ""
    if head[: len(head) - len(prefix)].rstrip().endswith("."):
        # A member access whose left side is not a plain name chain — a call, a
        # subscript, a literal. Offering every global here would be nonsense, and
        # resolving it properly would mean running the expression.
        return {"start": offset - len(prefix), "length": len(prefix), "items": []}

    seen = set()
    items = []
    for source in (_namespace, vars(builtins)):
        for name, value in list(source.items()):
            if name in seen or name.startswith("__") or not name.startswith(prefix):
                continue
            seen.add(name)
            items.append({"label": name, "kind": _kind(value), "detail": _detail(name, value)})
    for word_ in keyword.kwlist:
        if word_.startswith(prefix) and word_ not in seen:
            items.append({"label": word_, "kind": "keyword", "detail": "keyword"})
    return {"start": offset - len(prefix), "length": len(prefix), "items": items}


def _hover(code, offset):
    start = offset
    while start > 0 and (code[start - 1].isalnum() or code[start - 1] in "._"):
        start -= 1
    end = offset
    while end < len(code) and (code[end].isalnum() or code[end] == "_"):
        end += 1
    chain = code[start:end].strip(".")
    if not chain:
        return None
    obj = _lookup(chain)
    if obj is None:
        return None
    lines = ["```python", _detail(chain.split(".")[-1], obj), "```"]
    doc = inspect.getdoc(obj)
    if doc:
        # The summary, not the whole manual — a hover card is a few lines.
        lines.append(doc.strip().split("\n\n")[0])
    return {"markdown": "\n".join(lines), "start": start, "length": end - start}


def _signature(code, offset):
    """The call being typed: walk back to the innermost unclosed `(`."""
    depth = 0
    commas = 0
    i = offset - 1
    while i >= 0:
        c = code[i]
        if c in ")]}":
            depth += 1
        elif c in "([{":
            if depth == 0:
                if c != "(":
                    return None
                break
            depth -= 1
        elif c == "," and depth == 0:
            commas += 1
        i -= 1
    if i < 0:
        return None
    callee = _CALLEE.search(code[:i])
    if not callee:
        return None
    obj = _lookup(callee.group(1))
    if obj is None or not callable(obj):
        return None
    try:
        signature = inspect.signature(obj)
    except (TypeError, ValueError):
        return None
    name = callee.group(1).split(".")[-1]
    return {
        "signatures": [{
            "label": name + str(signature),
            "parameters": [{"label": str(p)} for p in signature.parameters.values()],
        }],
        "active": 0,
        "activeParameter": commas,
    }


_SERVICES = {"complete": _complete, "hover": _hover, "signature": _signature}


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
    if _message.get("t") in _SERVICES:
        # Editor features never fail a cell: an introspection that raises is a
        # missing completion, not a broken interpreter.
        try:
            _reply = _SERVICES[_message["t"]](
                base64.b64decode(_message.get("code", "")).decode("utf-8"),
                int(_message.get("offset", 0)))
        except Exception:
            _reply = None
        _send({"t": "service", "d": json.dumps(_reply)})
        _send({"t": "done", "status": "ok"})
        continue
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
