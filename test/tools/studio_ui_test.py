#!/usr/bin/env python3
"""Browser checks for the Studio web app.

    python3 test/tools/studio_ui_test.py              # build, then run everything
    python3 test/tools/studio_ui_test.py --no-build   # iterate on a built tree
    python3 test/tools/studio_ui_test.py --only files-shell
    python3 test/tools/studio_ui_test.py --list

Not run by CI, and not a replacement for the vitest suite beside the app — these
are the things only a browser can answer. `isFullBleed('/files/default')` is a
pure function and belongs in `routes.test.ts`; whether the file tree moved 28px
when you opened a file is not, and that was the bug.

Each check gets a fresh workspace, a fresh data dir and a fresh admin account,
because the first-run screen exists once per data dir and half of these care
about what a first visit looks like. That costs a few seconds per check and buys
independence: one failing check cannot leave the next one signed out.

**Every check asserts before it concludes, and every one of them has been run
against the bug it covers.** A browser check that cannot fail is worse than
none — it is a green tick over a broken page.

Requires: playwright (`pip install playwright && playwright install chromium`)
and the .NET SDK. No network, no docker.
"""

import argparse
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from studio_harness import (  # noqa: E402
    authenticator, build, first_run, serving, studio, temp_root,
)

VIEWPORT = {"width": 1500, "height": 950}

CHECKS = []


def check(name):
    def register(fn):
        CHECKS.append((name, fn))
        return fn
    return register


def workspace(root):
    """One notebook, and the git workflow — which most of the app needs."""
    nb, data = os.path.join(root, "nb"), os.path.join(root, "data")
    os.makedirs(nb), os.makedirs(data)
    with open(os.path.join(nb, "etl.nb.md"), "w") as f:
        f.write("# Extract\n\n```csharp\n1+1\n```\n")
    studio("git", "init", data=data, notebooks=nb)
    return nb, data


def explorer(page):
    return page.locator('[aria-label="Explorer"]')


# --------------------------------------------------------------------- checks

@check("files-shell")
def files_shell(page, base, _root):
    """Files is the editor's shell with nothing open in it.

    The bug this covers: /files/:project used to be a card in a padded page, so
    opening a file swapped one tree for a different tree in a different place and
    the list you had been reading moved out from under you.
    """
    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_timeout(1500)
    assert explorer(page).count() == 1, page.inner_text("body")[:900]
    shell_box = explorer(page).bounding_box()

    body = page.inner_text("body").replace("\xa0", " ")
    assert "Pick a file on the left" in body, body[-900:]
    # One tree, not two: the shell's and the editor's are the same component.
    assert body.count("etl.nb.md") == 1, body

    page.get_by_role("button", name="etl.nb.md").first.click()
    page.wait_for_url(lambda u: "/edit/" in u, timeout=10000)
    page.wait_for_timeout(2000)
    assert explorer(page).bounding_box() == shell_box, (
        f"the sidebar moved: {shell_box} -> {explorer(page).bounding_box()}")

    # A width set in one is the width the other opens at — the layout is one
    # stored value, not two.
    page.evaluate("""() => {
        const key = Object.keys(localStorage).find(k => k.includes('layout'));
        const v = JSON.parse(localStorage.getItem(key));
        v.explorerWidth = 320;
        localStorage.setItem(key, JSON.stringify(v));
    }""")
    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_timeout(1500)
    assert round(explorer(page).bounding_box()["width"]) == 320, explorer(page).bounding_box()


