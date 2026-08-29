# docs/

**Everything in this folder is published** to https://clrkernel.github.io/ClrKernel/ —
**except `internal/`, which never is.** That is the whole rule; the path tells you which
half a file is in.

| Path | Audience | On the site |
| --- | --- | --- |
| `docs/*.md` | users | yes — a page, generated on every push to `main` |
| `docs/images/`, `docs/examples/` | users | yes — as assets the pages link to |
| `docs/internal/` | us | **no** |

`docs/images/studio/` is generated. `python3 test/tools/studio_screenshots.py` boots a
throwaway Studio against a temp workspace, signs in with a virtual passkey, runs a
notebook, queries a throwaway PostgreSQL and captures the thirteen shots `studio.md`
embeds (`--list` names them, `--only` re-takes one). Re-run it when the app's
look changes; the PNGs are committed because `studio.md` is read on GitHub too, where
a file generated into `docs-site/public/` would be a broken image.

## Adding a user document

Write `docs/whatever.md` and push. It publishes itself as a page under **Guide**, and
`docs-site/scripts/sync-content.mjs` prints a line saying it did. To put it somewhere
else — its own sidebar section, a particular order — add it to the `docsPages` table in
that script. Nothing else is needed, and there is no way to add a document here and have
it silently not appear.

Links between documents survive the move to the site: `[docker.md](docker.md#passwords)`
becomes a link to the docker page and keeps its anchor. A link to anything outside
`docs/` becomes a GitHub link.

## What is in `internal/`

The engineering record — not documentation, and not a to-do list:

- `handoff/HANDOFF-NN-*.md` — the design-decision record. **Check here for "why is X this
  way" before re-deriving it.** One entry per piece of work, written when it landed.
- `*-spec.md` — design specs for Studio's web app, written before the work.
- `windows-verification-checklist.md`, `windows-1.0-verification.md` — the manual gates
  that have to pass on Windows before 1.0.
- `design_handoff_warm_paper_redesign/` — the visual prototypes the Studio redesign came from.

These are for anyone working on ClrKernel, including future you. They are kept in the
repository rather than a wiki so they version with the code they explain — but they
describe *decisions*, not the current state, so they are deliberately not user-facing.
When something in here is worth telling users, write it in a `docs/*.md` instead of
linking into `internal/`.

## A note on `///` comments

The site publishes the XML doc comments from `src/` as an API reference, so a `///`
comment is user-facing text. Keep internal pointers — a handoff reference, a phase
label, a `ponytail:` note — in `//` comments, which are not published.
