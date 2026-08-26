/**
 * The Contents sidebar's second view: a column of small syntax-coloured previews
 * of each cell, in document order.
 *
 * React-free and Monaco-free, like `notebook.ts` and `toc.ts` — this is the part
 * with the arithmetic and the decisions in it, and it should be checkable by
 * calling a function rather than by looking at a sidebar.
 */

/** A thumbnail's aspect ratio, from the spec. Fixed, and clipped rather than
 *  squashed: a preview that changed shape per cell would be unreadable as a
 *  column, and one that squashed its text would be unrecognisable. */
export const ASPECT = 4 / 3;

/**
 * How wide the sidebar has to be before a thumbnail is worth drawing.
 *
 * The outline is happy at 180px. A 4:3 preview at that width is 135px tall and
 * shows about four lines of anything, which is a grey smudge rather than a
 * recognisable shape — so switching to thumbnails widens a narrower sidebar
 * instead of rendering something useless.
 */
export const MIN_THUMBNAIL_WIDTH = 220;

/** Padding inside the sidebar, so a thumbnail is not flush to both edges. */
const GUTTER = 20;

/** The box a thumbnail occupies at a given sidebar width. */
export function thumbnailBox(sidebarWidth: number): { width: number; height: number } {
  const width = Math.max(sidebarWidth - GUTTER, MIN_THUMBNAIL_WIDTH - GUTTER);
  return { width, height: Math.round(width / ASPECT) };
}

/**
 * How far to shrink the code to fit.
 *
 * Not "fit the whole cell". A thumbnail is for recognising a shape, not for
 * reading — at this size the text is a few pixels tall whatever you do — so it
 * renders at a fixed small scale and clips, which keeps every thumbnail's text
 * the same size. Scaling to fit would make a long cell's text microscopic and a
 * two-line cell's enormous, and the column would stop reading as a column.
 */
export const SCALE = 0.42;

/** Monaco's line height at the cell font size, before scaling. */
const LINE_HEIGHT = 18;

/**
 * How many lines can appear in the box, plus one so the clip lands mid-line —
 * a preview that ends exactly on a boundary looks like the cell ends there.
 */
export function visibleLines(sidebarWidth: number): number {
  return Math.ceil(thumbnailBox(sidebarWidth).height / (LINE_HEIGHT * SCALE)) + 1;
}

/**
 * The text to colorize: the first N lines, and never the whole cell.
 *
 * Colorizing 2,000 lines to show 12 of them is the difference between a sidebar
 * that opens instantly and one that hangs on a long notebook. Leading blank
 * lines are dropped because they say nothing about a cell's shape, and trailing
 * ones because they would render as empty space below content that is there.
 */
export function previewSource(source: string, lines: number): string {
  const kept: string[] = [];
  for (const line of (source ?? '').replace(/\r\n/g, '\n').split('\n')) {
    if (kept.length >= lines) {
      break;
    }
    if (kept.length === 0 && line.trim().length === 0) {
      continue;
    }
    kept.push(line);
  }
  while (kept.length > 0 && kept[kept.length - 1].trim().length === 0) {
    kept.pop();
  }
  return kept.join('\n');
}

/**
 * The cache key for a colorized preview.
 *
 * Text and language, and deliberately **not** the theme. `monaco.editor.colorize`
 * returns HTML whose colours come from Monaco's global `.mtkN` stylesheet rather
 * than from inline styles, and `setTheme` rewrites that stylesheet — so cached
 * HTML recolors itself when the theme changes. The spec assumed the colours were
 * baked into the HTML and that a theme change had to bust the cache; measured
 * against this build, they are not and it does not.
 *
 * Both themes are built by one function from one rule list, which is what makes
 * `.mtk4` mean the same thing in each. A theme with a different rule set would
 * shift those indices, and this is the warning for whoever adds one.
 */
export function previewKey(cellId: string, language: string, source: string): string {
  return `${cellId} ${language} ${source}`;
}