@check("branch-switch")
def branch_switch(page, base, _root):
    """Switching branch with a file open closes the file.

    The same path on two branches is two files, and on test it may not exist at
    all — the pane used to go on showing the other branch's copy. The edit still
    has to survive the crossing, which is what the second half is.
    """
    page.goto(f"{base}/files/default/edit/mine/etl.nb.md", wait_until="networkidle")
    page.wait_for_timeout(3000)
    page.locator(".monaco-editor").first.click()
    page.keyboard.press("Meta+A")
    page.keyboard.type("var survived = 99;")
    # Straight into the switch, with the autosave debounce still pending.
    explorer(page).get_by_role("combobox", name="Branch").click()
    page.get_by_role("option", name="test", exact=True).click()
    page.wait_for_url(lambda u: "/edit/" not in u, timeout=8000)

    assert page.url.rstrip("/").endswith("/files/default"), page.url
    assert "Pick a file on the left" in page.inner_text("body")
    page.wait_for_timeout(2500)
    text = page.evaluate("""async () => await (await fetch(
        '/api/projects/default/branches/mine/notebooks/content?path=etl.nb.md')).text()""")
    assert "var survived = 99;" in text, "the pending edit was lost on the way out:\n" + text


@check("completions")
def completions(page, base, _root):
    """A question about a cell that closes before the answer comes back.

    Both halves matter and they pull opposite ways: leaving mid-request must be
    silent, and completions must still arrive. The obvious fix for the first —
    answering null always — breaks the second, and only the second notices.
    """
    page.goto(f"{base}/files/default/edit/mine/etl.nb.md", wait_until="networkidle")
    page.wait_for_timeout(4000)
    page.locator(".monaco-editor").first.click()
    page.keyboard.press("Meta+A")
    # Narrowed, because the widget virtualises: the whole member list is there
    # but only the first screenful is in the DOM.
    page.keyboard.type("Console.Wri", delay=90)
    page.wait_for_timeout(4000)
    rows = page.locator(".suggest-widget .monaco-list-row")
    assert rows.count() > 0, "no completions at all"
    labels = " ".join(rows.all_inner_texts())
    assert "WriteLine" in labels, labels[:400]
    page.keyboard.press("Escape")

    for how in ("link", "branch"):
        page.goto(f"{base}/files/default/edit/mine/etl.nb.md", wait_until="networkidle")
        page.wait_for_timeout(3500)
        page.errors.clear()
        page.locator(".monaco-editor").first.click()
        page.keyboard.press("Meta+A")
        # A trigger character, so a request is certainly in flight as we leave.
        page.keyboard.type("Console.")
        if how == "link":
            page.locator('a[href="/files/default"]').first.click()
        else:
            explorer(page).get_by_role("combobox", name="Branch").click()
            page.get_by_role("option", name="test", exact=True).click()
        page.wait_for_url(lambda u: u.rstrip("/").endswith("/files/default"), timeout=8000)
        page.wait_for_timeout(3000)
        assert page.errors == [], f"leaving by {how}: {sorted(set(page.errors))}"


@check("invite")
def invite(page, base, _root):
    """An invite carries the name and the handle it will create.

    Both are settled on the admin's form, which is what lets a collision be
    refused there instead of while somebody is holding a security key.
    """
    page.goto(f"{base}/settings/users", wait_until="networkidle")
    page.wait_for_timeout(1200)

    page.get_by_role("textbox", name="Their name").fill("Grace Hopper")
    handle = page.get_by_role("textbox", name="Username")
    assert handle.input_value() == "grace-hopper", handle.input_value()

    # Taken — by the admin, whose own handle came from their name.
    page.get_by_role("textbox", name="Their name").fill("Someone Else")
    handle.fill("ada-lovelace")
    page.get_by_role("button", name="Create invite").click()
    page.wait_for_timeout(1500)
    assert "already" in page.inner_text("body"), "a taken username was accepted"

    handle.fill("grace")
    page.get_by_role("textbox", name="Their name").fill("Grace Hopper")
    page.get_by_role("button", name="Create invite").click()
    page.wait_for_timeout(2000)
    row = page.locator("table").last.inner_text().replace("\xa0", " ")
    assert "Grace Hopper" in row and "grace" in row, row

    code = page.evaluate("""async () => {
        const r = await (await fetch('/api/invites')).json();
        return r.invites.find(i => i.status === 'open').code;
    }""")

    # A different person, a different authenticator, and nothing to type.
    guest = page.context.browser.new_context().new_page()
    guest_errors = []
    guest.on("pageerror", lambda e: guest_errors.append(str(e)))
    authenticator(guest)
    guest.goto(f"{base}/invite/{code}", wait_until="networkidle")
    guest.wait_for_timeout(1200)
    seen = guest.inner_text("body").replace("\xa0", " ")
    assert "Grace Hopper" in seen and "grace" in seen, seen
    assert guest.get_by_role("textbox").count() == 0, "the invitee still has something to type"
    guest.get_by_role("button", name="Create my account").click()
    guest.wait_for_url(lambda u: "/invite/" not in u, timeout=20000)
    assert guest_errors == [], guest_errors

    users = page.evaluate("async () => (await (await fetch('/api/users')).json()).users")
    grace = [u for u in users if u["displayName"] == "Grace Hopper"]
    assert len(grace) == 1, users
    assert grace[0]["username"] == "grace", grace[0]


