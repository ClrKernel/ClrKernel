# HANDOFF-24 — A documentation site that nobody maintains

The README had grown into the manual, and the manual was a 600-line scroll on a GitHub
repo page. People installing a dotnet tool, a VS Code extension, a Docker image and a
handful of NuGet packages deserve a site with a sidebar, search, and screenshots — but
not a second copy of the prose that drifts from the first. So the site has **no content
of its own**. It is a build artifact of the repository, regenerated on every push to
`main`, hosted on GitHub Pages so that a community contributor who fixes a sentence in
the README has fixed the website.

## What was chosen, and why

**Astro Starlight** for the site. Built-in search (Pagefind, static, no Algolia account),
dark mode, sidebar autogeneration from folders, and it's Node — which CI already installs
for the Studio webapp, so there is no new toolchain. Docusaurus was the runner-up; it
only wins if we ever want versioned docs side by side (`v0.x` / `v1.x`), and it's heavier
to build. MkDocs Material looks great but is Python. Note Starlight 0.39 changed the
sidebar syntax to `{ label, items: [{ autogenerate }] }`; the config uses the new form.

**DocFX as an extractor, not a site.** `docfx metadata --outputFormat markdown` turns the
XML doc comments (already on via `GenerateDocumentationFile`) into one markdown file per
type, and the sync script gives each one the frontmatter Starlight requires. We get the
.NET-native reference without living with DocFX's theme.

**GitHub Pages** over Cloudflare Pages: the repo is the source of truth and the project
is open source; keeping hosting inside GitHub means a fork gets a working site by
enabling Pages and setting `DOCS_BASE`.

## How the site is assembled

`.github/workflows/docs.yml` (push to `main` touching README/docs/samples/src/props/docs-site,
or manual dispatch):

1. `dotnet build ClrKernel.slnx -c Release` — only so `--help` can be captured.
2. `scripts/gen-cli-reference.sh` — runs `clrkernel --help`, `clrkernel run --help`,
   `clrkernel-studio --help`, `clrkernel-studio serve --help` into `scripts/out/cli/*.txt`.
3. `docfx metadata docfx.json` — `src/ClrKernel*/*.csproj` minus Studio and the CLI
   host, `memberLayout: samePage` so it's one page per type, into `src/content/docs/api/`.
4. `node scripts/sync-content.mjs` — everything else, see below.
5. `astro build` → `actions/upload-pages-artifact` → `actions/deploy-pages`.

### What `sync-content.mjs` does

- **README** is cut at every `##`; inside **Use**, at every `###`. Each cut becomes a page
  under `guide/` (Build & test, Develop go to `contributing/`; License is dropped).
  Headings are demoted so each page starts at H2 under the frontmatter title. A
  `readmePages` table in the script gives the important sections stable slugs
  (`guide/sql/`, `guide/analysis-services/`) and sidebar order; anything not in the
  table gets `kebab(title)`.
- **Anchors survive the split.** `[below](#sql-cells)` in the README is rewritten to the
  page that now owns that heading, with the anchor kept when it's a sub-heading. The
  script builds the map from the headings it saw, so it doesn't need updating when
  sections are added.
- **Links** to `samples/X.nb.md` → the sample page; `docs/studio.md`, `docs/docker.md` →
  the Studio pages; `docs/images/*` (including the one `raw.githubusercontent` URL the
  README uses) → `public/images/`; any other repo path → a GitHub blob/tree link.
- **Samples**: the `.nb.md` files are copied as pages with a "this page is a runnable
  notebook" tip at the top linking to the source. That's the pitch of the format made
  visible — a reader can open the page they're reading in VS Code and run it.
- **Packages**: a table from `<PackageId>`/`<Description>` of every project under `src/`,
  split into dotnet tools (`PackAsTool`) and libraries, each linked to nuget.org.
- **Version**: `<Version>` from `Directory.Build.props` → `src/data/version.json`, which
  the landing page imports for the install line.

Everything the script writes is gitignored; only `index.mdx`, the config, the script,
`docfx.json` and this README are committed.

## Verified

`npm install`, `docfx metadata`, `gen-cli-reference.sh`, sync and `astro build` were all
run locally against the current tree: 13 README pages, 13 samples, 26 packages, 3 CLI
captures, 362 API pages, 394 HTML pages, Pagefind index built, base path `/ClrKernel`
applied to every internal link.

**DocFX was run for real** — the risk flagged in the first draft of this handoff was
real, and it found three things, all in `fixApi()`:

- **The build failed outright.** DocFX escapes generics in its headings, so
  `Class Message<T\>` became a double-quoted YAML title containing `\>` — an unknown
  escape sequence, and `astro build` stopped on it. `fm()` now escapes backslashes.
- **Every title carried an anchor.** DocFX writes
  `# <a id="ClrKernel_..."></a> Class Message<T\>`; the tag landed verbatim in the
  sidebar and the `<title>`. Stripped, along with the escaping.
- **4,008 dead links.** DocFX cross-references its own output by file name
  (`](ClrKernel.Core.Scripting.md)`) — relative `.md` links to files `fixApi` then
  renames. Every one 404'd. They are now rewritten to the page each file becomes, and
  the `#member` anchors survive because DocFX's inline `<a id>` tags do.

Two consequences worth keeping:

