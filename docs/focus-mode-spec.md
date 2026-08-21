# Feature: Notebook Focus Mode (ClrKernel.Jobs web UI) when editing a notebook

## Summary

Add a second viewing mode to the notebook UI view. Today the notebook renders as a
vertical list of cells ("Normal Mode"). Focus Mode instead devotes the whole
work area to **one cell at a time**: its editor on top, its output below, each
pane independently scrollable and the divider between them draggable — the SSMS
query/results layout. A collapsible, horizontally-resizable table-of-contents
sidebar on the left lists the notebook's sections and cells and is how you move
between cells.

Normal Mode stays exactly as it is. Do not restyle it.

## Layout

```
┌───────────────────────────────────────────────────────────────────────┐
│  [Notebook toolbar]                        [ Normal | ●Focus ] toggle │
├──────────────────┬────────────────────────────────────────────────────┤
│ CONTENTS      ⟨⟩ │  Cell [3]  ▸Run  ⏹Stop            (cell toolbar)   │
│                  ├────────────────────────────────────────────────────┤
│ ▾ Setup          │                                                    │
│    ▪ [1] using…  │   Monaco editor for the ACTIVE cell only           │
│    ▪ [2] conn…   │   own scrollbar                                    │
│ ▾ Extract        │                                                    │
│    ▪ [3] var df  │≡≡≡≡≡≡≡≡≡ draggable horizontal splitter ≡≡≡≡≡≡≡≡≡≡≡≡│
│    ▪ [4] df.He…  │                                                    │
│ ▸ Load           │   Output / results for the ACTIVE cell             │
│ ▸ Notes          │   own scrollbar                                    │
│                  │                                                    │
└──────────────────┴────────────────────────────────────────────────────┘
        ↕ draggable vertical splitter (sidebar width)
```

- **Sidebar**: collapsible to zero width, with a persistent reveal affordance
  (a thin rail or a ⟨⟩ button in the toolbar) so it can be brought back.
  Horizontally resizable by dragging its right edge. Min ~180px, max ~40% of
  the work area.
- **Editor pane / output pane**: vertically stacked, separated by a draggable
  splitter. Min height ~80px each. Double-clicking the splitter resets to 50/50.
- Both panes scroll independently. Neither pane's scroll should ever move the
  other, and the page itself should not scroll in Focus Mode — the work area is
  fixed to the viewport height.

## Mode toggle

- A toggle in the notebook toolbar (segmented control or button) switches
  Normal ⇄ Focus. **This button is the only way to leave Focus Mode.**
- **`Esc` must not exit Focus Mode.** `Esc` keeps whatever meaning it already
  has inside Monaco (dismiss suggest widget, exit find, etc.). Explicitly do not
  add a global `Esc` handler for this feature.
- Switching Normal → Focus: focus the cell that was last active / nearest the
  top of the viewport.
- Switching Focus → Normal: scroll the list so the focused cell is in view and
  keep it selected. Round-tripping should not lose your place.

## Table of contents

Model it on GitHub's repo file tree, but sourced from a single notebook:

- **Sections = markdown headings.** A markdown cell containing `#`/`##`/`###`
  creates a collapsible node; nesting follows heading level.
- **Leaves = cells.** Every cell (code and markdown) appears under the nearest
  preceding heading. Cells before any heading go in an implicit root group.
- Chevron expand/collapse per section; expansion state is UI-only, never
  written back to the `.ipynb`.
- Leaf labels: code cells → execution count badge (`[3]`, or `[ ]` if unrun) plus
  the first non-empty, non-comment line, truncated with ellipsis and a `title`
  tooltip carrying the full line. Markdown cells → the heading text, or the
  first line if headingless.
- Per-leaf status affordance: idle / queued / running (spinner) / ok / error.
  A cell that errored should be visibly findable from the TOC without clicking
  through cells.
- The active cell is highlighted. Clicking a leaf makes it active. Clicking a
  section header expands/collapses only — it does not change the active cell
  (unless the section header cell is itself the leaf clicked).
- Keyboard: ↑/↓ move the TOC selection when the sidebar has focus; Enter
  activates. The tree should be reachable by Tab and have sane ARIA
  (`role="tree"` / `treeitem` / `aria-expanded` / `aria-selected`).

## Editing and execution

- Run the active cell with the existing keybindings (`Ctrl/Cmd+Enter` run in
  place; `Shift+Enter` run and advance to the next cell in TOC order — in Focus
  Mode "advance" means make that cell active, not scroll).
