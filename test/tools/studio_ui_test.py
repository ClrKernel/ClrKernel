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
    # The pane shows the repo now, not an empty state. The file appears twice on
    # purpose — once in the explorer, once in the Contents table — which is why
    # this counts the *explorer's* copy rather than the page's: one sidebar, not
    # two, is the thing this check was written to protect.
    assert "Contents" in body and "History" in body, body[-900:]
    assert explorer(page).get_by_role("button", name="etl.nb.md").count() == 1, body

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

    # The branch is in the address now, so switching it is a navigation — which
    # is what makes the Contents and History beside the tree follow along. This
    # asserts the *new* branch, not just that the file closed: landing on the
    # shell for the branch you left would be the old bug wearing a new URL.
    assert page.url.rstrip("/").endswith("/files/default/test"), page.url
    # Landed on the shell, which is the repo browser now rather than an empty pane.
    assert "Contents" in page.inner_text("body")
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
            # The breadcrumb points at the branch, not the project's door.
            page.locator('a[href^="/files/default/"]').first.click()
        else:
            explorer(page).get_by_role("combobox", name="Branch").click()
            page.get_by_role("option", name="test", exact=True).click()
        page.wait_for_url(lambda u: "/edit/" not in u, timeout=8000)
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


@check("repo-browser")
def repo_browser(page, base, _root):
    """The Files route shows the repo, not an empty pane.

    Contents lists the folder with the commit that last touched each row —
    the column a filesystem cannot answer — and History is the branch's log.
    """
    def write(branch, path, text):
        return page.evaluate("""async ({ branch, path, text }) => (await fetch(
            `/api/projects/default/branches/${branch}/notebooks/content?path=${path}`,
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: text })).status""",
            {"branch": branch, "path": path, "text": text})

    assert write("mine", "reports/monthly.nb.md", "# Monthly\n") == 200
    page.evaluate("""async () => { await fetch('/api/projects/default/branch/push',
        { method: 'POST', headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({ message: 'add the monthly report' }) }); }""")

    page.goto(f"{base}/files/default", wait_until="networkidle")
    # Two fetches land here — the tree for the explorer and the listing for the
    # table — so wait for the row rather than guessing at a number.
    page.get_by_role("row", name=re.compile("reports")).first.wait_for(timeout=15000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "Contents" in body and "History" in body, "no Contents/History tabs:\n" + body[:900]

    # Contents: the folder, and why it last changed.
    table = page.locator("table").last.inner_text().replace("\xa0", " ")
    assert "reports" in table, table
    assert "add the monthly report" in table, (
        "no last-commit column — that is the whole point of this table:\n" + table)

    # Descend into it. Scoped to the table: the explorer has a `reports` folder
    # too, it comes first in the DOM, and clicking that one only expands the
    # sidebar — leaving the table showing the root and the assertion below
    # blaming the wrong thing.
    page.locator("table").last.get_by_role("button", name="reports", exact=True).click()
    # The filename, not "monthly": the root listing's `reports` row carries the
    # commit message "add the monthly report", so the looser pattern matched
    # before the click had even landed — and the table was then read during the
    # refetch, empty, blaming the descend for a race in the wait.
    page.get_by_role("row", name=re.compile(r"monthly\.nb\.md")).first.wait_for(timeout=15000)
    table = page.locator("table").last.inner_text().replace("\xa0", " ")
    assert "monthly.nb.md" in table, "descending showed no file: " + table

    # History is the branch's log.
    page.get_by_role("tab", name="History").click()
    page.wait_for_timeout(2000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "add the monthly report" in body, "History does not show the commit:\n" + body[-1000:]


@check("branch-in-url")
def branch_in_url(page, base, _root):
    """The branch is part of the address, and switching it moves the whole page.

    Reported: switching branch in the explorer left Contents and History showing
    the branch you had just left. The tree read its own state; the panes beside
    it read a branch computed from localStorage on render, and nothing re-ran
    when the tree wrote to it.
    """
    def write(branch, path, text):
        return page.evaluate("""async ({ branch, path, text }) => (await fetch(
            `/api/projects/default/branches/${branch}/notebooks/content?path=${path}`,
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: text })).status""",
            {"branch": branch, "path": path, "text": text})

    # A file only your branch has, so the two listings cannot be confused.
    assert write("mine", "only-mine.nb.md", "# Mine\n") == 200

    # The door redirects to a branch rather than being a place of its own.
    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_url(lambda u: "/files/default/" in u, timeout=10000)
    assert page.url.rstrip("/").endswith("/files/default/mine"), page.url

    page.get_by_role("row", name=re.compile(r"only-mine")).first.wait_for(timeout=15000)

    # Switch, and everything moves: the URL, the tree, and the table beside it.
    explorer(page).get_by_role("combobox", name="Branch").click()
    page.get_by_role("option", name="test", exact=True).click()
    page.wait_for_url(lambda u: u.rstrip("/").endswith("/files/default/test"), timeout=10000)
    page.wait_for_timeout(2500)
    table = page.locator("table").last.inner_text().replace("\xa0", " ")
    assert "only-mine.nb.md" not in table, (
        "Contents is still showing the branch you left:\n" + table)

    # History follows too — the other half of the same complaint.
    page.get_by_role("tab", name="History").click()
    page.wait_for_timeout(2500)
    assert "adopt existing notebooks" in page.inner_text("body"), page.inner_text("body")[-800:]

    # And a link written before the branch moved in front of the view still lands.
    page.goto(f"{base}/files/default/edit/mine/only-mine.nb.md", wait_until="networkidle")
    page.wait_for_url(lambda u: "/mine/edit/" in u, timeout=10000)
    assert page.url.endswith("/files/default/mine/edit/only-mine.nb.md"), page.url


@check("commit-detail")
def commit_detail(page, base, _root):
    """A commit in History is a link to a page, and the page says what it did.

    It used to expand in place: the files appeared inside a row in a list, with
    nowhere to go from there and no address to send anybody.
    """
    import subprocess
    root = page.evaluate("async () => (await (await fetch('/api/health')).json()).notebooksRoot")
    tree = os.path.join(root, "test")

    def commit(message):
        for args in (["add", "-A"], ["-c", "user.email=t@x", "-c", "user.name=Tess",
                                     "commit", "-m", message]):
            subprocess.run(["git", *args], cwd=tree, check=True, capture_output=True)

    def write(rel, text):
        full = os.path.join(tree, rel)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w") as f:
            f.write(text)

    # Committed straight into the test worktree, which is what somebody else's
    # push looks like from here — and the only way to get a *deletion* into a
    # commit, which the app itself has no button for.
    write("reports/monthly.nb.md", "# Monthly\n")
    write("reports/doomed.nb.md", "# Doomed\n")
    commit("the first two reports")

    # One commit with one of each status, so a mapping wired backwards cannot pass
    # by drawing every row the same way.
    write("reports/monthly.nb.md", "# Monthly\n\nreworked\n")
    write("fresh.nb.md", "# Fresh\n")
    os.remove(os.path.join(tree, "reports/doomed.nb.md"))
    commit("add, change and remove")

    page.goto(f"{base}/files/default/test", wait_until="networkidle")
    page.wait_for_timeout(2000)
    page.get_by_role("tab", name="History").click()
    page.wait_for_timeout(2500)

    # The list: a message, and under it the sha, the author and a written-out
    # date. Not "2d ago" — a history is a record, and "which afternoon was that"
    # is a question relative time cannot answer.
    body = page.inner_text("body").replace("\xa0", " ")
    assert "add, change and remove" in body, body[-900:]
    assert re.search(r"\d\d/\d\d/\d{4} \d\d:\d\d [AP]M", body), (
        "no written-out date in the commit list:\n" + body[-900:])

    # The graph column beside it.
    assert page.locator("ol svg").count() >= 2, "no graph column beside the commits"

    # A commit is a link to its own page, not a row that expands. Read from the
    # list itself: the explorer beside it names every file on the branch, so a
    # whole-body assertion would find `fresh.nb.md` there and never fail.
    listed = page.locator("ol").first.inner_text().replace("\xa0", " ")
    assert "fresh.nb.md" not in listed, (
        "the files are already listed — the row still expands:\n" + listed[-900:])
    page.get_by_role("link", name=re.compile("add, change and remove")).first.click()
    page.wait_for_url(re.compile(r"/files/default/test/commit/[0-9a-f]{40}$"), timeout=10000)
    page.wait_for_timeout(2000)

    body = page.inner_text("body").replace("\xa0", " ")
    assert "CHANGED FILES" in body, "no changed-files pane:\n" + body[-900:]
    for name in ("fresh.nb.md", "monthly.nb.md", "doomed.nb.md"):
        assert name in body, f"{name} missing from the commit page:\n" + body[-1200:]

    # Deleted is struck through and added carries the plus — told apart in the DOM,
    # not merely both drawn as "special".
    pane = page.locator('div[aria-label="Changed files"]')
    struck = pane.locator("span.line-through")
    assert struck.count() == 1, f"expected one struck-through name, got {struck.count()}"
    assert "doomed" in struck.first.inner_text(), struck.first.inner_text()
    plus = pane.locator('svg[class*="square-plus"]')
    assert plus.count() == 1, f"expected one added-file icon, got {plus.count()}"

    # Before picking a file: a card per file, collapsed, that opens to its diff.
    cards = page.locator('ul[aria-label="Changes"] button[aria-expanded]')
    assert cards.count() == 3, f"expected a card per changed file, got {cards.count()}"
    assert page.locator(".diff-editor").count() == 0, "a diff is open before anything was clicked"
    cards.filter(has_text="fresh.nb.md").first.click()
    for _ in range(15):
        page.wait_for_timeout(1000)
        if page.locator(".diff-editor").count() > 0:
            break
    assert page.locator(".diff-editor").count() == 1, "the card did not open its diff"
    assert "Added by this commit" in page.inner_text("body").replace("\xa0", " ")

    # And picking a file from the pane is its own address, showing that one diff.
    pane.get_by_role("link", name=re.compile("monthly.nb.md")).first.click()
    page.wait_for_url(re.compile(r"/commit/[0-9a-f]{40}/reports/monthly.nb.md"), timeout=10000)
    for _ in range(15):
        page.wait_for_timeout(1000)
        if "reworked" in page.inner_text("body"):
            break
    body = page.inner_text("body").replace("\xa0", " ")
    assert "reworked" in body, "the chosen file's diff never arrived:\n" + body[-1200:]
    assert page.locator(".diff-editor").count() == 1, "one file, one diff"

    # And the way back is to the list you came in on, not to Contents.
    page.get_by_role("link", name="History").first.click()
    page.wait_for_url(re.compile(r"/files/default/test\?tab=history"), timeout=10000)
    page.wait_for_timeout(2000)
    assert "add, change and remove" in page.inner_text("body"), (
        "back from a commit lands somewhere else:\n" + page.inner_text("body")[-900:])


@check("file-history")
def file_history(page, base, _root):
    """A file's own History: the commits that touched it, and what each did.

    Between Source and Diff, because that is the order the questions come in:
    what does it say, what has it been, how does it differ from where it goes.
    """
    def write(path, text):
        return page.evaluate("""async ({ path, text }) => (await fetch(
            `/api/projects/default/branches/mine/notebooks/content?path=${path}`,
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: text })).status""",
            {"path": path, "text": text})

    def push(message):
        page.evaluate("""async ({ message }) => { await fetch(
            '/api/projects/default/branch/push', { method: 'POST',
            headers: {'Content-Type': 'application/json'},
            body: JSON.stringify({ message }) }); }""", {"message": message})

    def move(path, to):
        return page.evaluate("""async ({ path, to }) => (await fetch(
            `/api/projects/default/branches/mine/notebooks/move?path=${path}`,
            { method: 'POST', headers: {'Content-Type': 'application/json'},
              body: JSON.stringify({ to }) })).status""", {"path": path, "to": to})

    # Several lines, because the rename at the end has to stay recognisable as the
    # same file: git detects a rename by similarity, and a one-line file that
    # changes at all is 0% similar to itself.
    def report(last):
        return f"# Report\n\nalpha\nbeta\n{last}\n"

    # Three commits, one of which is about a different file entirely.
    assert write("reports/monthly.nb.md", report("First")) == 200
    push("add the monthly report")
    assert write("other.nb.md", "# Unrelated\n") == 200
    push("something else entirely")
    assert write("reports/monthly.nb.md", report("Second")) == 200
    push("rework the rollup")

    # Then it is renamed and edited in one commit, which is the shape the editor's
    # own rename produces.
    assert move("reports/monthly.nb.md", "reports/quarterly.nb.md") == 200
    assert write("reports/quarterly.nb.md", report("Third")) == 200
    push("rename the rollup")

    page.goto(f"{base}/files/default/mine/history/reports/quarterly.nb.md",
              wait_until="networkidle")
    page.wait_for_timeout(4000)
    body = page.inner_text("body").replace("\xa0", " ")

    # This file's commits, and not the branch's: the unrelated one is absent.
    assert "rework the rollup" in body, body[-1200:]
    assert "add the monthly report" in body, body[-1200:]
    assert "something else entirely" not in body, (
        "the branch's history, not the file's — a commit that never touched it:\n"
        + body[-1200:])

    # Across the rename: the two commits above are from when the file had another
    # name, and the list says where the name changed rather than starting again.
    assert "renamed from" in body and "reports/monthly.nb.md" in body, (
        "the history stops at the rename — a file's history is the file's:\n"
        + body[-1200:])

    # It opens on the newest change rather than making you click for anything.
    assert "Before (left) and after (right)" in body, body[-1200:]
    assert page.locator(".diff-editor").count() == 1, "no side-by-side diff"
    for _ in range(15):
        page.wait_for_timeout(1000)
        if "Third" in page.inner_text("body"):
            break
    assert "Third" in page.inner_text("body"), "the newest version is not in the diff"

    # And it is a comparison, not a creation: reading the left-hand side at the new
    # path would find nothing there and call a move the commit that made the file.
    body = page.inner_text("body").replace("\xa0", " ")
    assert "Added by" not in body, (
        "the rename reads as a creation — the left side was read at the new path:\n"
        + body[-1200:])
    assert "Second" in body, (
        "the pre-rename text should be on the left of the rename commit:\n"
        + body[-1200:])

    # A commit from before the rename opens at the name the file had then.
    page.get_by_role("button", name=re.compile("rework the rollup")).first.click()
    for _ in range(15):
        page.wait_for_timeout(1000)
        if "First" in page.inner_text("body"):
            break
    body = page.inner_text("body").replace("\xa0", " ")
    assert "First" in body and "Second" in body, (
        "a commit from before the rename shows nothing — it was fetched at today's "
        "path:\n" + body[-1200:])
    assert "Added by" not in body, body[-1200:]

    # And the graph rail is drawn beside the list.
    assert page.locator("ol svg").count() >= 2, "no rail beside the commits"

    # Clicking the oldest shows what it did — which was create the file, so it
    # says so rather than diffing against an empty left-hand side.
    page.get_by_role("button", name=re.compile("add the monthly report")).first.click()
    for _ in range(15):
        page.wait_for_timeout(1000)
        if "Added by" in page.inner_text("body"):
            break
    body = page.inner_text("body").replace("\xa0", " ")
    assert "Added by" in body, (
        "the commit that created the file should say so, not show an empty before:\n"
        + body[-1000:])


@check("merge-preview")
def merge_preview(page, base, _root):
    """The merge says what it will do, and whose work is whose.

    The complaint this answers: a confirm() box saying "1 file(s) changed there.
    Anything you have not committed is committed first" — which named no file,
    and read as though the person's own work was being merged into test.
    """
    def write(branch, path, text):
        return page.evaluate("""async ({ branch, path, text }) => (await fetch(
            `/api/projects/default/branches/${branch}/notebooks/content?path=${path}`,
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: text })).status""",
            {"branch": branch, "path": path, "text": text})

    assert write("mine", "shared.nb.md", "# Base\n") == 200
    page.evaluate("""async () => { await fetch('/api/projects/default/branch/push',
        { method: 'POST', headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({ message: 'base' }) }); }""")

    # Somebody else moves test on.
    import subprocess
    root = page.evaluate("async () => (await (await fetch('/api/health')).json()).notebooksRoot")
    test_tree = os.path.join(root, "test")
    with open(os.path.join(test_tree, "shared.nb.md"), "w") as f:
        f.write("# Theirs\n")
    for args in (["add", "-A"], ["-c", "user.email=t@x", "-c", "user.name=Grace Hopper",
                                 "commit", "-m", "rework the rollup"]):
        subprocess.run(["git", *args], cwd=test_tree, check=True, capture_output=True)

    # And I have something of my own, unsaved.
    assert write("mine", "only-mine.nb.md", "# Mine\n") == 200

    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_timeout(2500)
    explorer(page).get_by_role("button", name="Update from test").click()
    page.wait_for_timeout(3000)

    # The dialog, not the page: every one of these filenames is also in the
    # explorer behind it, so reading the body would pass on a preview that showed
    # nothing at all.
    dialog = page.locator(".modal").last.inner_text().replace("\xa0", " ")
    # The commit arriving, named, with its author and its file.
    assert "rework the rollup" in dialog, "the preview does not name what is coming:\n" + dialog
    assert "Grace Hopper" in dialog, dialog
    assert "shared.nb.md" in dialog, dialog
    # My own work, named separately, and said to stay on my branch.
    assert "only-mine.nb.md" in dialog, (
        "the preview does not say which of my files it commits:\n" + dialog)
    assert "Nothing of yours goes to test" in dialog, dialog
    # And the picture.
    assert page.locator(".modal svg[role=img]").count() >= 1, "no branch graph"

    # It merges only when asked, and reports what happened.
    page.get_by_role("button", name=re.compile("Merge .* into my branch")).click()
    for _ in range(20):
        page.wait_for_timeout(1000)
        if "Up to date with test" in page.inner_text("body"):
            break
    assert "Up to date with test" in page.inner_text("body"), page.inner_text("body")[-900:]
    # The file test changed is now mine, and my own is untouched.
    got = page.evaluate("""async () => await (await fetch(
        '/api/projects/default/branches/mine/notebooks/content?path=shared.nb.md')).text()""")
    assert "Theirs" in got, got


@check("missing-file")
def missing_file(page, base, _root):
    """A file that is not on the branch you are looking at says so.

    Every pane rendered "Loading…" for ever instead: `null` meant both "still
    reading" and "the read failed", and the pane could not tell them apart — so
    it contradicted the error banner directly above it, and the spinner was the
    more believable half.
    """
    import subprocess
    root = page.evaluate("async () => (await (await fetch('/api/health')).json()).notebooksRoot")

    def commit_into(tree, name, body, message):
        with open(os.path.join(root, tree, name), "w") as f:
            f.write(body)
        for args in (["add", "-A"], ["-c", "user.email=t@x", "-c", "user.name=Grace Hopper",
                                     "commit", "-m", message]):
            subprocess.run(["git", *args], cwd=os.path.join(root, tree), check=True,
                           capture_output=True)

    # Visit first, so the personal branch forks from test's head *now* — a branch
    # made afterwards would already carry everything below and prove nothing.
    page.goto(f"{base}/files/default", wait_until="networkidle")
    page.wait_for_timeout(2500)

    commit_into("prod", "prod-only.nb.md", "# Prod only\n", "prod only")
    commit_into("test", "test-only.nb.md", "# Test only\n", "test only")

    # On prod's file from your own branch: nothing to update, so it points at the
    # branch picker.
    page.goto(f"{base}/files/default/edit/mine/prod-only.nb.md", wait_until="networkidle")
    page.wait_for_timeout(3000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "prod-only.nb.md is not on this branch" in body, body[-900:]
    assert "Loading" not in body, "still says it is loading:\n" + body[-900:]
    assert "branch picker" in body, body[-900:]

    # Same file, same story, from test — the case worth checking separately
    # because test is not your branch and has no ↓ of its own.
    page.goto(f"{base}/files/default/edit/test/prod-only.nb.md", wait_until="networkidle")
    page.wait_for_timeout(3000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "prod-only.nb.md is not on this branch" in body, body[-900:]
    assert "Loading" not in body, body[-900:]

    # And test's file from your own branch, which *is* recoverable: this is the
    # one that should name the fix rather than shrug.
    page.goto(f"{base}/files/default/edit/mine/test-only.nb.md", wait_until="networkidle")
    page.wait_for_timeout(4000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "test-only.nb.md is not on this branch" in body, body[-900:]
    assert "update from test" in body, (
        "a file test has and yours does not should name the way to get it:\n" + body[-900:])

    # The Diff tab is where this was first noticed, so check it too.
    page.goto(f"{base}/files/default/diff/mine/prod-only.nb.md", wait_until="networkidle")
    page.wait_for_timeout(3000)
    assert "Loading" not in page.inner_text("body"), "the Diff tab still hangs"


@check("behind-test")
def behind_test(page, base, _root):
    """Being behind test is said about the file, not about every file.

    The report: every file's toolbar said "Update from test", including files
    that exist only on the person's own branch and that test has never seen. The
    branch was behind; the file was not, and the toolbar had only the count.
    """
    def write(branch, path, text):
        return page.evaluate("""async ({ branch, path, text }) => (await fetch(
            `/api/projects/default/branches/${branch}/notebooks/content?path=${path}`,
            { method: 'PUT', headers: {'Content-Type': 'text/plain'}, body: text })).status""",
            {"branch": branch, "path": path, "text": text})

    # A file both branches have: mine first, pushed, so test has a copy of it.
    assert write("mine", "shared.nb.md", "# Base\n") == 200
    page.evaluate("""async () => { await fetch('/api/projects/default/branch/push',
        { method: 'POST', headers: {'Content-Type': 'application/json'},
          body: JSON.stringify({ message: 'base' }) }); }""")

    # Then test moves on underneath it. Committed straight into the test worktree,
    # which is what somebody else pushing looks like from here.
    import subprocess
    root = page.evaluate("async () => (await (await fetch('/api/health')).json()).notebooksRoot")
    test_tree = os.path.join(root, "test")
    with open(os.path.join(test_tree, "shared.nb.md"), "w") as f:
        f.write("# Theirs\n")
    for args in (["add", "-A"], ["-c", "user.email=t@x", "-c", "user.name=T",
                                 "commit", "-m", "theirs"]):
        subprocess.run(["git", *args], cwd=test_tree, check=True, capture_output=True)

    # And something of mine test has never seen, left unpushed — which is also what
    # gives the branch something to push, so the disabled Push button below has a
    # reason to be drawn at all.
    assert write("mine", "only-mine.nb.md", "# Mine\n") == 200

    # The branch is behind, so the explorer offers the merge.
    page.goto(f"{base}/files/default/edit/mine/only-mine.nb.md", wait_until="networkidle")
    page.wait_for_timeout(4000)
    assert explorer(page).get_by_role("button", name="Update from test").count() == 1, \
        "the branch is behind test and the explorer does not say so"

    # But this file is not behind anything, and its toolbar must not claim it is.
    body = page.inner_text("body").replace("\xa0", " ")
    assert "behind test" not in body, (
        "a file test has never seen was called behind it:\n" + body[:1200])
    # And the push is offered — disabled, saying why — rather than replaced.
    push = page.get_by_role("button", name="Push to test")
    assert push.count() == 1, "no Push to test at all:\n" + body[:1200]
    assert push.first.is_disabled(), "push is offered while the server would refuse it"

    # The file test *did* change says so, on the same branch, in the same toolbar.
    page.goto(f"{base}/files/default/edit/mine/shared.nb.md", wait_until="networkidle")
    page.wait_for_timeout(4000)
    body = page.inner_text("body").replace("\xa0", " ")
    assert "behind test" in body, "the file test changed does not say so:\n" + body[:1200]

    # And its diff is against test, not production — test is where it goes next.
    assert "Diff vs test" in body, body[:1200]
    page.get_by_role("tab", name="Diff vs test").click()
    # Two fetches land here — this branch's copy and test's — so poll rather than
    # guess a number: "Loading…" is a real state, not a failure.
    for _ in range(20):
        page.wait_for_timeout(1000)
        if "Theirs" in page.inner_text("body"):
            break
    assert "Theirs" in page.inner_text("body"), (
        "the diff did not show test's version:\n" + page.inner_text("body")[-1200:])


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

    # And it is pill-shaped. This check read the text and the tooltip and passed
    # for a week over a 71x43 amber egg: the toolbar row is `items-stretch`, so a
    # chip with no height of its own grows to the full 44px and a full radius
    # turns that into an oval. Nothing that reads text can see that.
    box, row = pill.first.bounding_box(), page.locator(".nb-toolbar").first.bounding_box()
    assert box["height"] <= 24, (
        f"the pill is {box['width']:.0f}x{box['height']:.0f} in a "
        f"{row['height']:.0f}px row — it stretched")
    assert box["width"] > box["height"], f"taller than it is wide: {box}"
    top, bottom = box["y"] - row["y"], (row["y"] + row["height"]) - (box["y"] + box["height"])
    assert abs(top - bottom) <= 2, f"not centred in the row: {top:.0f} above, {bottom:.0f} below"


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
