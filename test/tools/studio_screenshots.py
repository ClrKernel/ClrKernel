#!/usr/bin/env python3
"""Capture the Studio screenshots that docs/studio.md embeds.

    python3 test/tools/studio_screenshots.py             # rebuild, capture everything
    python3 test/tools/studio_screenshots.py --no-build   # iterate on a built tree
    python3 test/tools/studio_screenshots.py --only connections,files
    python3 test/tools/studio_screenshots.py --list       # the shot names

Everything is disposable except the PNGs: a temp workspace, a temp data dir, an
admin account created and thrown away, and — for the Connections shots — a
PostgreSQL container from dev/docker-compose.dbs.yml. Nothing touches the
notebooks, the run history or the connections of an instance you are using.

The images are committed rather than generated in CI, because docs/studio.md is
read on GitHub as well as on the docs site, and a file generated into
docs-site/public/ is gitignored — the GitHub copy would show broken images. The
cost is that they go stale unless somebody re-runs this; that is why it is one
command with no arguments.

**Every shot asserts something specific before the shutter.** A screenshot of a
sign-in screen, an error boundary or a cell that has not finished is still a
valid PNG, and the first person to notice would be whoever reads the published
page. If a page is empty here, the fixture is missing something — seed it in
`workspace()` or `seed()` rather than photographing the emptiness.

Requires: playwright (`pip install playwright && playwright install chromium`),
the .NET SDK, node, a network (hello.nb.md restores NuGet and calls httpbin),
and docker for the Connections shots.
"""

import argparse
import json
import os
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STUDIO = os.path.join(REPO, "src", "ClrKernel.Studio")
COMPOSE = os.path.join(REPO, "dev", "docker-compose.dbs.yml")

# --------------------------------------------------------------------------- fixture

# Real sample notebooks, in folders, because the file list is a tree and a flat
# one never shows that. The key is where it lands under the notebooks root.
NOTEBOOKS = {
    "hello.nb.md": "hello.nb.md",
    "MermaidDiagrams.nb.md": "reports/MermaidDiagrams.nb.md",
    "HttpRequests.nb.md": "checks/HttpRequests.nb.md",
    "Shell.nb.md": "checks/Shell.nb.md",
    "Sql.nb.md": "ingest/Sql.nb.md",
    "SqlEtl.nb.md": "ingest/SqlEtl.nb.md",
}

# A jobs file is named for the notebook it schedules — `hello.jobs.yaml` schedules
# `hello.nb.md`. Crons are daily or weekly on purpose: a `*/5` would fire in the
# middle of the shoot and put a running job in a screenshot that is supposed to
# be the same every time.
JOBS = {
    "hello.jobs.yaml": 'jobs:\n  - name: hello-hourly\n    cron: "0 * * * *"\n',
    "reports/MermaidDiagrams.jobs.yaml":
        'jobs:\n  - name: diagrams-nightly\n    cron: "0 2 * * *"\n'
        '  - name: diagrams-weekly\n    cron: "0 3 * * 1"\n',
    "checks/HttpRequests.jobs.yaml": 'jobs:\n  - name: api-checks\n    cron: "0 6 * * *"\n',
}

# Run history, so the dashboard is a dashboard and not four zeroes. Only jobs that
# actually pass are seeded: Shell.nb.md ssh's to an example host and Sql*.nb.md want
# a database, so scheduling them is realistic but running them is not.
SEED_RUNS = [("hello-hourly", "test"), ("diagrams-nightly", "test"),
             ("hello-hourly", "prod"), ("diagrams-weekly", "test")]

# The demo warehouse behind the Connections shots. Small enough to read in a
# screenshot, wide enough that the tree has something to expand.
DEMO_SQL = """
DROP SCHEMA IF EXISTS sales CASCADE;
CREATE SCHEMA sales;
CREATE TABLE sales.customers (id int primary key, name text, country text);
CREATE TABLE sales.products  (id int primary key, name text, unit_price numeric);
CREATE TABLE sales.orders (
    id int primary key, customer_id int references sales.customers,
    product_id int references sales.products, quantity int, placed_at date);
CREATE VIEW sales.order_lines AS
    SELECT o.id, c.name AS customer, p.name AS product,
           o.quantity, p.unit_price * o.quantity AS total, o.placed_at
    FROM sales.orders o
    JOIN sales.customers c ON c.id = o.customer_id
    JOIN sales.products  p ON p.id = o.product_id;
INSERT INTO sales.customers VALUES
    (1,'Northwind Trading','GB'), (2,'Meridian Foods','US'),
    (3,'Kestrel Supply','DE'), (4,'Bellweather Ltd','GB');
INSERT INTO sales.products VALUES
    (1,'Espresso beans 1kg', 18.50), (2,'Filter papers x200', 4.25),
    (3,'Grinder burrs', 62.00), (4,'Tamper 58mm', 27.90);
INSERT INTO sales.orders VALUES
    (1,1,1,12,'2026-08-03'), (2,2,3,2,'2026-08-04'), (3,1,2,40,'2026-08-07'),
    (4,3,4,6,'2026-08-11'), (5,4,1,25,'2026-08-14'), (6,2,2,90,'2026-08-18'),
    (7,3,1,8,'2026-08-21'), (8,4,3,1,'2026-08-25');
"""

