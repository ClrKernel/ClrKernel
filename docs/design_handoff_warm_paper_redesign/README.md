# Handoff: ClrKernel Jobs — "Warm Paper" redesign

## Overview
A full visual redesign + interactive prototype of the ClrKernel Jobs webapp (`src/ClrKernel.Jobs/webapp/`). It replaces the current top-nav layout with an ADO-inspired shell — icon rail, breadcrumb header, tabs — on a warm cream palette with the ClrKernel green as the brand accent. Covers: Dashboard, Jobs list, Job detail (form / runs / YAML), Run detail (live cell progress / log), Notebooks (file explorer with branch picker), the Notebook editor (Normal + Focus modes), Channels, and Settings.

## About the Design Files
The files in this bundle are **design references created in HTML** (single-file interactive prototypes) — they show intended look and behavior, not production code to copy. The task is to **recreate these designs in the existing codebase**: the React 18 + Vite + TypeScript app at `src/ClrKernel.Jobs/webapp/` in `ClrKernel/ClrKernel`. The user wants the implementation on **Tailwind CSS + shadcn/ui** (replacing the current hand-rolled `styles.css`). Keep the existing app structure (react-router routes, `usePolling`, `api.ts`) and re-skin/re-structure the UI layer.

## Fidelity
**High-fidelity.** Colors, typography, spacing, radii, and copy are final. Recreate pixel-perfectly using Tailwind utilities and shadcn components.

## Migration notes (Tailwind + shadcn)
- Map the palette below into `tailwind.config` / CSS variables following shadcn's theming convention (`--background`, `--card`, `--border`, `--muted-foreground`, `--primary`, etc.). Suggested mapping:
  - `--background: #faf9f6` · `--card: #fffefb` · `--secondary`(panel): `#f5f3ee` · `--border: #e6e2d8` · `--foreground: #22251f` · `--muted-foreground: #6f6b60` (secondary muted `#8a8577`) · `--primary: #0e6e43` (hover `#0a5636`) · `--destructive: #b91c1c`
  - Status: success `#15803d`, failed `#b91c1c`, running `#1d4ed8`, warn/dev `#b45309`, pending/muted `#b6b0a2`
  - env chips: dev = `#b45309` on `#fdf3e3`, border `#f0dcbb`; prod = `#0e6e43` on `#e9f3ec`, border `#cfe4d6`
- shadcn component mapping: Button (default = green primary; outline = cream bordered), Tabs (underline variant — 2px bottom border in primary), Badge (outlined pill for run status, tinted pill for env), Table, Card (stat cards), Input/Textarea/Checkbox/Label (job form), Select (branch picker, cell language), Alert (banners: ok/error/`Not promotable yet`), Tooltip (rail icons), Separator, ScrollArea (log, cell list), Dialog (future connection wizard).
- Fonts: **Instrument Sans** (UI) + **JetBrains Mono** (code, paths, cron, ids). Add via Google Fonts or self-host; wire as `font-sans` / `font-mono` in Tailwind.
- Keep Monaco for the real editor cells; the prototype's line-numbered pane is a stand-in.
- Icons: lucide-react (ships with shadcn) — the prototype uses lucide outlines: layout-grid, play, book-open, bell, settings, search, git-branch.

## Screens / Views

### App shell (all pages)
- **Icon rail**, 48px wide, `#fffefb`, right border `#e6e2d8`. Top: 28px brand square, radius 7px, `#0e6e43`, white mono `>_`. Below: 32px square icon buttons (radius 7px); active = bg `#eef3ee`, icon `#0e6e43`; inactive icon `#8a8577`; hover bg `#eef3ee`. Order: Dashboard, Jobs, Notebooks, Channels, spacer, Settings.
- **Header**, 46px, `#fffefb`, bottom border: brand text "ClrKernel Jobs" (600) · `/` separators (`#b6b0a2`) · section (muted) · detail (600). Right: search input (230px, radius 7px, bg `#faf9f6`), env pill `dev` (dev chip colors), 26px avatar circle `#0e6e43`.
- Base font 13.5px; page titles 19px/700; content padding 20px 28px.

### Dashboard
4 stat cards (border, radius 9px, bg `#fffefb`, value 22px/700 — success rate green, failed red, in-flight blue) + "Recent runs" table. Table: header row muted 500, row borders `#efece4`, cell padding 7px 10px; status = outlined pill in status color; job name green 600 + env chip; notebook path mono 12px muted. Rows clickable → Run detail; hover bg `#f3f1ea`.

### Jobs
Title row with green "New job" button. Table: Job (+env chip), Notebook (mono), Schedule (mono cron or "manual"/"after X"), Depends on, Last run status pill. Rows → Job detail.

### Job detail
Header: job name + env chip; actions: "Run now" (outline), "Promote to production" (primary), "Delete" (outline, red on hover). Banner slot (ok = green tint, error = red tint). Tabs: Overview / Runs / YAML (underline style).
- Overview = form, max-width 560px, labels 13px/500 with muted hints; inputs radius 7px border `#ded9cd`; mono for notebook path/cron/parameters JSON textarea; Save (primary) + Cancel.
- Runs = run history table. YAML = generated jobs-yaml in a mono `pre` on `#f5f3ee`.
- Behavior: Run now creates a run and navigates to it; Promote blocked with error banner if last run failed; parameters validated as JSON on save.