@check("job-card")
def job_card(page, base, _root):
    """The job card follows its run.

    It used to say "Started." and never revise it — still Started long after the
    run had finished, which is worse than saying nothing.
    """
    page.evaluate("""async () => {
        await fetch('/api/projects/default/branches/mine/notebooks/content?path=etl.jobs.yaml',
            { method: 'PUT', headers: {'Content-Type': 'text/plain'},
              body: ['notebook: ./etl.nb.md', 'jobs:', '  - name: nightly', '']
                  .join(String.fromCharCode(10)) });
        await fetch('/api/projects/default/branch/push', { method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({ message: 'a job' }) });
    }""")
    page.goto(f"{base}/files/default/overview/test/etl.jobs.yaml", wait_until="networkidle")
    page.wait_for_timeout(2500)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "Run now" in body, body[-1200:]
    assert "View runs" in body, "no View runs button:\n" + body[-1200:]
    assert "Its runs" not in body, "the old link is still there"

    page.get_by_role("button", name="Run now").click()
    seen = set()
    for _ in range(40):
        page.wait_for_timeout(1500)
        text = page.inner_text("body").replace("\xa0", " ")
        seen |= {s for s in ("Pending", "Running", "Succeeded", "Failed") if s in text}
        if seen & {"Succeeded", "Failed"}:
            break
    assert "Started." not in page.inner_text("body"), "the stale note is back"
    assert seen & {"Succeeded", "Failed"}, f"the card never reported a finish; saw {seen}"


