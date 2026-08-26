# HANDOFF-22 — Thumbnails in the Focus Mode contents panel

*Landed 2026-08-26. Spec: `docs/Focus-mode-thumbnails-spec.md`. Dark mode
(HANDOFF-21) came out of this spec and went first.*

## Two views, one panel

A segmented toggle in the CONTENTS header switches between the outline and a
column of 4:3 previews. The choice lives beside the sidebar's width and collapsed
flag, because it is the same kind of thing: how this panel is set up, everywhere.

Both views carry the language chip, the execution count, the run state and the
active highlight, because switching between them must not cost you information.
Sections survive in the thumbnail column as sticky headers — a flat column of
previews loses the heading structure entirely.

## The rendering decision

`monaco.editor.colorize`, not an editor per thumbnail. It runs the same tokenizer
and returns markup with no DOM, no model and no editor instance; the check asserts
that a six-cell notebook in thumbnail view has exactly **one** Monaco editor on the
page, the one showing the open cell.

Three things that follow:

- **Fixed scale, not fit-to-box.** Scaling each cell to fit would make a long
  one microscopic and a two-line one enormous, and the column would stop reading
  as a column. Every preview renders at 0.42 and clips, with the spec's fade at
  the bottom so a cut reads as a cut rather than as the end of the cell.
- **First N lines only.** Colorizing two thousand lines to show twelve is the
  difference between a sidebar that opens and one that hangs.
- **Colorize when near the viewport.** An IntersectionObserver with a 400px
  margin. Opening the 201-cell notebook colorizes 4; scrolling brings it to 65.

## The cache is not keyed on the theme

The spec says a theme change must bust it, because "the colors are baked into the
returned HTML". Measured against this build, they are not: `colorize` returns
spans carrying `.mtkN` classes, and Monaco writes the stylesheet behind those
classes when the theme is set — so **cached HTML recolors itself**. There is a
browser check that switches to dark with no reload and no forced re-render and
watches the previews change colour.

Both themes are built by one function from one rule list, which is what makes
`.mtk4` mean the same thing in each. A theme with a different rule set would
shift those indices; that warning is in `previewKey`.

## The chip

Letters, not an icon: the redesign narrowed to lucide, which has nothing for
"Oracle SQL", and at this size a glyph is a smudge while three letters stay
legible. `ICellLanguage.Monogram` supplies them, so a language plugged in at run
time gets a correct chip with no front-end change; C# and Markdown are named in
the web app because neither is a registered cell language, the same reason the
language picker prepends them.

The colour is one of six `--lang-*` tokens chosen by hashing the id. It only has
to be stable and help the eye group things — the letters identify the cell.
Deliberately not a colour from the kernel: the token layer is the only place a
colour is written down, and a chip carrying one would be the exception that ends
that rule.

## Two things found by looking

- **SQL string literals have always rendered pure red.** Monaco's base themes
  carry *language-specific* rules of their own — `string.sql` is `#FF0000`,
  `operator.sql` is slate — and a more specific rule beats a generic one, so the
  palette's amber never applied. Found by checking that a thumbnail matches the
  cell it previews: it did, which is how the cell turned out to be the thing that
  was wrong. Fixed in the theme builder.
- **A 201-cell notebook trips Monaco's listener-leak detector in Normal mode**,
  before any thumbnail exists — 200 cell editors, one per cell. Verified by
  opening that notebook with the contents panel never shown. Pre-existing, not
  this feature's, and not fixed here; the check filters it by name rather than
  ignoring every error. It is the argument for virtualising the *notebook* view,
  which is a separate piece of work.

## Not done

Everything the spec files under "Later": output thumbnails rather than code, a
zoom slider, and drag-to-reorder. Markdown cells preview their source like
everything else — rendering the prose instead would be more recognisable and
would mean two rendering paths in one list.