### Run detail
Header: job name (link) + status pill; when live: "live · refreshing" + Cancel run. Meta row (mono path, trigger, started, took). Failed runs show a red mono error banner. 4px progress bar (green; red when failed). Tabs: Cells / Log / Notebook.
- Cells: rows = index (n/total mono), status pill, source `pre` (mono 12px), duration right-aligned mono. Running row bg `#eef3ee`; failed row bg `#fdf1ef` with error text under source; downstream cells "Skipped".
- Live runs advance cell-by-cell (poll-driven in the real app).

### Notebooks
Intro line, then **branch picker**: git-branch icon + Select with `prod`, `test`, `dev-jsadams` (mono); `prod` shows a "read-only" chip. Below: file explorer card (the explorer appears ONLY here and in the editor sidebar — never on Dashboard/Jobs/Channels/Settings) (border, radius 9px) — folders toggle (▾/▸), files mono 12px, jobs-yaml files muted; per-file: job chips (green tinted pill → job detail) and "+ job" action; clicking a notebook name opens the Editor.

### Notebook editor — file explorer sidebar
Left sidebar shown only in the editor: header "EXPLORER" with a collapse ‹ button (collapsed = 16px rail with › to reopen), branch Select (`prod` / `test` / `dev-jsadams`, mono 11.5px), then the notebook tree (folders toggle, files mono, active file highlighted `#eae7de` with 2px green left edge). Default width 218px, user-resizable by dragging a 5px col-resize splitter (clamp 150-420px; splitter hover = primary green). Implement with shadcn Resizable (react-resizable-panels) + Collapsible.

### Notebook editor — Normal mode
Toolbar (44px, card bg): tabs Notebook / Source / Diff vs production; right: kernel status (green dot + mono "ClrKernel 0.10.0.0 · idle"), Normal|Focus segmented toggle (active = primary bg), "▷ Run All", "↺ Restart kernel", "Save", "Promote to production" (primary; desaturated `#a8c4b4` when not promotable). "Not promotable yet" notice (white card, orange `#b45309` title + bullet) when the notebook has no jobs.
Cells (gap 14px, radius 8px, left edge 3px green when succeeded):
- Code cell: 40px gutter (`#f5f3ee`, run ▶ top, exec count `[1]` bottom mono 10.5px), source with light syntax color (keywords `#1d4ed8`, strings `#b45309`, numbers `#0e6e43`, directives `#b91c1c`), footer strip (status dot + "succeeded", spacer, optional "⛁ Connect", language picker "C# ▾").
- Markdown cell: rendered prose, footer "Markdown ▾".
- Output: attached under the cell on `#faf9f6`, indented past gutter; interactive grid = "Filter all columns…" input (live), Clear, Analyze, "n rows"; header cells bg `#f0ede4` with sort carets; numeric columns right-aligned mono.

### Notebook editor — Focus mode
Bordered shell: left CONTENTS sidebar (240px, `#f5f3ee`): sticky uppercase header + collapse ‹; tree = section rows (▾, sans 600) and leaf rows (mono 11.5px, ¶ for markdown, `[1]`/`[ ]` for code); active leaf = solid `#0e6e43`, white text. Right: cell toolbar (Cell [1], ▶ Run, status, spacer, Clear output, language, red ×), line-numbered source pane (numbers `#c2bcae`, 38px right-aligned), output pane below (same grid) split by a border.

### Channels / Settings
Channels: simple table (name 600, type, target mono). Settings: intro, sections ("Server", "Git workflow") as label/value/meta tables — locked values plain text with source chips ("env: CLRK_API_KEY", "host-only", "restart to apply"); editable = inputs/checkbox; green Save button; ok banner on save.

## Interactions & Behavior
- Rail + breadcrumb navigate; search filters jobs and runs live.
- Run simulation: Run now → run appears at top of tables as Running, progresses cell-by-cell, Cancel → Cancelled; success rate/stat cards recompute.
- Editor: Run All marks code cells succeeded (numbers execution counts); Restart kernel resets; grid filter is live across all columns; Focus TOC click selects the cell; branch picker switches context.
- Hovers: table rows tint, outline buttons take green border/text, destructive actions take red.

## State Management
Route-level: current page, selected job, selected run, active tab per page, search query. Editor: mode (normal/focus), tab, focused cell index, run state per cell, branch. Data via existing `api.ts` + `usePolling` (runs poll at 2-3s while active).

## Design Tokens
- Radii: 7px (controls), 8px (cells), 9px (cards), 999px (pills)
- Spacing: cell padding 7px 10px (tables), content 20px 28px, form gap 13px
- Type: 19px/700 titles, 14px/600 section heads, 13.5px body, 12.5-13px tables, 12px mono paths, 11-11.5px chips/labels
- Shadows: essentially none (borders carry hierarchy); popovers may use `0 4px 14px rgba(0,0,0,.16)`

## Assets
No raster assets. Brand mark = green rounded square + mono `>_` (recreate in code). Icons from lucide-react.

## Files
- `ClrKernel Jobs Prototype.dc.html` — the interactive prototype (all screens; open in a browser)
- `ClrKernel Jobs Directions.dc.html` — the three explored directions (1a Warm Paper was chosen)
Target codebase: `ClrKernel/ClrKernel` → `src/ClrKernel.Jobs/webapp/` (React 18 + Vite + TS; replace `src/styles.css` with Tailwind + shadcn theme).
