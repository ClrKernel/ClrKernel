# ClrKernel docs site

Published at https://clrkernel.github.io/ClrKernel/ by the `Docs` workflow on every push
to `main` that touches `README.md`, `docs/`, `samples/`, `src/` or this folder.

**There is nothing to edit in here to change the site's content.** Every page except
the landing page is generated from the repository:

| Site section     | Source                                                          |
| ---------------- | --------------------------------------------------------------- |
| Guide            | `README.md`, one page per `##` (and per `###` under **Use**)    |
| VS Code          | `editors/vscode/README.md` — the extension's own docs, one page per `##` |
| Samples          | `samples/*.nb.md` — the notebooks *are* the pages               |
| Studio           | `docs/studio.md`, split one page per `##` (and per `###` under **Getting around**) |
| Studio → Docker  | `docs/docker.md`, whole — it is one walkthrough, numbered 1 to 5 |
| Guide (extra)    | any other `docs/*.md` — `docs/internal/` is never published      |
| Reference → Packages | `<PackageId>` / `<Description>` from every `src/*/*.csproj` |
| Reference → CLI  | `--help` output captured from the built tools                   |
| API              | `///` XML doc comments, via [DocFX](https://dotnet.github.io/docfx/) |
| Contributing     | `README.md` → **Build & test**, **Develop**; the extension README → **Developing this extension** |
| version stamp    | `<Version>` in `Directory.Build.props`                          |

The extension's README is the truth about the extension — the root README names VS
Code as one of four ways to run a notebook and links to it, so the page here and the
one on the Marketplace cannot drift apart.

So: to fix a typo in the SQL guide, edit `README.md`. To add a worked example, add a
`samples/Foo.nb.md`. To document a method, write a `///` summary on it. To add a page,
write a `docs/whatever.md` — it publishes itself under **Guide**, and the `docsPages`
table in `scripts/sync-content.mjs` is where you send it somewhere else. See
[`docs/README.md`](../docs/README.md) for the published/internal split.

## Run it locally

```bash
cd docs-site
npm install
node scripts/sync-content.mjs   # README/docs/samples/csproj -> src/content/docs
npm run dev                      # http://localhost:4321/ClrKernel/
```

For the API reference you also need the .NET SDK and docfx — pinned, because sync
rewrites DocFX's output and depends on its exact shape:

```bash
dotnet tool install -g docfx --version 2.78.5   # the version CI uses
npm run api                                     # BEFORE npm run sync
```

For the CLI page, build the solution (`dotnet build ClrKernel.slnx -c Release`) and run
`scripts/gen-cli-reference.sh` before sync. Both are optional locally — without them the
API section is a placeholder and the CLI page is simply absent.

`npm run check` walks the built `dist/` and fails on any internal link or anchor that
does not resolve. CI runs it after the build; run it yourself after touching
`sync-content.mjs` or bumping docfx.

`DOCS_BASE` overrides the `/ClrKernel` base path (a fork publishing to its own Pages
would set it to `/<repo>`).

## What is hand-written here

- `src/content/docs/index.mdx` — the landing page
- `astro.config.mjs` — sidebar groups, theme
- `scripts/sync-content.mjs` — the generator; page slugs and sidebar order for README
  sections live in its `readmePages` table
- `docfx.json` — which projects feed the API reference
- `scripts/check-links.mjs` — the post-build link check
- `public/favicon.svg` — placeholder mark; swap in the real project icon

Built with [Astro Starlight](https://starlight.astro.build/).