- **docfx is pinned to 2.78.5** in the workflow and in `docs-site/README.md`. All three
  fixes above are couplings to its exact markdown output; an unpinned upgrade can
  reintroduce them in a build that still goes green.
- **`npm run check` (`scripts/check-links.mjs`) runs after `astro build` in CI** and
  fails on any internal link or anchor that does not resolve. That is the backstop for
  the pin, and it is what makes the rewriting in `fixApi` safe to maintain.

Two smaller things the local run caught: `gen-cli-reference.sh` was not executable
(the workflow calls it as `./scripts/…`), and `clrkernel-studio serve --help` prints
the same text as `clrkernel-studio --help`, so `syncCli` now prints identical help once.
Starlight asks for `/favicon.svg` by name and there was none — `public/favicon.svg` is a
placeholder; swap in the real icon.

One macOS-only trap, fixed: `apiSlug` lowercases, so `ClrKernel.md` → `clrkernel.md`
differs only by case. Writing the new name and deleting the old one deleted the file it
had just written on case-insensitive APFS — the namespace root page vanished locally and
would have been fine on Linux CI. `fixApi` renames before reading now, so there is no
delete at all.

## Known rough edges (all fixable in the README, which is the point)

- ~~The README's headless-execution paragraph has no heading, so it lands at the bottom
  of the **Fabric warehouse writes** page.~~ Fixed in the README with a
  `### Headless execution` heading, which is the point: the site was the thing that
  made the gap visible, and the fix went in the source.
- Sample page slugs are `kebab(filename)`: `SqlEtl.nb.md` → `/samples/sqletl/`. Fine,
  but renaming a sample changes its URL.
- ~~No screenshots of Studio.~~ Done, but **not** the way this bullet proposed.
  `test/tools/studio_screenshots.py` captures Dashboard / Files / Focus Mode and
  `docs/studio.md` embeds them. Three things the sketch had wrong:
  **(1)** they are committed to `docs/images/studio/`, not generated into
  `docs-site/public/images/` — that directory is gitignored, and `studio.md` is read
  on GitHub as well as on the site, so generating them would leave broken images on
  the copy most people see first. The cost is that they go stale unless someone
  re-runs the script.
  **(2)** it is a local script, not a workflow job. Capturing needs a signed-in
  session, and passkeys are the only way in — the harness registers a CDP virtual
  authenticator before the first navigation and creates a throwaway admin.
  **(3)** `docs/examples/docker/notebooks` is one notebook and one job, which
  photographs as an empty app. The fixture is six real `samples/*.nb.md` in folders,
  three `*.jobs.yaml`, and four seeded runs, in a temp workspace named `analytics`
  because `git init` puts the folder name in every breadcrumb.

  It now covers all thirteen nav destinations, and what that took was **fixture, not
  Playwright**: an empty page photographs as a broken one, so Channels and
  Notifications get seeded rules and destinations, Connections gets a throwaway
  PostgreSQL from `dev/docker-compose.dbs.yml` with a small `sales` schema, and Diff
  vs production needed a commit on `test` — the view compares the two branches that
  *run*, never your own, so writing to `mine` produced a diff of nothing. The
  Connections shots skip themselves with a printed line when there is no docker
  rather than leaving the previous PNG in place and looking fresh.

  Every shot asserts something specific before the shutter: the first attempt waited
  on `svg`, matched a toolbar icon, and produced a perfectly valid screenshot of a
  cell that was still pending. The Focus Mode shot now waits for **Run all cells** to
  re-enable and for a diagram inside the output iframe — Mermaid renders client-side
  in a sandbox, so it is never a node of the parent document.
- `lastUpdated: true` is inert: the generated pages are gitignored, so Starlight finds no
  history and renders no date at all — harmless, but it does nothing until the script
  passes through the source file's last commit. `editLink.baseUrl` is inert for the same
  kind of reason: every generated page sets `editUrl: false` and links its own source in
  a Source note instead.

## Files to stage

```
.github/workflows/docs.yml
docs-site/.gitignore
docs-site/README.md
docs-site/astro.config.mjs
docs-site/docfx.json
docs-site/package.json
docs-site/scripts/gen-cli-reference.sh
docs-site/scripts/sync-content.mjs
docs-site/src/content.config.ts
docs-site/package-lock.json
docs-site/public/favicon.svg
docs-site/scripts/check-links.mjs
docs-site/src/content/docs/index.mdx
docs-site/src/styles/custom.css
docs/handoff/HANDOFF-24-docs-site.md
README.md                              (the ### Headless execution heading)
```

`gen-cli-reference.sh` has to be committed executable — the workflow runs it as
`./scripts/gen-cli-reference.sh`.

## Before the first deploy

In the repo settings → Pages → **Source: GitHub Actions** (once; the workflow's
`deploy-pages` step fails until this is set). Then push, or run **Docs** from the
Actions tab. The site lands at https://clrkernel.github.io/ClrKernel/.

Suggested commit message:

```
Add auto-generated docs site (Starlight + DocFX → GitHub Pages)

Every page is built from the repo on push to main: README split into
guide pages, samples/*.nb.md as runnable-example pages, docs/studio.md
and docker.md, a package table from the csproj descriptions, --help
captures, and an API reference from the XML doc comments via docfx.
Nothing on the site is hand-maintained.
```
