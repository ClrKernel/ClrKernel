# Promotion UX — diagnosis and plan

Written after a user could not get a new `.nb.md` to production and gave up:

> "I created new in my local. Ran all, then promoted to test, then went to test —
> nothing about promoting to prod, only 'Copy to my branch'. … then i went back to
> personal branch and saw Promote to Prod as option, did that, then changed branch to
> prod in explorer dropdown, no files listed. then did a refresh on page and they
> appeared."

Not implemented yet. Phases are independently shippable.

## 1. What actually happened

### 1a. No promote on `test` — working as coded, and the coding is the bug

The whole Save/Push/**Promote** cluster sits behind one gate: `NotebookToolbar.tsx:512`
wraps it in `{canWrite && (...)}`, and `canWrite` is branch-gated —
`sessionContext.ts:43-45` ANDs `BranchAllows.write`, which `Editor.tsx:92` sets to
`branch === 'mine' && fileEditable(path)`.

So Promote renders **only on your own branch** — the one branch promotion has nothing
to do with. On `test` you get the read-only note and Copy to my branch
(`NotebookToolbar.tsx:493-509`), and nothing else.

**The server does not care.** `api.ts:731-742` hardcodes `scope('test')` for both
`promotionStatus` and `promote`, and `JobsApi.cs:1775` refuses any branch but `test`.
The client could render Promote from any branch view and it would work identically.

The Diff tab, which *is* shown on test, even renders "what promotion would ship"
(`Editor.tsx:66-72`) — with no way to ship it from there.

### 1b. Empty prod tree until F5 — real, and it took two wrong answers to confirm

`NotebookExplorer.tsx` fetched the tree **once, at mount**:
`usePolling(() => api.notebooks(), null)` — no interval, no deps. The editor stays
mounted across files and branches on purpose (`Editor.tsx:154-162`), and
`api.notebooks()` returns every environment in one payload, so switching the dropdown
to prod re-slices a payload fetched before the promotion happened. F5 refetches.
`promote()` reloaded only the promotion status; `push()` had the same gap for test.

**The record of getting there, because the mistake is instructive.**

*First conclusion: not reproduced.* Three browser checks behaved identically with and
without the fix, so it was reverted rather than shipped as a placebo. That was the
right call **on the evidence available** and the wrong answer, and two of those three
checks were broken in ways worth naming:

- one matched the filename in the **breadcrumb** rather than the tree — the file was
  open, so its name was on screen whatever the explorer showed;
- one drove the **breadcrumb's** branch picker rather than the explorer's, which
  navigates and therefore remounts the explorer, refetching for free.

*Second conclusion: real.* After the branch was rebased and the bundle rebuilt from a
clean tree, the corrected check failed **three times out of three**, with the server
reporting `[['mine', true], ['test', true], ['prod', false]]` for the pushed file
while the explorer, switched to test, did not list it. The data was there and the UI
was showing a stale tree. Restoring the fix: **two passes out of two.**

The most likely reason the earlier runs disagreed is a stale `wwwroot` — one of them
ran `./build.sh Web` from the wrong directory, which fails and leaves the previous
bundle in place, and that is exactly the trap `.claude/skills/studio-webapp-dev`
exists to warn about. **A check that cannot fail is not a check, and a check that
cannot fail because it is testing last week's bundle is worse: it says the opposite of
the truth with confidence.** Rebuild from the repo root and confirm the build
succeeded before believing either answer.

### 1c. Promotion status is also fetched once

`Editor.tsx:184-188` — `usePolling(..., null, [path])`. A job going green in test never
enables Promote until a save, push or path change refetches. This is why the button's
appearance felt arbitrary.

### 1d. Two defects the report did not reach

- **Role mismatch.** The POST requires `ProjectAdmin` (`JobsApi.cs:824`), the button
  renders behind `canWrite` = ProjectMember (`NotebookToolbar.tsx:512,552`), and the
  eligibility GET is Viewer-level (`JobsApi.cs:777`). **A Member sees an enabled Promote
  that 403s on click.**
- **Unpushed-work trap.** On `mine`, Promote ships *what is on test*, not what is on
  screen. With `ahead > 0` or a dirty tree the button can be green while your latest
  edits are not in the promotion. The Diff tab admits this
  (`Editor.tsx:1138-1143`); the button does not.

### 1e. Why it never lit up

Each refusal is true and none names the next step. "Ran all" is interactive execution and
never evidence (`Promotion.cs:137-147`). With no jobs: *"No jobs are defined for this
notebook in test"* (`Promotion.cs:120`). Before the first push: *"Nothing to promote:
'<path>' exists in neither environment"* (`Promotion.cs:86-88`) — technically true,
hostile to someone looking at the file on their branch. And the reasons live behind a
small ⓘ next to a disabled button, because a disabled button swallows clicks
(`NotebookToolbar.tsx:552-568`).

### 1f. A job's Overview tab

Not a promotion surface, and shouldn't be: the promotable unit is the notebook + its jobs
file, deliberately (`Promotion.cs:38-45`). The fix is 1a — the file-level Promote showing
up on test — not a per-job button.

## 2. The model problem

**Assumed:** environments are places; the action that moves content lives where the
content is. To promote what is in test, go to test.

**Implemented:** the personal-branch editor is a pipeline cockpit — Save, Push, Promote on
one toolbar — and every other branch view is "read-only, go away", offering only Copy to
my branch, which actively teaches *nothing is done from here*.

Neither is wrong. But the app never states its model, hides the cockpit's promote lever
behind an unrelated gate, phrases blockers in test/prod vocabulary while you stand on
`mine`, and never names the sequence *push → add a job → run it in test → promote*. The
docs know it (`docs/studio.md:617-647`); the UI does not teach it.

## 3. Target

**Promote is a pipeline action about test → prod, visible wherever the pipeline is —
`mine` and `test` — and it always answers when clicked.**

- **`mine`:** toolbar shape unchanged. Promote is **never disabled**: eligible + admin →
  the existing confirm; blocked → the reasons, opened by the click itself. With unpushed
  work the text says promotion ships what is committed on test.
- **`test`:** the same Promote beside Copy to my branch. Admins act; members see it and
  learn why they cannot.
- **`prod` and others' branches:** absent. Nothing to promote from there.
- **Blocked is a checklist, not a grievance list** — push to test → add a job → run it in
  test → promote, with the current step marked and server reasons rendered under the step
  they belong to.
- **A brand-new notebook with no jobs** — the case that started this — shows Promote from
  the first moment and answers with step 1.
- **After promoting**, the tree is fresh without a reload.

## 4. Phases

**Phase 1 — bugs, shippable today.**
`NotebookExplorer.tsx` takes a `refresh` prop and `[env, refresh]` deps; `Editor.tsx`
bumps it in `promote()` and `push()`. `Editor.tsx:184-188` polls at 15s like
`branchStanding` beside it. `NotebookToolbar.tsx` uses `useIsProjectAdmin`
(`sessionContext.ts:67-69`) so a Member sees a blocked button and the reason, not a 403.
Checked with Playwright — promote, switch to prod, assert the row without `page.reload()`
— and by breaking each fix first.

**Phase 2 — Promote on test, always clickable.**
Split the `canWrite` cluster: Undo/File/Save/Push stay; Promote moves behind
`(branch === 'mine' && canWrite) || (branch === 'test' && member)`. Drop `disabled` for
eligibility; blocked click opens the reasons. The rule becomes one pure function
`promoteControl(branch, isAdmin, isMember, promotion)` in `notebookToolbar.ts`, vitest'd
as a table. Screenshot at 940px to confirm the bar still does not scroll sideways.

**Phase 3 — the checklist.**
New React-free `promotionSteps.ts` (+ tests): `standing` + server `reasons[]` + `isAdmin`
→ ordered steps with state. Map the two hostile strings onto steps client-side; unmapped
reasons attach verbatim. Emit the unpushed-work warning for the eligible-but-ahead case.
Optionally append "— push it to test first" to `Promotion.cs:86-88`.

**Phase 4 — "Add a job" from the editor.**
Extract Files' `schedule()`/`jobsFileFor()` (`Files.tsx:125-127,171-184`) into a shared
helper; the File menu gains "Schedule (add a job)…", and the checklist's step 2 links to
it. Today that affordance exists only on the Files page.

**Phase 5 — `docs/studio.md`**: where Promote lives now, that it is always clickable, and
the member-visible/admin-actionable rule.

## 5. Deliberately not doing

- **No promotion wizard or Deployments page.** The house style is one toolbar plus
  on-demand toasts; permanent banners were deliberately removed
  (`docs/internal/app-shell-redesign-spec.md`).
- **No per-job Promote.** The promotable unit is the pair, and a job-level control would
  misstate the gate.
- **No relaxing the gate.** "Every enabled job has a clean green run of exactly these
  bytes" is the product's promise. The problem is legibility, not strictness.
- **No DOM testing library.** New decision logic goes into React-free modules for vitest,
  with Playwright for the rendered truth.
- **No SSE.** A 15s poll and reload-on-action match `branchStanding` beside it.
- **No renaming "Push to test".** Two actions both called promote would be worse than the
  current asymmetry.
- **No branch-parameterised promote endpoint.** `JobsApi.cs:1771-1781` refusing anything
  but test is a correctness guard.
