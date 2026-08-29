# Redesign: App Shell + Base Theme (ClrKernel.Studio web UI)

## Summary

Restructure the app chrome and establish a real design system underneath it.
Three things change:

1. **Navigation moves from a horizontal top nav to a fixed left icon rail**,
   VS Code activity-bar style — icons only, label on hover.
2. **The top bar shrinks to a context strip**: breadcrumb on the left, theme
   picker and settings on the right. Nothing else.
3. **The content region goes full width** (padded, not centered in a
   max-width column), and everything inside it is rebuilt on **shadcn/ui +
   lucide-react + a token-based theme** with a 5-option accent picker.

The notebook cell UX (Normal/Focus modes, cell toolbars, execution) does not
change behaviorally in this pass. Only its styling is brought onto the tokens.

## Design direction

The brief is "simple and functional, visually appealing, not too busy, not too
dark." Concretely, that means the following rules. Follow them rather than
inventing an aesthetic.

- **Light, low-chroma base.** App background is a near-white neutral; surfaces
  (cards, editors, panels) are pure white on top of it. This gives separation
  without shadows.
- **Hairline borders, not elevation.** 1px neutral borders define regions.
  Shadows are reserved for genuinely floating layers only — dropdowns, popovers,
  tooltips, dialogs. No card shadows, no gradients, no glass effects.
- **Color is semantic, never decorative.** The accent appears on: the active nav
  indicator, primary buttons, focus rings, and links. Status colors (amber
  warning, red error, green success, blue running) appear only on status.
  Everything else is neutral. If a colored element isn't communicating state or
  the primary action, it should be gray.
- **Compact density.** This is a developer tool, not a marketing site. Base font
  14px, small/control text 13px, default control height 32px, 4px spacing grid.
  Tighten the current generous vertical rhythm.
- **One radius token** used everywhere (6px). No mixed corner treatments.
- **Type**: system UI stack for chrome and prose; a single mono stack for code,
  cell language labels, kernel version/status, and execution counts. Chrome and
  code should be visually distinct at a glance.
- **Restraint check**: if a screen has more than one thing competing for
  attention, the wrong things are colored or bordered. Neutralize until only the
  primary action stands out.

## Layout

```
┌────┬──────────────────────────────────────────────────────────────────────┐
│ ▣  │  Notebooks  ›  demo.nb.md  [dev]                          ◐    ⚙     │ 48px
│────│──────────────────────────────────────────────────────────────────────┤
│ ⌂  │                                                                      │
│ ▶  │   page header — title, page-level actions (Saved / Promote…)         │
│ 📓 │   ────────────────────────────────────────────────────────────────   │
│ ⇄  │   page content, full width, 24px horizontal padding                  │
│    │                                                                      │
│    │                                                                      │
│    │                                                                      │
└────┴──────────────────────────────────────────────────────────────────────┘
 48px
```

- Shell is a 2-column CSS grid: `48px 1fr`, full viewport height, no page-level
  scroll. The content region is the scroll container (`overflow: auto`), except
  on the notebook page where Focus Mode manages its own panes.
- The rail and top bar are fixed; only content scrolls.

### Left rail

- 48px fixed width, full height, own background one step darker than the app
  background, 1px right border.
- Top: compact product mark (the `ClrKernel Studio` wordmark moves out of the nav;
  a square mark stands in). Clicking it goes to Dashboard.
- Below: nav items — **Dashboard, Jobs, Notebooks, Channels**. 20px lucide
  icons, 40×40 hit target, ~4px gap.
- Suggested icons: Dashboard → `LayoutDashboard`, Jobs → `Play` or `ListChecks`,
  Notebooks → `NotebookText`, Channels → `Radio` or `Share2`.
- **Settings leaves the rail** — it lives only in the top bar, so there aren't
  two entry points to the same page.
- Active state: 2px accent bar flush to the rail's left edge, accent-tinted icon,
  subtle neutral background on the item. Hover: neutral background only.
