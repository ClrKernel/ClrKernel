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
import base64
import json
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

_namespace = {"__name__": "__main__", "__builtins__": __builtins__}

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
    _code = base64.b64decode(_message.get("code", "")).decode("utf-8")
    try:
        exec(compile(_code, "<cell>", "exec"), _namespace)
        _send({"t": "done", "status": "ok"})
    except BaseException as _error:
        # Minus this driver's own frame. The top frame is always the `exec` below,
        # which is plumbing: a cell's traceback should start at the cell, the way it
        # would if the user had run the file themselves. `tb_next` drops exactly that
        # one frame and keeps everything the cell actually called.
        _tb = _error.__traceback__
        _lines = traceback.format_exception(type(_error), _error, _tb.tb_next if _tb else None)
        _send({"t": "done", "status": "error", "error": "".join(_lines)})