@check("project-filter")
def project_filter(page, base, root):
    """The breadcrumb's project switcher: one line per name, and filterable.

    The two behaviours that took a fix each: a menu item focuses itself as the
    pointer crosses it, which pulled the caret out of the box mid-word; and the
    arrows had to be handed to the list by hand, because the menu moves between
    items with a roving focus group that only listens on the items themselves.
    """
    long_name = "Quarterly Financial Reconciliation Warehouse"
    names = [long_name] + [f"Team {n}" for n in range(1, 10)]
    made = page.evaluate("""async ({ names, root }) => {
        const out = [];
        for (const [i, name] of names.entries()) {
            const r = await fetch('/api/projects', { method: 'POST',
                headers: {'Content-Type': 'application/json'},
                body: JSON.stringify({ name, slug: 'p' + i, root: root + '/p' + i,
                    gitEnabled: false, remoteMode: 'Local', pushUserBranches: false }) });
            out.push(r.status);
        }
        return out;
    }""", {"names": names, "root": os.path.join(root, "projects")})
    assert set(made) == {201}, made

    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_timeout(1500)
    page.locator('button[aria-label^="Project:"]').first.click()
    page.wait_for_timeout(600)
    menu = page.get_by_role("menu")

    item = menu.get_by_role("menuitem").filter(has_text=long_name).first
    height = item.bounding_box()["height"]
    assert height < 40, f"the longest name is {height}px tall — it wrapped"

    box = page.get_by_role("textbox", name="Filter projects")
    box.type("team 3", delay=40)
    page.wait_for_timeout(400)
    shown = [t for t in menu.get_by_role("menuitem").all_inner_texts()
             if "Team" in t or long_name in t]
    assert shown == ["Team 3"], shown

    # Moving over the filtered rows must not steal the caret.
    menu.get_by_role("menuitem").first.hover()
    page.wait_for_timeout(300)
    box.type("x", delay=40)
    assert box.input_value() == "team 3x", (
        f"the caret left the box on hover: {box.input_value()!r}")

    # And the arrows still reach the list, so this is not a mouse-only control.
    box.fill("Team 7")
    page.wait_for_timeout(400)
    page.keyboard.press("ArrowDown")
    page.wait_for_timeout(300)
    focused = page.evaluate("() => document.activeElement?.textContent")
    assert focused and "Team 7" in focused, f"ArrowDown did not enter the list: {focused!r}"

    box.fill("Quarterly")
    page.wait_for_timeout(400)
    menu.get_by_role("menuitem").filter(has_text=long_name).first.click()
    page.wait_for_url(lambda u: "/files/p0" in u, timeout=8000)


@check("secrets-project")
def secrets_project(page, base, root):
    """Secrets names the project it is showing, and can change it.

    It used to follow whichever project you last had open somewhere else, with
    nothing on screen naming it — so the only way to see another project's
    secrets was to go to Files, switch there, and come back.
    """
    # Git-initialised, so it has the same test/prod branches the first one does —
    # which is the whole point: the same branch name in two projects.
    made = page.evaluate("""async ({ root }) => {
        const r = await fetch('/api/projects', { method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({ name: 'Warehouse', slug: 'warehouse',
                root: root + '/warehouse', gitEnabled: true,
                remoteMode: 'Local', pushUserBranches: false }) });
        if (r.status !== 201) return [r.status, await r.text()];
        const init = await fetch('/api/projects/warehouse/init', { method: 'POST' });
        return [init.status, await init.text()];
    }""", {"root": os.path.join(root, "projects")})
    assert made[0] == 200, made

    # One name, two projects, two values — the case the page could not tell apart.
    for slug, value in (("default", "sk-from-default"), ("warehouse", "sk-from-warehouse")):
        status = page.evaluate("""async ({ slug, value }) => (await fetch(
            `/api/projects/${slug}/branches/test/secrets/OPENAI`,
            { method: 'PUT', headers: {'Content-Type': 'application/json'},
              body: JSON.stringify({ value }) })).status""", {"slug": slug, "value": value})
        assert status == 200, (slug, status)

    page.goto(f"{base}/settings/secrets", wait_until="networkidle")
    page.wait_for_timeout(1500)
    picker = page.get_by_role("combobox", name="Project")
    assert picker.count() == 1, "no project picker:\n" + page.inner_text("body")[:1200]

    # Whichever project it opens on, it says which one that is.
    opened = picker.inner_text().strip()
    assert opened in ("nb", "Warehouse"), f"the picker names no project: {opened!r}"

    page.get_by_role("combobox", name="Branch").click()
    page.get_by_role("option", name="test", exact=True).click()
    page.wait_for_timeout(1200)
    assert "OPENAI" in page.locator("table").last.inner_text(), page.inner_text("body")[-900:]

    # The other project, without leaving the page — and its secrets are its own.
    other = "Warehouse" if opened != "Warehouse" else "nb"
    picker.click()
    page.get_by_role("option", name=other, exact=True).click()
    page.wait_for_timeout(1500)
    assert picker.inner_text().strip() == other, (
        f"picked {other}, but the page still says {picker.inner_text().strip()!r}")
    # Back on your own branch, where neither project was given a secret.
    assert "OPENAI" not in page.locator("table").last.inner_text(), (
        "the other project's branch carried a selection across")

    page.get_by_role("combobox", name="Branch").click()
    page.get_by_role("option", name="test", exact=True).click()
    page.wait_for_timeout(1500)
    assert "OPENAI" in page.locator("table").last.inner_text(), page.inner_text("body")[-900:]

    # And the value never travelled, for either of them.
    body = page.inner_text("body")
    assert "sk-from-" not in body, body[-900:]


