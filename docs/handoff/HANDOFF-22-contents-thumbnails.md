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

## The zoom, and reordering (added the same week)

**Zoom** is a native `<input type="range">` in a slim row above the column, shown
only in the thumbnail view. The kit has no slider and this is the one control the
platform ships complete — keyboard steps, Home and End, the right role, a drag
that matches every other slider on the machine. Its own row rather than a fourth
item in the CONTENTS header, because at the 220px this view enforces, a title,
two toggles, a collapse button and a slider do not fit and the collapse button is
what loses.

Zooming moves the box *and* the code scale together, so it shows the same lines
smaller rather than fewer lines the same size. That is what a zoom means
everywhere else, and it keeps a zoomed-out column recognisable: the shapes are
the ones you already learned, further away. A test asserts the visible line count
is identical at 0.5, 0.7 and 1.

**Reorder** works by drag in both views and by Alt+↑/↓ in either. The arithmetic
is in `reorder.ts` because the awkward part is not the events: a cell is removed
before it is re-inserted, so dropping cell 2 after cell 5 is index 5 and not 6 —
the off-by-one every hand-rolled reorder has. A drop that changes nothing returns
the original index so it never becomes an undo entry; a Ctrl+Z that appears to do
nothing is worse than no undo.

HTML5 drag-and-drop rather than pointer events, for the drag image, the escape
key and the cursor the browser gives for free. Alt+↑/↓ exists because a drag is a
pointer gesture and nothing else — without it a notebook could not be reordered
from the keyboard at all.

### Two things worth knowing

`onDragOver` calls `preventDefault()` **unconditionally**. Gating it on the
"am I dragging" state looked tidier and was wrong: those props were built on the
render *before* dragstart set that state, so the first dragover saw null,
declined to allow the drop, and a short drag never got a second one. `onDrop`
reads the source index from the DataTransfer for the same reason — the browser
wrote it at dragstart and it cannot be a render behind.

**No automation driver delivers the whole gesture.** Playwright's `drag_to` fires
every event on the source element; a hand-rolled `mouse.down/move/up` produces
real `dragstart` and `dragover` but never a `drop`. Both measured with listeners
on the page. So the browser check does it in two halves: a real pointer, to prove
the markup is drag-enabled and that Chromium routes dragover across rows, then a
dispatched `drop` carrying a real `DataTransfer`, to prove the arithmetic.
Neither half alone would mean much; together they cover the path.

## Not done

Output thumbnails, the third thing the spec files under "Later". They would show
what a cell *produced* rather than what it says — genuinely the better way to
find "the one with the chart" — but outputs live in the session, are cleared by a
restart, and are never written to the file, so a freshly opened notebook would
show a column of empty boxes until you ran it. And the rich ones are sandboxed
iframes, which is exactly the per-cell cost this view exists to avoid. It becomes
worth building the day outputs are persisted; until then it is a view that is
blank when you most want it.

Markdown cells preview their source like everything else — rendering the prose
instead would be more recognisable and would mean two rendering paths in one
list.