- `Alt+↑` / `Alt+↓` (or `Ctrl+PageUp/PageDown`) move to previous/next cell
  without running.
- Edits in Focus Mode write to the same notebook document model as Normal Mode.
  There is no separate buffer and no save step unique to this mode.
- Cells that are not active keep running and keep receiving output in the
  background. Switching away from a running cell must not cancel it, and
  switching back must show the output produced while you were away.

## Monaco specifics (please read — these are the usual bugs)

1. **Reuse one editor instance; swap models.** Create a single Monaco editor for
   the Focus Mode editor pane and call `editor.setModel(cellModel)` when the
   active cell changes. Do not create/dispose an editor per cell switch — it's
   slow and it throws away per-cell undo history. Keep an `ITextModel` per cell
   (they likely already exist for Normal Mode; share them, don't duplicate, or
   edits will diverge between modes).
2. **Layout on resize.** Monaco does not reflow on container resize by itself.
   Either set `automaticLayout: true` or call `editor.layout()` from a
   `ResizeObserver` on the pane. Call it on: splitter drag (throttled to
   animation frames), sidebar resize/collapse, window resize, and on entering
   Focus Mode after the container has its final size. A common symptom of
   getting this wrong is a correctly-sized editor whose click targets are offset
   from the visible text.
3. Preserve per-cell view state (cursor, selection, scroll, folds) with
   `editor.saveViewState()` before switching away and `restoreViewState()` after
   `setModel`.
4. Keep the editor pane's own scrollbar as Monaco's; don't wrap it in an
   `overflow: auto` div, or you get nested scrollbars.

## Output pane

- Reuse the existing output renderer component from Normal Mode. Do not fork it
  — same renderers for stdout/stderr, tables, errors, rich mime types.
- Streaming output appends live. Stick-to-bottom while streaming, but disengage
  the moment the user scrolls up; re-engage if they scroll back to the bottom.
- Empty state when the cell has never been run: something quiet, e.g. "No output
  — run this cell to see results."
- Long output must not push the editor pane; the splitter position is the only
  thing that sizes these panes.

## Persistence

Persist in `localStorage` (or wherever existing UI prefs live), keyed globally,
not per notebook:

- sidebar width, sidebar collapsed state
- editor/output split ratio

Persist per notebook: last mode used, last active cell. Nothing here goes into
the `.ipynb`.

## Edge cases

- Notebook with zero cells: Focus Mode renders an empty state, TOC empty, no crash.
- Active cell deleted: fall back to the next cell, or previous if it was last.
- Cell added while in Focus Mode: appears in the TOC immediately.
- Very deep heading nesting (H4+): flatten past H3 rather than growing indentation.
- Markdown cell as the active cell: editor pane shows the markdown source; output
  pane shows the rendered preview.

## Non-goals for v1

- No drag-to-reorder cells in the TOC.
- No multi-cell selection.
- No pop-out/separate-window mode.
- No search/filter box in the TOC (nice later, out of scope now).
- No changes to Normal Mode's appearance or behavior.

## Acceptance criteria

- [ ] Toolbar toggle switches between Normal and Focus Mode; state survives
      switching notebooks and reloads.
- [ ] `Esc` does not exit Focus Mode.
- [ ] In Focus Mode exactly one cell's editor and output are shown, stacked.
- [ ] The splitter between editor and output is draggable; both panes scroll
      independently; double-click resets to 50/50.
- [ ] The sidebar collapses to zero width and can be reopened; its width is
      draggable and remembered.
- [ ] The TOC groups cells under markdown headings with working expand/collapse.
- [ ] Clicking a TOC entry switches the active cell; cursor position and scroll
      of the previously-active cell are restored when returning to it.
- [ ] Monaco resizes correctly after splitter drag, sidebar resize, and window
      resize, with no click-target offset.
- [ ] A cell started in Focus Mode keeps running when you switch cells, and its
      output is intact on return.
- [ ] Edits made in Focus Mode are present in Normal Mode and vice versa.

## Assumptions I made — correct these if wrong

1. Sections come from markdown headings (there's no other section concept in the
   notebook format to hang a tree off).
2. The output renderer is already a reusable component.
3. Cell text models already exist and are shared, rather than being created on
   demand when a cell scrolls into view in Normal Mode. If Normal Mode
   virtualizes cells, say so — it changes how models are managed.