@check("read-only-pill")
def read_only_pill(page, base, _root):
    """A file you cannot edit says so as a pill, and the pill says why on hover.

    It used to be a grey sentence at the far end of the toolbar, next to nothing
    that explained it and small enough to miss.
    """
    page.evaluate("""async () => {
        await fetch('/api/projects/default/branches/mine/notebooks/content?path=chart.png',
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: 'x' });
    }""")
    page.goto(f"{base}/files/default/preview/mine/chart.png", wait_until="networkidle")
    page.wait_for_timeout(2000)

    pill = page.get_by_text("read-only", exact=True)
    assert pill.count() >= 1, "no read-only pill:\n" + page.inner_text("body")[:1200]
    body = page.inner_text("body").replace("\xa0", " ")
    assert "read-only — a picture" not in body, "the old grey sentence is still there"
    pill.first.hover()
    page.wait_for_timeout(900)
    assert "picture opens here to look at" in page.inner_text("body"), \
        "the tooltip did not say why"


@check("cell-line-numbers")
def cell_line_numbers(page, base, _root):
    """Cells have a line-number gutter."""
    page.goto(f"{base}/files/default/edit/mine/etl.nb.md", wait_until="networkidle")
    page.wait_for_timeout(3000)
    gutter = page.locator(".monaco-editor .line-numbers")
    assert gutter.count() > 0, "no line-number gutter in a cell"
    assert "1" in gutter.first.inner_text(), gutter.first.inner_text()


# ----------------------------------------------------------------------- main

def run(name, fn, port):
    """One check, against a server and an account of its own."""
    from playwright.sync_api import sync_playwright

    with temp_root(f"clrkernel-ui-{name}-") as root:
        nb, data = workspace(root)
        with serving(nb, data, port) as base, sync_playwright() as pw:
            browser = pw.chromium.launch()
            page = browser.new_page(viewport=VIEWPORT)
            # Hung off the page so a check can clear it: "no errors from here on"
            # is a different question from "no errors all run".
            page.errors = []
            page.on("pageerror", lambda e: page.errors.append(str(e).split("\n")[0]))
            try:
                authenticator(page)
                first_run(page, base)
                fn(page, base, root)
                # A React error boundary makes a thrown exception look like an
                # empty panel, so a check that only reads text can pass over one.
                assert page.errors == [], page.errors
            finally:
                browser.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5098)
    ap.add_argument("--no-build", action="store_true")
    ap.add_argument("--only", default="", help="comma-separated check names")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    if args.list:
        for name, fn in CHECKS:
            print(f"{name:20} {(fn.__doc__ or '').strip().splitlines()[0]}")
        return 0

    only = {n.strip() for n in args.only.split(",") if n.strip()}
    unknown = only - {n for n, _ in CHECKS}
    if unknown:
        raise SystemExit(f"no such check: {', '.join(sorted(unknown))}. --list to see them.")

    if not args.no_build:
        build()

    failed = []
    for name, fn in CHECKS:
        if only and name not in only:
            continue
        print(f"\n=== {name}", flush=True)
        try:
            run(name, fn, args.port)
            print(f"    ok", flush=True)
        except Exception as e:
            failed.append(name)
            print(f"    FAILED: {e}", flush=True)

    print()
    if failed:
        print(f"{len(failed)} failed: {', '.join(failed)}")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
