#!/usr/bin/env python3
"""Jupyter complete_request / inspect_request test over real ZeroMQ.
Starts the clrkernel-dev kernel, executes a cell, then verifies tab-completion
and inspection reflect the executed state."""
import sys
from jupyter_client.manager import start_new_kernel

passed = failed = 0
def check(name, ok):
    global passed, failed
    print(("PASS " if ok else "FAIL ") + name)
    if ok: passed += 1
    else: failed += 1

def reply_of(kc, expected):
    while True:
        msg = kc.get_shell_msg(timeout=30)
        if msg["header"]["msg_type"] == expected:
            return msg["content"]

km, kc = start_new_kernel(kernel_name="clrkernel-dev")
try:
    kc.execute('var greeting = "hello"; var count = 42;')
    reply_of(kc, "execute_reply")

    kc.complete("greeting.", 9)
    c = reply_of(kc, "complete_reply")
    check("complete_reply status ok", c.get("status") == "ok")
    check("completion has executed var members (ToUpper)", "ToUpper" in c.get("matches", []))
    check("cursor_start marks the dot boundary", c.get("cursor_start") == 9)

    kc.complete("coun", 4)
    c2 = reply_of(kc, "complete_reply")
    check("completion of executed identifier (count)", "count" in c2.get("matches", []))

    kc.inspect("greeting", 3)
    i = reply_of(kc, "inspect_reply")
    check("inspect found", i.get("found") is True)
    check("inspect reports string type", "string" in i.get("data", {}).get("text/plain", ""))
finally:
    km.shutdown_kernel()

print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
