#!/usr/bin/env python3
"""Content-Length framed JSON-RPC harness for ClrKernel.Server over stdio."""
import json, subprocess, sys, threading, queue, time

SERVER = sys.argv[1]

proc = subprocess.Popen(["dotnet", SERVER], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE)

incoming = queue.Queue()

def reader():
    buf = proc.stdout
    while True:
        headers = {}
        line = buf.readline()
        if not line:
            return
        while line.strip():
            k, _, v = line.decode().partition(":")
            headers[k.strip().lower()] = v.strip()
            line = buf.readline()
        length = int(headers.get("content-length", 0))
        body = buf.read(length)
        incoming.put(json.loads(body))

threading.Thread(target=reader, daemon=True).start()

_id = 0
def request(method, params=None):
    global _id
    _id += 1
    msg = {"jsonrpc": "2.0", "id": _id, "method": method}
    if params is not None:
        msg["params"] = params
    raw = json.dumps(msg).encode()
    proc.stdin.write(f"Content-Length: {len(raw)}\r\n\r\n".encode() + raw)
    proc.stdin.flush()
    return _id

notifications = []

def params_of(n):
    p = n.get("params", {})
    return p[0] if isinstance(p, list) and p else p

def response_for(rid, timeout=90):
    # Collect notifications on the side instead of dropping them.
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            msg = incoming.get(timeout=1)
        except queue.Empty:
            continue
        if msg.get("id") == rid:
            return msg
        if "id" not in msg:
            notifications.append(msg)
    raise TimeoutError("no response for id " + str(rid))

def drain_notifications():
    while True:
        try:
            m = incoming.get_nowait()
            if "id" not in m:
                notifications.append(m)
        except queue.Empty:
            break
    out = list(notifications)
    notifications.clear()
    return out

results = []

# 1. initialize
r = response_for(request("initialize"))
results.append(("initialize", r["result"]["name"] == "ClrKernel.Server"))

# 2. define state in cell 1
r = response_for(request("execute", {"cellId": "c1", "code": "var x = 21;"}))
results.append(("cell1 ok", r["result"]["status"] == "ok"))

# 3. use state in cell 2 + console output
r = response_for(request("execute", {"cellId": "c2", "code": "Console.WriteLine(\"answer=\" + (x * 2));"}))
time.sleep(1.0)
notes = drain_notifications()
console_hits = [n for n in notes if n.get("method") == "display"
                and "answer=42" in json.dumps(params_of(n))]
results.append(("cell2 ok", r["result"]["status"] == "ok"))
results.append(("console display notification (answer=42)", len(console_hits) >= 1))

# 4. DisplayAs + Update: expect display then updateDisplay with same display_id
r = response_for(request("execute", {"cellId": "c3",
    "code": "var dv = \"working\".DisplayAs(\"text/html\"); dv.Update(\"<b>done</b>\");"}))
time.sleep(1.0)
notes = drain_notifications()
disp = [n for n in notes if n.get("method") == "display" and params_of(n).get("cellId") == "c3"]
upd = [n for n in notes if n.get("method") == "updateDisplay" and params_of(n).get("cellId") == "c3"]
same_id = (disp and upd and
           params_of(disp[0])["transient"].get("display_id") == params_of(upd[0])["transient"].get("display_id"))
results.append(("display + updateDisplay with matching display_id", bool(same_id)))
results.append(("update content", bool(upd) and "<b>done</b>" in json.dumps(params_of(upd[0]))))

# 5. cell result value (expression result comes back in the response)
r = response_for(request("execute", {"cellId": "c4", "code": "x + 21"}))
data = r["result"].get("data") or {}
results.append(("expression result in response", "42" in json.dumps(data)))

# 6. error cell
r = response_for(request("execute", {"cellId": "c5", "code": "throw new InvalidOperationException(\"boom\");"}))
err = r["result"]
results.append(("error status + message", err["status"] == "error" and "boom" in err["error"]["message"]))

# 7. markdown import through the server (executable markdown end to end)
import os, tempfile
md = os.path.join(tempfile.mkdtemp(), "lib.md")
open(md, "w").write("# Doc\n\nprose\n\n```csharp\npublic static class FromMd { public static int V = 7; }\n```\n")
r = response_for(request("execute", {"cellId": "c6", "code": f"#!import \"{md}\"\nConsole.WriteLine(\"md=\" + FromMd.V);"}))
time.sleep(1.0)
notes = drain_notifications()
md_hits = [n for n in notes if "md=7" in json.dumps(params_of(n))]
results.append(("executable markdown import via server", r["result"]["status"] == "ok" and len(md_hits) >= 1))

# 8. shutdown
request("shutdown")
proc.wait(timeout=10)
results.append(("clean shutdown", proc.returncode == 0))

failed = [n for n, ok in results if not ok]
for name, ok in results:
    print(("PASS " if ok else "FAIL ") + name)
sys.exit(1 if failed else 0)