- Hover label: shadcn `Tooltip`, `side="right"`, ~300ms delay, sentence-case
  label. Every icon button also carries an `aria-label` — the tooltip is not an
  accessibility substitute.
- The rail does not expand or collapse. It is always the icon rail.

### Top bar

- 48px tall, 1px bottom border, same background as the content region.
- **Left**: breadcrumb only. Section › page, e.g. `Notebooks › demo.nb.md` with
  the environment `Badge` (`dev`) inline after the leaf. Middle-truncate long
  file names; full value in the `title` attribute.
- **Right**: theme picker button, then settings gear. Both 32px ghost icon
  buttons with tooltips.
- **The API key field moves out of the top bar into the Settings page.** It's
  configuration, not navigation, and it's the busiest thing in the current
  header.
- Page-level actions (`Saved`, `Promote to production`, `Run All`,
  `Restart kernel`) stay inside the page, not in the top bar. The top bar tells
  you where you are; the page tells you what you can do here. They all live on a
  single page toolbar — see **Notebook page toolbar** below.

### Content region

- Full width, 24px horizontal padding, 16px top padding. Remove the centered
  max-width wrapper.
- Notebook cells span the full content width.
- **Exception**: rendered markdown prose inside markdown cells and doc-like
  pages caps at ~78ch and is left-aligned. Full-bleed body text at 1600px is
  unreadable. Code cells, tables, and outputs stay full width.

### Notebook page toolbar

