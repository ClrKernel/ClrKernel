# Feature: Thumbnail view for the Focus Mode Contents panel

## Summary

The Contents sidebar in Focus Mode gains a second view. A small toggle next to the
collapse chevron switches between:

- **Outline** — today's tree: sections from markdown headings, cells as leaves.
- **Thumbnails** — a single vertical column of 4:3 previews of each cell's code,
  scrollable, in document order.

Both views also gain a **language indicator** per cell, so a notebook mixing C#,
SQL, HTTP, and markdown is scannable at a glance.

## Toggle

- Two-icon toggle in the `CONTENTS` header, immediately left of the collapse
  chevron. Suggested lucide pair: `List` and `GalleryVertical`.
- The choice persists alongside the sidebar width and collapsed state.
- Switching views preserves the active cell and scrolls it into view — you should
  land on the same cell you were looking at.

## How to render the thumbnails

This is the decision that determines whether the feature is pleasant or unusable.

**Do not instantiate a Monaco editor per thumbnail.** Twenty editors in a sidebar
will make scrolling miserable and burn memory for no benefit.

Use **`monaco.editor.colorize(text, languageId, options)`**, which returns
theme-consistent colorized HTML without creating an editor. Render that HTML into
the thumbnail and scale it with a CSS `transform: scale()` and
`transform-origin: top left`. You get real syntax colors — the thing that makes a
thumbnail recognizable — at a fraction of the cost.

Details that follow from that:

- **Thumbnails are for shape recognition, not reading.** At 4:3 in a ~260px
  sidebar the text is a few pixels tall. Don't try to fit the whole cell — render
  the first N lines, clip at the bottom with a short fade, and let the silhouette
  plus the language indicator do the work.
- **Clip to maintain aspect ratio, never squash.** Fixed 4:3 box, `overflow:
  hidden`.
- **Virtualize the list.** Render only visible thumbnails plus a small buffer.
  `colorize` is cheap, but a 200-cell notebook isn't.
- **Cache the colorized HTML per cell**, invalidated on cell text change (debounce
  it to the autosave cadence rather than per keystroke) and **on theme or accent
  change** — the colors are baked into the returned HTML, so a theme switch must
  bust the cache or every thumbnail keeps the old palette.

## Language indicator

Add to **both** views. Same source, same treatment.

The language identity should come from the `ICellLanguage` metadata, not from a
lookup table in the web app — that's the same rule the SQL dialect work
established, and it means a new dialect or language shows up correctly in the
sidebar with no front-end change. Add an **icon key and color key** to that
metadata.

On presentation, one decision to make:

- **A small monogram chip** — `C#`, `SQL`, `HTTP`, `MD` — tinted per language.
  Costs nothing, respects the redesign brief's "lucide icons only" rule, and
  stays legible at small sizes.
- **Real file-type icons** (Seti, vscode-icons) look more like VS Code but mean
  adding an icon set dependency, and the brief deliberately narrowed to lucide.

Recommend the monogram chip. Either way, T-SQL and Oracle SQL should be
distinguishable — that's much of the point of splitting the dialects.

## Parity between the two views

Whatever the outline shows, the thumbnail view must show too, or switching costs
you information:

- Execution count / `[ ]` placeholder
- Running spinner, success, error state
- Active cell highlight
- Section grouping — keep it as small sticky headers above the thumbnails in that
  section, so heading structure isn't lost in the flat list

Markdown cells get thumbnails like everything else, rendered from their source for
consistency. (Rendering the prose instead would be more recognizable but means two
rendering paths in one list; not worth it in v1.)

## Sidebar width

Thumbnails need more room than a text outline. The outline's ~180px minimum makes
a 4:3 preview too small to read as anything.

- Enforce a higher minimum width in thumbnail mode (~220px).
- If the sidebar is narrower than that when the user switches to thumbnails,
  widen it to the minimum rather than rendering something useless.

## Keyboard and accessibility

- Same navigation as the outline: ↑/↓ to move, Enter to activate, and the list is
  reachable by Tab.
- The thumbnail image is decorative. Each item's accessible name is the cell label
  and language — the same string the outline uses — not "thumbnail".

## Later

- Thumbnail of the cell's **output** rather than its code, as a third view or a
  hover flip. Genuinely useful for finding "the cell that produced the chart", but
  it needs output snapshots that don't exist yet.
- Zoom slider for thumbnail density.
- Drag to reorder cells from either view.

## Acceptance criteria

- [ ] Toggle switches Contents between outline and thumbnails; the choice persists
      and the active cell is preserved across the switch.
- [ ] Thumbnails are syntax-colored, fixed 4:3, clipped rather than squashed, in
      document order.
- [ ] No Monaco editor instance is created for any thumbnail.
- [ ] A 200-cell notebook scrolls the thumbnail list smoothly.
- [ ] Changing the theme or accent recolors the thumbnails.
- [ ] Both views show a language indicator sourced from `ICellLanguage` metadata,
      with T-SQL and Oracle SQL distinguishable.
- [ ] Run state, execution count, and active highlight appear in both views.
- [ ] Switching to thumbnails widens a too-narrow sidebar to the usable minimum.