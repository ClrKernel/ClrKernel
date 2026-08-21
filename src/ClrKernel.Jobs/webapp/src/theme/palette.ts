/**
 * The palette, in one place — the "Warm Paper" design system.
 *
 * Monaco's theme API takes literal hex — it cannot read a CSS variable — so the
 * editor's colours have to exist in TypeScript. Rather than keep a second
 * hand-maintained copy of the palette, this module is the source and
 * `theme/tokens.css` mirrors it; `palette.test.ts` parses that CSS and fails if
 * the two ever drift. Hex, not oklch, for the same reason: Monaco parses hex.
 *
 * Accents are the only thing a theme changes. The neutral base is fixed across
 * all five, which is what stops the app looking like five different apps.
 */

/** Neutrals — identical under every accent. */
export const NEUTRAL = {
  /**
   * Warm Paper inverts the usual light theme: the canvas is a warm cream and
   * *cards sit above it*, lighter, not the other way round. Hierarchy is
   * carried by borders — there is no card shadow anywhere in this design.
   */
  background: '#faf9f6',
  foreground: '#22251f',
  /** Cards, the rail, the header, inputs — the lightest surface. */
  card: '#fffefb',
  popover: '#fffefb',
  /** Panels a step *below* the canvas: cell gutters and footers, `pre` blocks,
   *  the explorer and Focus mode's contents sidebar. */
  panel: '#f5f3ee',
  /** One step below that again: the explorer's hover and active row. */
  panelStrong: '#eae7de',
  /** Body-secondary text. `mutedSubtle` is the fainter of the two — labels,
   *  hints, chip text, the things that must not compete with content. */
  mutedForeground: '#6f6b60',
  mutedSubtle: '#8a8577',
  /** Region borders. Table row rules are the fainter `borderSubtle`. */
  border: '#e6e2d8',
  borderSubtle: '#efece4',
  /** Control borders sit a shade darker than region borders, so an input reads
   *  as something you can click rather than as a panel edge. */
  input: '#ded9cd',
  /** Row and item hover across tables, menus and trees. */
  hover: '#f3f1ea',
  destructive: '#b91c1c',
} as const;

/** Tinted row backgrounds for a run's cells — the failure tint in particular is
 *  a design value, not a computed one, so it lives here rather than as a
 *  color-mix nobody can check. */
/** The interactive grid's own chrome. Rendered by ClrKernel.Formatting.Html, so
 *  it reaches the page only through the --vscode-* variables SandboxedHtml
 *  passes into the frame; these are the values behind those variables. */
export const GRID = {
  header: '#f0ede4',
} as const;

export const ROW = {
  failed: '#fdf1ef',
  failedBorder: '#ecc9c5',
} as const;

export const STATUS = {
  idle: '#b6b0a2',
  running: '#1d4ed8',
  error: '#b91c1c',
  warning: '#b45309',
  success: '#15803d',
} as const;

/**
 * Environment chips. Tinted rather than outlined, and deliberately *not*
 * accent-derived: `prod` is green because production is production, whichever
 * accent the user picked — the same rule the ANSI palette follows.
 */
export const ENV = {
  dev: { fg: '#b45309', bg: '#fdf3e3', border: '#f0dcbb' },
  prod: { fg: '#0e6e43', bg: '#e9f3ec', border: '#cfe4d6' },
} as const;

/**
 * Monaco's colours. `background` must equal the surrounding card exactly or a
 * seam shows at the editor's edge. Cursor and selection stay neutral on
 * purpose — the accent never appears inside the editor, so an accent change
 * needs no re-theme.
 */
export const EDITOR = {
  background: NEUTRAL.card,
  foreground: '#3d4038',
  lineHighlight: '#f5f3ee',
  lineNumber: '#c2bcae',
  indentGuide: '#eae7de',
  selection: '#e0e9e2',
  widgetBackground: NEUTRAL.popover,
  widgetBorder: NEUTRAL.border,
  /** Syntax, from the handoff. Four hues, chosen to sit on cream. */
  keyword: '#1d4ed8',
  string: '#b45309',
  number: '#0e6e43',
  directive: '#b91c1c',
  comment: '#8a8577',
} as const;

export type AccentName = 'green' | 'blue' | 'violet' | 'amber' | 'rose';

export interface Accent {
  name: AccentName;
  label: string;
  /** Fill for primary buttons, the active nav item, links and focus rings. */
  primary: string;
  /** The hover/pressed shade of `primary`. */
  primaryHover: string;
  /** A tint of `primary` used as a selected-surface fill, never as text. */
  primarySoft: string;
  /**
   * Text on top of `primary`. Amber is the reason this is per-accent rather
   * than always white: a mid-amber fill under white text is nowhere near
   * 4.5:1, so it takes the dark foreground instead.
   */
  primaryForeground: string;
}

/** Order is the order the swatches appear in the picker. Green is the brand. */
export const ACCENTS: readonly Accent[] = [
  {
    name: 'green',
    label: 'Green',
    primary: '#0e6e43',
    primaryHover: '#0a5636',
    primarySoft: '#eef3ee',
    primaryForeground: '#ffffff',
  },
  {
    name: 'blue',
    label: 'Blue',
    primary: '#1d4ed8',
    primaryHover: '#1739a8',
    primarySoft: '#edf1fb',
    primaryForeground: '#ffffff',
  },
  {
    name: 'violet',
    label: 'Violet',
    primary: '#6d28d9',
    primaryHover: '#571fad',
    primarySoft: '#f2eefc',
    primaryForeground: '#ffffff',
  },
  {
    name: 'amber',
    label: 'Amber',
    primary: '#b45309',
    primaryHover: '#8f4107',
    primarySoft: '#fdf3e3',
    primaryForeground: '#ffffff',
  },
  {
    name: 'rose',
    label: 'Rose',
    primary: '#be123c',
    primaryHover: '#980e30',
    primarySoft: '#fdeef1',
    primaryForeground: '#ffffff',
  },
] as const;

export const DEFAULT_ACCENT: AccentName = 'green';

export function isAccentName(value: unknown): value is AccentName {
  return ACCENTS.some((accent) => accent.name === value);
}

/** The mono stack, shared by the CSS `--font-mono` token and Monaco. */
export const FONT_MONO =
  "'JetBrains Mono', ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, monospace";

/** The UI stack, shared by the CSS `--font-sans` token. */
export const FONT_SANS =
  "'Instrument Sans', -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif";