DEMO_QUERY = """SELECT customer, product, quantity, total, placed_at
FROM sales.order_lines
ORDER BY placed_at DESC"""

# The password is a throwaway for a container bound to localhost. It still travels
# as a secret *reference*: the server resolves CLRKERNEL_SECRET_DEMO, which is the
# rule every connection in this repo follows.
DEMO_DSN = dict(host="localhost", port="55432", database="clrkernel_studio",
                user="postgres", password="devonly", secret_ref="DEMO")


def sh(args, **kw):
    """Run a command, letting its output through. A silenced build in a harness
    keeps serving the last binary that compiled — see the studio-webapp-dev skill."""
    print(f"$ {' '.join(args)}", flush=True)
    subprocess.run(args, check=True, cwd=REPO, **kw)


def studio(*args, data, notebooks):
    sh(["dotnet", "run", "--project", STUDIO, "-f", "net8.0", "--no-build", "--",
        *args, "--notebooks", notebooks, "--data-dir", data, "--store", "sqlite"])


def wait_for(url, timeout=90):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(url, timeout=2) as r:
                return json.load(r)
        except (urllib.error.URLError, OSError, json.JSONDecodeError):
            time.sleep(1)
    raise SystemExit(f"studio never answered {url}")


def workspace(root):
    """A notebooks root and a data dir, seeded and git-initialised."""
    # `git init` names the project after the folder, and the name is in the
    # breadcrumb of every screenshot — so it is not called "nb".
    nb, data = os.path.join(root, "analytics"), os.path.join(root, "data")
    os.makedirs(nb), os.makedirs(data)
    for sample, dest in NOTEBOOKS.items():
        target = os.path.join(nb, dest)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copy(os.path.join(REPO, "samples", sample), target)
    for name, body in JOBS.items():
        target = os.path.join(nb, name)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        with open(target, "w") as f:
            f.write(body)
    studio("git", "init", data=data, notebooks=nb)
    ahead(os.path.join(nb, "test"))
    for job, env in SEED_RUNS:
        studio("run", job, "--env", env, data=data, notebooks=nb)
    return nb, data


def ahead(worktree):
    """Commit one edit on `test` so it is ahead of `prod`.

    `git init` adopts everything and promotes it, so the two branches start
    identical and "Diff vs production" correctly says so — which is a screenshot of
    a sentence. The diff compares the two branches that *run*, not your own, so the
    change has to be committed on test. That is what the Push to test button does;
    doing it with git here keeps the fixture to one step.
    """
    nb = os.path.join(worktree, "hello.nb.md")
    with open(nb) as f:
        body = f.read()
    body = body.replace(
        'var greeting = "Hello from ClrKernel";\nConsole.WriteLine(greeting);',
        'var greeting = "Hello from ClrKernel";\nvar runAt = DateTime.UtcNow;\n'
        'Console.WriteLine($"{greeting} at {runAt:HH:mm}");')
    body += ("\n## Row counts\n\nA cell that is on test and not yet in production.\n\n"
             "```csharp\nConsole.WriteLine($\"{DateTime.UtcNow:yyyy-MM-dd}: 4 feeds checked\");\n```\n")
    with open(nb, "w") as f:
        f.write(body)
    sh(["git", "-c", "user.name=Ada Lovelace", "-c", "user.email=ada@example.com",
        "-C", worktree, "commit", "-am", "Report row counts after each run"])