Today the notebook page spends three rows on chrome: a header row with
`Saved` / `Promote to production`, a tab row, and a controls row. **Collapse
these into one toolbar row.** Tabs sit on the left, a flexible spacer takes the
middle, and every action right-aligns.

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Notebook │ Source │ Diff vs production                                       │
│                    ⟵ flex spacer ⟶   [0.10.0.0 ·idle] [Normal│Focus] │ ▶ Run │
│                                       All  ↻ Restart │ Saved  Promote to prod│
└──────────────────────────────────────────────────────────────────────────────┘
```

(One physical row — wrapped above only to fit this page.)

- The row is a flex container: `Tabs` list, then `flex-1` spacer, then the action
  cluster. Single row height ~40px, 1px bottom border, tabs' underline indicator
  sitting on that border.
- **Right-side order, left to right**, with `Separator` (vertical) between groups:
  1. Kernel status `Badge` — non-interactive, so it reads as information before
     you reach the controls
  2. `Normal | Focus` `ToggleGroup`
  3. — separator —
  4. `Run All` (`Play`), `Restart kernel` (`RotateCcw`) — outline buttons
  5. — separator —
  6. `Saved` (secondary; shows saved state and saves when dirty),
     `Promote to production` (primary — the only accent-filled button on the page)
- **Tab-dependent visibility**: the execution controls (kernel status,
  Normal/Focus, Run All, Restart kernel) belong to the Notebook tab and hide on
  Source and Diff. `Saved` and `Promote to production` are document-level and
  stay visible on all three tabs. Hide rather than disable — a disabled Run All
  on a diff view is noise.
- **Never wrap to a second row.** Degrade in this order as width shrinks:
  1. Below ~1400px: `Restart kernel` becomes icon-only (tooltip carries the
     label).
  2. Below ~1200px: `Run All` becomes icon-only, and the kernel status badge
     drops its version, keeping the status word and dot.
  3. Below ~1024px: execution controls collapse into a `DropdownMenu` behind a
     `MoreHorizontal` button. `Saved` and `Promote to production` never collapse.
- The toolbar is **sticky** to the top of the content region so `Run All` stays
  reachable while scrolling a long notebook in Normal Mode. In Focus Mode it's
  fixed above the panes and doesn't scroll at all.
- The `Not promotable yet` alert stays where it is, above the toolbar, unchanged
  except for token adoption.

## Theming

### Token layer

Use Tailwind + shadcn's CSS-variable convention. Define the standard token set
(`--background`, `--foreground`, `--card`, `--popover`, `--muted`,
`--muted-foreground`, `--border`, `--input`, `--ring`, `--primary`,
`--primary-foreground`, `--destructive`, `--radius`) plus app-specific ones:

- `--surface-rail` — left rail background
- `--status-idle`, `--status-running`, `--status-error`, `--status-warning`
- `--code-bg` — must match the Monaco `editor.background` exactly
- `--font-mono`

Nothing in the app may hardcode a color. All component styling reads tokens.

### Accent picker (5 options)

- The **neutral base is fixed** across all five themes. Only the accent changes.
  This is what keeps the app from looking like five different apps.
- Themes: **Blue** (default), **Violet**, **Emerald**, **Amber**, **Rose**.
- Implementation: `data-accent="blue|violet|emerald|amber|rose"` on `<html>`;
  each value overrides `--primary`, `--primary-foreground`, and `--ring` only.
- Trigger: icon button in the top bar (lucide `Palette` or `SwatchBook`) opening
  a `DropdownMenu` or `Popover` with a row of five color swatches; the active one
  is checked. Swatches carry accessible names, not just color.
- Persist to `localStorage`; apply before first paint (inline script in the
  document head) so there's no flash of the default accent.
- **Every accent must pass 4.5:1 contrast against `--primary-foreground`** on
  buttons. Amber in particular needs a dark foreground, not white — verify all
  five rather than assuming.

### Dark mode

Out of scope for this pass. Define the `.dark` token block so it's not painful
later, but ship **no dark-mode toggle** and don't QA dark. Light is the design
target.

## Component adoption (shadcn/ui)

Initialize with the shadcn CLI, CSS-variable mode, and use the generated `cn()`
helper. Map the existing UI onto components rather than restyling bespoke
markup:

| Current element | Component |
|---|---|
| Notebook / Source / Diff vs production | `Tabs` |
| Normal / Focus toggle | `ToggleGroup` (single) |
| Run All, Restart kernel, Promote | `Button` (`outline` / `default`, `size="sm"`) with lucide icons |
| Cell language selector (C#, HTTP, SQL, Markdown) | `Select` |
| "Not promotable yet" panel | `Alert` — add a `warning` variant; shadcn ships only `default` and `destructive` |
| `dev` chip, kernel version pill | `Badge` |
| Kernel status (`idle`) | `Badge` with a leading status dot driven by `--status-*` |
| API key field (in Settings) | `Input` + `Button` |
| Nav hover labels | `Tooltip` (single `TooltipProvider` at the root) |
| Theme picker | `DropdownMenu` or `Popover` |
| Save/run failures | `Sonner` toasts |

Icons: `lucide-react` only. Remove any other icon source. Default 16px in
controls, 20px in the rail.

## Monaco integration (read this — it will bite otherwise)

1. **Monaco cannot read CSS variables.** Its themes take literal hex values via
   `monaco.editor.defineTheme`. Define one app theme (`clrkernel-light`) whose
   `editor.background` is the literal value of `--code-bg`, and keep a single
   source of truth — a TS constants module that both the CSS token file and the
   Monaco theme definition are generated from or import. Two hand-maintained
   copies of the palette will drift.
2. `monaco.editor.defineTheme` / `setTheme` are **global**, not per-editor. With
   one editor instance (per the Focus Mode work) this is fine, but don't try to
   theme editors independently.
3. Accent changes don't need a Monaco re-theme — the accent doesn't appear in
   the editor. Only cursor and selection colors would; leave them neutral.
4. **The editor background must match the surrounding card exactly**, or a seam
   appears at the editor's edge. Verify at 100% and 125% zoom.
5. Set Monaco's `fontFamily` from the same `--font-mono` stack and
   `fontSize: 13` to match the compact density. Also set `padding: { top: 8 }`
   so the first line isn't flush to the border.
6. After the shell changes the editor's container size, `editor.layout()` still
   has to run — the rail and top bar changing height/width counts.

## Migration order

Do this in reviewable steps, not one commit:

1. Tailwind + shadcn init, token layer, font stacks. No layout change yet;
   confirm the app still renders.
2. Build the shell (rail, top bar, content region) with routing unchanged. Old
   page internals still render inside the new shell.
3. Replace primitives page by page — buttons, tabs, selects, badges, alerts.
   Notebook page last, since it's the most complex.
4. Theme picker + persistence + Monaco theme wiring.
5. Delete the dead CSS from the old top nav and the centered container.

## Non-goals

- No changes to Normal/Focus mode behavior, cell execution, or the notebook
  document model.
- No bottom status bar (kernel status stays in the notebook toolbar).
- No dark mode toggle.
- No rail expand/collapse.
- No new pages or nav destinations.
- No responsive/mobile layout work — desktop widths only, but don't let anything
  break below ~1024px.

## Acceptance criteria

- [x] Left rail is fixed at 48px with Dashboard, Jobs, Notebooks, Channels;
      hovering an icon shows its label; the active route is visibly indicated.
      → measured 48px; tooltip on hover, 2px accent bar on the active route
- [x] Every rail and top-bar icon button has an `aria-label` and a visible
      keyboard focus ring.
      → all five rail links labelled; focus ring visible on Tab
- [x] Top bar contains only the breadcrumb, theme picker, and settings gear.
      → breadcrumb + accent picker + settings; zero inputs in the header
- [x] The API key field is on the Settings page and works there.
      → under *Browser*; stored in the browser, sent as X-Api-Key
- [x] The notebook page has one toolbar row: tabs left, actions right, no
      separate header or controls row.
      → one row measured — every child shares a top offset
- [x] The toolbar never wraps to a second line down to 1024px, degrading through
      the documented steps instead.
      → one row at 1600/1399/1199/1023, degrading through the documented steps
- [x] Execution controls are hidden on the Source and Diff tabs; Saved and
      Promote remain on all tabs.
      → absent on both; Save and Promote present on all three tabs
- [x] `Promote to production` is the only accent-filled button on the page.
      → computed backgrounds scanned: only Promote, plus the rail's product mark
- [x] Notebook cells span the full content width with consistent padding; there
      is no centered max-width wrapper.
      → cell 1404px of a 1452px content region; no centered wrapper found
- [x] Rail and top bar stay fixed; only the content region scrolls; there is no
      page-level scrollbar.
      → documentElement never scrolls; the content region is the scroll container
- [x] Focus Mode still fills the viewport correctly inside the new shell, and
      Monaco lays out with no click-target offset after the shell change.
      → work area 764px, ending 13px above the container bottom; cells run, click target unshifted
- [x] Theme picker offers 5 accents, applies instantly, survives reload, and
      shows no flash of the wrong accent on load.
      → all five measured; survives reload with the attribute set at first evaluation
- [x] All five accents pass 4.5:1 contrast on primary buttons.
      → blue 5.17, violet 5.70, emerald 5.48, amber 7.69, rose 6.29 — asserted in palette.test.ts
- [x] No hardcoded colors remain outside the token definitions and the Monaco
      theme constants.
      → swept; guarded by noLiterals.test.ts, verified to fail on an injected literal
- [x] Monaco's background is seamless with its container.
      → editor, container and --code-bg all #ffffff

## Decisions I made for you — say so if you disagree

1. Settings lives only in the top bar, not in the rail.
2. The API key input moves to Settings.
3. Page actions (Run All, Restart kernel, Saved, Promote) stay in the page, on a
   single toolbar row shared with the tabs.
3b. The kernel status badge and the Normal/Focus toggle join that same right-hand
   cluster, and the execution controls hide on the Source and Diff tabs.
4. The five themes vary the accent only, on a shared neutral base.
5. Light mode only for now.
6. Rendered markdown prose is width-capped even though cells are full width.