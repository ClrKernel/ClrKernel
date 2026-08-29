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

/**
 * How far the previews are zoomed out, as a fraction of the sidebar's width.
 *
 * 1 fills the panel; below that you trade size for how many fit on screen at
 * once, which is what the zoom is for — finding a cell in a long notebook by
 * scanning rather than scrolling.
 */
export const MIN_ZOOM = 0.5;
export const MAX_ZOOM = 1;
export const DEFAULT_ZOOM = 1;

export function clampZoom(zoom: number): number {
  return Number.isFinite(zoom) ? Math.min(Math.max(zoom, MIN_ZOOM), MAX_ZOOM) : DEFAULT_ZOOM;
}

/** The box a thumbnail occupies at a given sidebar width and zoom. */
export function thumbnailBox(
  sidebarWidth: number, zoom: number = DEFAULT_ZOOM,
): { width: number; height: number } {
  const available = Math.max(sidebarWidth - GUTTER, MIN_THUMBNAIL_WIDTH - GUTTER);
  const width = Math.round(available * clampZoom(zoom));
  return { width, height: Math.round(width / ASPECT) };
}

/**
 * The scale the code is drawn at, which moves with the zoom.
 *
 * Both together, so zooming out shows the *same lines, smaller* rather than
 * fewer lines at the same size. That is what a zoom means everywhere else, and
 * it is what keeps a zoomed-out column recognisable: the shapes are the shapes
 * you already learned, just further away.
 */
export function codeScale(zoom: number = DEFAULT_ZOOM): number {
  return SCALE * clampZoom(zoom);
}

/**
 * How far to shrink the code to fit.
 *
 * Not "fit the whole cell". A thumbnail is for recognising a shape, not for
 * reading — at this size the text is a few pixels tall whatever you do — so it
 * renders at one scale for the whole column and clips. Scaling each cell to fit
 * would make a long one's text microscopic and a two-line one's enormous, and
 * the column would stop reading as a column. The zoom moves this for every
 * thumbnail at once, which is a different thing.
 */
export const SCALE = 0.42;

/** Monaco's line height at the cell font size, before scaling. */
const LINE_HEIGHT = 18;

/**
 * How many lines can appear in the box, plus one so the clip lands mid-line —
 * a preview that ends exactly on a boundary looks like the cell ends there.
 */
export function visibleLines(sidebarWidth: number, zoom: number = DEFAULT_ZOOM): number {
  return Math.ceil(
    thumbnailBox(sidebarWidth, zoom).height / (LINE_HEIGHT * codeScale(zoom))) + 1;
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