def database():
    """Start the dev PostgreSQL and fill it with the demo warehouse.

    Returns True when the Connections shots can be taken. Postgres rather than SQL
    Server because it starts in seconds, and a screenshot does not care which
    dialect it is looking at.
    """
    try:
        sh(["docker", "compose", "-f", COMPOSE, "up", "-d", "postgres"])
    except (subprocess.CalledProcessError, FileNotFoundError):
        return False
    for _ in range(60):
        ready = subprocess.run(
            ["docker", "compose", "-f", COMPOSE, "exec", "-T", "postgres",
             "pg_isready", "-U", "postgres"], cwd=REPO, capture_output=True)
        if ready.returncode == 0:
            break
        time.sleep(1)
    else:
        return False
    subprocess.run(
        ["docker", "compose", "-f", COMPOSE, "exec", "-T", "postgres",
         "psql", "-U", "postgres", "-d", DEMO_DSN["database"], "-v", "ON_ERROR_STOP=1"],
        cwd=REPO, input=DEMO_SQL.encode(), check=True, capture_output=True)
    return True


# --------------------------------------------------------------------------- shots

SHOT = {"width": 1440, "height": 900}
SHOTS = []


def shot(name, height=None, needs_db=False):
    """Register a capture. `height` trims the frame for a page that does not fill
    900px, because half a screenshot of empty background reads as an empty app.

    Each function navigates and returns the selector that proves the page arrived.
    """
    def register(fn):
        SHOTS.append((name, fn, height, needs_db))
        return fn
    return register


class Studio:
    """The signed-in page, plus the ids the shots need to build URLs."""

    def __init__(self, page, base, out):
        self.page, self.base, self.out = page, base, out
        self.slug = self.api("/projects")["projects"][0]["slug"]
        runs = self.api("/runs?take=5")["runs"]
        self.run_id = runs[0]["id"] if runs else None
        self.connection_id = None

    def api(self, path, method="GET", body=None):
        return self.page.evaluate(
            """async ([path, method, body]) => {
                 const r = await fetch('/api' + path, {
                   method, headers: {'Content-Type': 'application/json'},
                   body: body == null ? undefined : JSON.stringify(body) });
                 const text = await r.text();
                 if (!r.ok) throw new Error(path + ' -> ' + r.status + ' ' + text);
                 return text ? JSON.parse(text) : null;
               }""", [path, method, body])

    def go(self, path):
        self.page.goto(self.base + path, wait_until="networkidle")

    def edit(self, view, branch, path):
        self.go(f"/files/{self.slug}/{view}/{branch}/{path}")


@shot("dashboard")
def dashboard(s):
    s.go("/")
    return "text=Up next"


@shot("monitoring")
def monitoring(s):
    s.go("/monitoring")
    # A row, not the heading: the grid renders its chrome before the runs arrive.
    return "table tbody tr"


@shot("notifications", height=760)
def notifications(s):
    s.go("/notifications")
    return "text=ops-webhook"


@shot("channels", height=720)
def channels(s):
    s.go("/channels")
    return "text=ops-webhook"


@shot("settings")
def settings(s):
    s.go("/settings")
    return "text=Passkeys"


@shot("files", height=620)
def files(s):
    s.go(f"/files/{s.slug}")
    return "text=hello.nb.md"


@shot("job")
def job(s):
    s.edit("overview", "test", "hello.jobs.yaml")
    # Not the job's name: that is an input's value, which `text=` does not see.
    # "Run now" only renders once a job card is on the page.
    return "text=Run now"


@shot("run")
def run(s):
    if not s.run_id:
        raise SystemExit("no runs to open — the seeded runs did not reach the store")
    s.go(f"/runs/{s.run_id}")
    return "text=Cells"


def run_notebook(s):
    """Run hello.nb.md and wait for it. The kernel session stays warm, so the
    editor shots after the first one reuse the outputs this leaves behind."""
    run_all = 'button[aria-label="Run all cells"]'
    s.page.wait_for_selector(".monaco-editor", timeout=30000)
    if s.page.query_selector(".output-html, .output-text"):
        return
    s.page.click(run_all)
    # The button disables for the duration, which is the one signal that does not
    # depend on reading a status word. Waiting for an `svg` instead matches the
    # toolbar icons and screenshots a pending cell — that has already happened here.
    s.page.wait_for_selector(f"{run_all}[disabled]", timeout=30000)
    s.page.wait_for_selector(f"{run_all}:not([disabled])", timeout=300000)  # NuGet restore


@shot("editor-normal")
def editor_normal(s):
    s.edit("edit", "test", "hello.nb.md")
    run_notebook(s)
    # Output, not just an editor: a notebook with no results is a text editor.
    return ".output-html, .output-text"


