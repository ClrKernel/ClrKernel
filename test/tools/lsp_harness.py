#!/usr/bin/env python3
"""LSP-over-stdio harness for the ClrKernel unified language server.
Usage: lsp_harness.py <path/to/ClrKernel.dll> — launches `dotnet ClrKernel.dll lsp`.

Verifies the Option-A premise: a cell executed over clrkernel/execute is visible
to textDocument/completion, plus hover and signature help."""
import json, subprocess, sys, threading, queue

SERVER = sys.argv[1]
proc = subprocess.Popen(["dotnet", SERVER, "lsp"], stdin=subprocess.PIPE,
                        stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)

responses, notifications = queue.Queue(), queue.Queue()

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
        msg = json.loads(buf.read(length))
        (responses if "id" in msg else notifications).put(msg)

threading.Thread(target=reader, daemon=True).start()

_id = 0
def send(method, params, notify=False):
    global _id
    msg = {"jsonrpc": "2.0", "method": method, "params": params}
    if not notify:
        _id += 1
        msg["id"] = _id
    raw = json.dumps(msg).encode()
    proc.stdin.write(f"Content-Length: {len(raw)}\r\n\r\n".encode() + raw)
    proc.stdin.flush()
    return _id if not notify else None

def request(method, params):
    rid = send(method, params)
    while True:
        msg = responses.get(timeout=30)
        if msg.get("id") == rid:
            return msg.get("result")

passed = failed = 0
def check(name, ok):
    global passed, failed
    print(("PASS " if ok else "FAIL ") + name)
    if ok: passed += 1
    else: failed += 1

# 1. initialize
init = request("initialize", {"capabilities": {}})
caps = (init or {}).get("capabilities", {})
check("initialize advertises completion", bool(caps.get("completionProvider")))
check("initialize advertises hover", caps.get("hoverProvider") is True)
check("initialize advertises signature help", bool(caps.get("signatureHelpProvider")))
send("initialized", {}, notify=True)

# 1b. the language descriptors ride the handshake and the dedicated request
exp = caps.get("experimental", {}).get("clrkernel", {})
langs = {l.get("id"): l for l in exp.get("languages", [])}
check("handshake carries language descriptors", "sql" in langs and "dax" in langs)
check("descriptors carry language tags", "tsql" in langs.get("sql", {}).get("languageTags", []))
check("descriptors carry directives", any(
    d.get("selector") == "#!sql-connect" for d in langs.get("sql", {}).get("directives", [])))

req_langs = request("clrkernel/languages", {})
check("clrkernel/languages answers with the same ids",
      {l.get("id") for l in (req_langs or {}).get("languages", [])} == set(langs))

# 1c. connection providers describe their settings schemas
described = request("clrkernel/connections/describe",
                    {"languageId": "sql", "notebookUri": "file:///tmp/harness.nb.md"})
providers = {p.get("type"): p for p in (described or {}).get("providers", [])}
check("sql describes the SqlServer provider", "SqlServer" in providers)
sql_settings = {s.get("name"): s for s in providers.get("SqlServer", {}).get("settings", [])}
check("provider settings carry enum auth modes", "entra" in sql_settings.get("auth", {}).get("enumValues", []))
check("passwords are secretRef settings", sql_settings.get("password", {}).get("kind") == "secretRef")

# Sessions are per notebook, keyed by the notebook path in each cell URI (the
# part before '#') — exactly what VS Code cell URIs look like. Every cell here
# shares one notebook so executed state is visible to language features, the
# same way it is in the editor.
def cell(fragment):
    return f"vscode-notebook-cell:/tmp/clrkernel-harness.nb.md#{fragment}"

# 2. execute a cell — its state must become visible to completion
ex = request("clrkernel/execute", {"cellId": cell("c1"), "code": 'var greeting = "hello"; var count = 42;'})
check("execute returns ok", (ex or {}).get("status") == "ok")

def open_doc(uri, text):
    send("textDocument/didOpen", {"textDocument": {"uri": uri, "languageId": "csharp", "version": 1, "text": text}}, notify=True)

def labels(uri, text, line, ch):
    open_doc(uri, text)
    res = request("textDocument/completion",
                  {"textDocument": {"uri": uri}, "position": {"line": line, "character": ch}})
    items = res.get("items", []) if isinstance(res, dict) else (res or [])
    return [i["label"] for i in items]

# 3. completion sees the executed variable, by member
member = labels(cell("mem1"), "greeting.", 0, 9)
check("member completion on executed var (ToUpper)", "ToUpper" in member)
check("member completion on executed var (Length)", "Length" in member)

# 4. completion sees the variable name itself
names = labels(cell("id1"), "coun", 0, 4)
check("identifier completion of executed var (count)", "count" in names)

# 5. BCL completion
bcl = labels(cell("bcl1"), "System.Console.Wri", 0, 18)
check("BCL member completion (WriteLine)", "WriteLine" in bcl)

# 6. hover on the executed variable
open_doc(cell("hov1"), "greeting")
hov = request("textDocument/hover", {"textDocument": {"uri": cell("hov1")}, "position": {"line": 0, "character": 3}})
hov_text = (hov or {}).get("contents", {}).get("value", "") if hov else ""
check("hover reports string type", "string" in hov_text and "greeting" in hov_text)

# 7. signature help inside a call
open_doc(cell("sig1"), "System.Console.WriteLine(")
sig = request("textDocument/signatureHelp", {"textDocument": {"uri": cell("sig1")}, "position": {"line": 0, "character": 25}})
sigs = (sig or {}).get("signatures", []) if sig else []
check("signature help lists WriteLine overloads", any("WriteLine" in s.get("label", "") for s in sigs))

send("shutdown", {})
send("exit", {}, notify=True)
print(f"\n{passed} passed, {failed} failed")
sys.exit(1 if failed else 0)
