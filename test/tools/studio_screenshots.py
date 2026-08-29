#!/usr/bin/env python3
"""Capture the Studio screenshots that docs/studio.md embeds.

    python3 test/tools/studio_screenshots.py            # rebuild, capture, write docs/images/studio
    python3 test/tools/studio_screenshots.py --no-build  # iterate on an already-built tree
    python3 test/tools/studio_screenshots.py --keep      # leave the server up to poke at

Everything is disposable except the PNGs: a temp workspace, a temp data dir, an
admin account created and thrown away. Nothing touches the notebooks or the run
history of an instance you are actually using.

The images are committed rather than generated in CI, because docs/studio.md is
read on GitHub as well as on the docs site, and a generated file lands in
docs-site/public/ which is gitignored — the GitHub copy would show broken
images. The cost is that they go stale unless somebody re-runs this; that is
why it is one command with no arguments.

Requires: playwright (`pip install playwright && playwright install chromium`),
the .NET SDK, and node for the web app build.
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
# a database, so scheduling them is realistic but running them is not. hello.nb.md
# has an HTTP cell, so this much does need a network.
SEED_RUNS = [("hello-hourly", "test"), ("diagrams-nightly", "test"),
             ("hello-hourly", "prod"), ("diagrams-weekly", "test")]


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
    for job, env in SEED_RUNS:
        studio("run", job, "--env", env, data=data, notebooks=nb)
    return nb, data


# --------------------------------------------------------------------------- shots

SHOT = {"width": 1440, "height": 900}


def capture(page, out, name, must_show, height=None):
    """Screenshot, but only once the page proves it rendered.

    Without the assertion a failed sign-in or an error boundary produces a
    perfectly valid PNG of the wrong thing, and the first person to notice is
    whoever reads the published page.

    `height` trims the frame to a page that does not fill 900px, because half a
    screenshot of empty background reads as an empty app.
    """
    page.wait_for_selector(must_show, timeout=20000)
    if height:
        page.set_viewport_size({"width": SHOT["width"], "height": height})
    page.wait_for_timeout(600)  # webfonts and the last transition
    page.screenshot(path=os.path.join(out, name))
    page.set_viewport_size(SHOT)
    print(f"  {name}", flush=True)


def shoot(base, out, keep_open=False):
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

        capture(page, out, "dashboard.png", "text=Up next")

        # Ask rather than assume: `git init` names the project after the folder,
        # which is a temp directory with a random suffix.
        projects = page.evaluate(
            "async () => (await (await fetch('/api/projects')).json()).projects")
        if not projects:
            raise SystemExit("studio has no projects; did `git init` run?")
        slug = projects[0]["slug"]

        page.goto(f"{base}/files/{slug}", wait_until="networkidle")
        capture(page, out, "files.png", "text=hello.nb.md", height=620)

        # `test` is a branch here, not a query parameter: the git workflow's two
        # worktrees are branches, and every view of a file names the one it shows.
        page.goto(f"{base}/files/{slug}/edit/test/hello.nb.md", wait_until="networkidle")
        page.wait_for_selector(".monaco-editor", timeout=30000)
        page.get_by_role("radio", name="Focus").click()

        # Run it. A screenshot of an editor with no output is a screenshot of a
        # text editor; the whole claim of this page is that the cells execute.
        run_all = 'button[aria-label="Run all cells"]'
        page.click(run_all)
        # The button disables for the duration, which is the one signal that does
        # not depend on reading a status word. Waiting for an `svg` instead matches
        # the toolbar icons and screenshots a pending cell — it has already happened.
        page.wait_for_selector(f"{run_all}[disabled]", timeout=30000)
        page.wait_for_selector(f"{run_all}:not([disabled])", timeout=300000)  # NuGet restore

        page.get_by_text("graph LR").click()
        try:
            # `.focus-empty` *is* "No output — run this cell to see results."
            page.wait_for_selector(".focus-empty", state="detached", timeout=30000)
            # Mermaid renders client-side inside a sandboxed iframe, so the
            # diagram is never a node of the parent document.
            page.frame_locator(".focus-output-pane iframe").locator(
                "svg").first.wait_for(timeout=60000)
        except Exception:
            pane = page.query_selector(".focus-output-pane")
            raise SystemExit(
                "the mermaid cell rendered no diagram — run all cells failed, or its "
                "output moved. This needs a network: hello.nb.md restores NuGet "
                "packages and calls httpbin.\n--- output pane ---\n"
                + (pane.inner_html()[:2000] if pane else "(not found)"))
        capture(page, out, "focus-mode.png", ".monaco-editor")

        if errors:
            raise SystemExit("the page threw, so a screenshot may be an error "
                             f"boundary rather than the app:\n  " + "\n  ".join(errors))
        if keep_open:
            input(f"serving {base} — enter to close: ")
        browser.close()


# --------------------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(REPO, "docs", "images", "studio"))
    ap.add_argument("--port", type=int, default=5097)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--keep", action="store_true", help="leave the server up at the end")
    args = ap.parse_args()

    if not args.no_build:
        # The web app first: wwwroot is copied into the output at *build* time, so
        # building the C# before the bundle packages the previous one.
        sh(["./build.sh", "Web"])
        sh(["dotnet", "build", os.path.join(STUDIO, "ClrKernel.Studio.csproj"),
            "-c", "Debug", "-f", "net8.0"])

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
            start_new_session=True)  # its own group, so teardown gets the child too
        health = wait_for(f"{base}/api/health")
        if health.get("errors"):
            raise SystemExit("studio reported: " + "; ".join(health["errors"]))
        print(f"studio {health['version']} on {base}", flush=True)
        shoot(base, args.out, keep_open=args.keep)
    finally:
        if server:
            os.killpg(os.getpgid(server.pid), signal.SIGTERM)
            server.wait(timeout=30)
        shutil.rmtree(root, ignore_errors=True)
    print(f"\nwrote {args.out}")


if __name__ == "__main__":
    sys.exit(main())