@shot("focus-mode")
def focus_mode(s):
    s.edit("edit", "test", "hello.nb.md")
    run_notebook(s)
    s.page.get_by_role("radio", name="Focus").click()
    s.page.get_by_text("graph LR").click()
    # `.focus-empty` *is* "No output — run this cell to see results."
    s.page.wait_for_selector(".focus-empty", state="detached", timeout=30000)
    # Mermaid renders client-side inside a sandboxed iframe, so the diagram is
    # never a node of the parent document.
    s.page.frame_locator(".focus-output-pane iframe").locator(
        "svg").first.wait_for(timeout=60000)
    return ".focus-output-pane"


@shot("editor-source")
def editor_source(s):
    s.edit("source", "test", "hello.nb.md")
    return ".monaco-editor"


@shot("editor-diff")
def editor_diff(s):
    # Always prod (left) against test (right), whichever branch you are on — so
    # what makes this shot possible is `ahead()`, not anything done here.
    s.edit("diff", "test", "hello.nb.md")
    return ".monaco-diff-editor"


@shot("connections", needs_db=True)
def connections(s):
    s.go(f"/connections/{s.connection_id}")
    # Collapsed, the tree is one row — and browsing a live database is half of what
    # this area does. Each level is a round trip to the server, so open them one at
    # a time and wait: the disclosure button relabels itself Collapse when it lands.
    # `Tables` is matched on its prefix: the tree labels it with a count.
    for label in [f'="Expand Warehouse (dev)"', f'="Expand {DEMO_DSN["database"]}"',
                  '="Expand sales"', '^="Expand Tables"']:
        s.page.click(f"button[aria-label{label}]", timeout=60000)
        s.page.wait_for_selector(
            f"button[aria-label{label.replace('Expand', 'Collapse')}]", timeout=60000)
    s.page.wait_for_selector("text=customers", timeout=60000)
    editor = ".monaco-editor"
    s.page.wait_for_selector(editor, timeout=30000)
    s.page.click(editor)
    # No parentheses or quotes in DEMO_QUERY: Monaco auto-closes them and would
    # type a different query than the one written here.
    s.page.keyboard.type(DEMO_QUERY)
    s.page.get_by_role("button", name="Run").first.click()
    return "table tbody tr"


def seed(s, with_db):
    """Everything a page needs so it is not photographed empty."""
    s.api("/channels", "PUT", {"channels": [
        {"name": "ops-webhook", "type": "webhook",
         "url": "https://chat.example.com/hooks/clrkernel", "bearerSecretRef": "OPS_HOOK"},
        {"name": "data-team", "type": "email", "host": "smtp.example.com", "port": 587,
         "from": "studio@example.com", "to": ["data-team@example.com"],
         "user": "studio@example.com", "passwordSecretRef": "SMTP"},
    ]})
    s.api("/notification-rules", "PUT", [
        {"event": "JobFailed", "to": ["ops-webhook", "data-team"], "enabled": True},
        {"event": "JobRecovered", "to": ["ops-webhook"], "enabled": True},
        {"event": "RunTooSlow", "environment": "prod", "to": ["data-team"],
         "afterSeconds": 900, "enabled": True},
        {"event": "PromotedToProd", "to": ["ops-webhook"], "enabled": True},
    ])
    if with_db:
        saved = s.api("/connections", "POST", {
            "name": "Warehouse (dev)", "scope": "shared", "type": "Postgres",
            "settings": {"server": DEMO_DSN["host"], "port": DEMO_DSN["port"],
                         "database": DEMO_DSN["database"], "user": DEMO_DSN["user"],
                         "sslMode": "Disable"},
            "secretRef": DEMO_DSN["secret_ref"]})
        s.connection_id = saved["id"]


def capture(page, out, name, must_show, height=None):
    page.wait_for_selector(must_show, timeout=30000)
    if height:
        page.set_viewport_size({"width": SHOT["width"], "height": height})
    page.wait_for_timeout(700)  # webfonts and the last transition
    page.screenshot(path=os.path.join(out, name + ".png"))
    page.set_viewport_size(SHOT)


