"""A throwaway Studio instance, and a browser signed in to it.

Shared by `studio_screenshots.py` and `studio_ui_test.py`, which want the same
five things and want them to stay the same: build, a temp workspace, `serve`, a
virtual authenticator, and the first-run account. Two copies of the WebAuthn
options is how one of them quietly stops matching the app.

Nothing here touches an instance you are using — every path is a temp directory
and every account is created and thrown away with it.
"""

import contextlib
import json
import os
import shutil
import signal
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
STUDIO = os.path.join(REPO, "src", "ClrKernel.Studio")


def sh(args, **kw):
    """Run a command, letting its output through. A silenced build in a harness
    keeps serving the last binary that compiled — see the studio-webapp-dev skill."""
    print(f"$ {' '.join(args)}", flush=True)
    subprocess.run(args, check=True, cwd=REPO, **kw)


def build():
    """The web app first: wwwroot is copied into the output at *build* time, so
    building the C# before the bundle packages the previous one."""
    sh(["./build.sh", "Web"])
    sh(["dotnet", "build", os.path.join(STUDIO, "ClrKernel.Studio.csproj"),
        "-c", "Debug", "-f", "net8.0"])


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


@contextlib.contextmanager
def serving(nb, data, port, env=None):
    """`serve` on a port, and its whole process group stopped afterwards.

    Its own session, because `dotnet run` starts the app as a child: killing only
    the parent leaves the port held by something nothing is watching.
    """
    base = f"http://localhost:{port}"
    log = open(os.path.join(data, "serve.log"), "w")
    server = subprocess.Popen(
        ["dotnet", "run", "--project", STUDIO, "-f", "net8.0", "--no-build", "--",
         "serve", "--notebooks", nb, "--data-dir", data, "--store", "sqlite",
         "--urls", base],
        cwd=REPO, stdout=log, stderr=subprocess.STDOUT,
        env={**os.environ, **(env or {})}, start_new_session=True)
    try:
        health = wait_for(f"{base}/api/health")
        if health.get("errors"):
            raise SystemExit("studio reported: " + "; ".join(health["errors"]))
        print(f"studio {health['version']} on {base}", flush=True)
        yield base
    finally:
        os.killpg(os.getpgid(server.pid), signal.SIGTERM)
        server.wait(timeout=30)
        log.close()


@contextlib.contextmanager
def temp_root(prefix):
    root = tempfile.mkdtemp(prefix=prefix)
    try:
        yield root
    finally:
        shutil.rmtree(root, ignore_errors=True)


def authenticator(page):
    """Passkeys are the only way in, so the browser needs one before the first
    navigation. `isUserVerified` and the presence simulation are what make the
    ceremony complete without a human touching a key."""
    cdp = page.context.new_cdp_session(page)
    cdp.send("WebAuthn.enable")
    cdp.send("WebAuthn.addVirtualAuthenticator", {"options": {
        "protocol": "ctap2", "transport": "internal", "hasResidentKey": True,
        "hasUserVerification": True, "isUserVerified": True,
        "automaticPresenceSimulation": True}})


def first_run(page, base, name="Ada Lovelace"):
    """Creates the server's admin and signs in. Once per data dir — a second call
    against the same one lands on a sign-in screen with no passkey to offer, which
    is a `page.fill` timeout rather than anything that says so."""
    page.goto(f"{base}/", wait_until="networkidle")
    if not page.url.rstrip("/").endswith("/setup"):
        raise SystemExit(f"expected the first-run setup screen, got {page.url}")
    page.fill('input[placeholder="Ada Lovelace"]', name)
    page.get_by_role("button", name="Create the admin account").click()
    page.wait_for_url(lambda u: not u.endswith("/setup"), timeout=30000)