def shoot(base, out, only, with_db):
    from playwright.sync_api import sync_playwright

    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        ctx = browser.new_context(viewport=SHOT, device_scale_factor=2)
        page = ctx.new_page()
        errors = []
        page.on("pageerror", lambda e: errors.append(str(e)))

        # Passkeys are the only way in, so the browser needs an authenticator
        # before the first navigation.
        cdp = ctx.new_cdp_session(page)
        cdp.send("WebAuthn.enable")
        cdp.send("WebAuthn.addVirtualAuthenticator", {"options": {
            "protocol": "ctap2", "transport": "internal", "hasResidentKey": True,
            "hasUserVerification": True, "isUserVerified": True,
            "automaticPresenceSimulation": True}})

        page.goto(f"{base}/", wait_until="networkidle")
        if not page.url.rstrip("/").endswith("/setup"):
            raise SystemExit(f"expected the first-run setup screen, got {page.url}")
        page.fill('input[placeholder="Ada Lovelace"]', "Ada Lovelace")
        page.get_by_role("button", name="Create the admin account").click()
        page.wait_for_url(lambda u: not u.endswith("/setup"), timeout=30000)

        s = Studio(page, base, out)
        seed(s, with_db)

        taken, skipped = [], []
        for name, fn, height, needs_db in SHOTS:
            if only and name not in only:
                continue
            if needs_db and not with_db:
                skipped.append(name)
                continue
            try:
                capture(page, out, name, fn(s), height)
            except Exception as e:
                raise SystemExit(
                    f"{name}: the page never showed what this shot is of, so the "
                    f"screenshot would have been of something else.\n  {e}")
            taken.append(name)
            print(f"  {name}", flush=True)

        if errors:
            raise SystemExit("the page threw, so a screenshot may be an error "
                             "boundary rather than the app:\n  " + "\n  ".join(errors))
        browser.close()
    # Said out loud: a shot that did not run leaves the last one in place, and a
    # stale screenshot looks exactly like a fresh one.
    if skipped:
        print(f"\nSKIPPED (no database): {', '.join(skipped)}", flush=True)
    return taken


# --------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "images", "studio"))
    ap.add_argument("--port", type=int, default=5097)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--no-database", action="store_true",
                    help="skip the Connections shots instead of starting postgres")
    ap.add_argument("--only", default="", help="comma-separated shot names")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    if args.list:
        for name, _, _, needs_db in SHOTS:
            print(name + ("  (needs a database)" if needs_db else ""))
        return 0

    only = {n.strip() for n in args.only.split(",") if n.strip()}
    unknown = only - {n for n, _, _, _ in SHOTS}
    if unknown:
        raise SystemExit(f"no such shot: {', '.join(sorted(unknown))}. --list to see them.")

    if not args.no_build:
        # The web app first: wwwroot is copied into the output at *build* time, so
        # building the C# before the bundle packages the previous one.
        sh(["./build.sh", "Web"])
        sh(["dotnet", "build", os.path.join(STUDIO, "ClrKernel.Studio.csproj"),
            "-c", "Debug", "-f", "net8.0"])

    wants_db = any(needs_db for n, _, _, needs_db in SHOTS if not only or n in only)
    with_db = wants_db and not args.no_database and database()
    if wants_db and not with_db and not args.no_database:
        print("no docker, so the Connections shots are skipped", flush=True)

    os.makedirs(args.out, exist_ok=True)
    root = tempfile.mkdtemp(prefix="clrkernel-shots-")
    base = f"http://localhost:{args.port}"
    server = None
    try:
        nb, data = workspace(root)
        log = open(os.path.join(root, "serve.log"), "w")
        server = subprocess.Popen(
            ["dotnet", "run", "--project", STUDIO, "-f", "net8.0", "--no-build", "--",
             "serve", "--notebooks", nb, "--data-dir", data, "--store", "sqlite",
             "--urls", base],
            cwd=REPO, stdout=log, stderr=subprocess.STDOUT,
            # The connection stores a secret *reference*; this is what it resolves to.
            env={**os.environ,
                 "CLRKERNEL_SECRET_" + DEMO_DSN["secret_ref"]: DEMO_DSN["password"]},
            start_new_session=True)  # its own group, so teardown gets the child too
        health = wait_for(f"{base}/api/health")
        if health.get("errors"):
            raise SystemExit("studio reported: " + "; ".join(health["errors"]))
        print(f"studio {health['version']} on {base}", flush=True)
        taken = shoot(base, args.out, only, with_db)
    finally:
        if server:
            os.killpg(os.getpgid(server.pid), signal.SIGTERM)
            server.wait(timeout=30)
        shutil.rmtree(root, ignore_errors=True)
    print(f"\nwrote {len(taken)} to {args.out}")


if __name__ == "__main__":
    sys.exit(main())
