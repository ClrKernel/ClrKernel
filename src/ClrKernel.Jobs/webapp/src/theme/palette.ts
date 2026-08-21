/**
 * The palette, in one place.
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

/** Neutrals and status colours — identical under every accent. */
export const NEUTRAL = {
  /**
   * The content region. Deliberately a real grey, not a near-white: with only a
   * few percent between page, card and border the whole app reads as one flat
   * surface and nothing separates from anything.
   */
  background: '#e9ecf1',
  foreground: '#1c1f24',
  /** Cards, editors, panels — pure white on top of the app background. */
  card: '#ffffff',
  popover: '#ffffff',
  /** Subtle fills *on a card*: hover states, table headers, markdown cells.
   *  Lighter than the page, so a filled cell still reads as raised. */
  muted: '#f1f3f6',
  mutedForeground: '#5b6472',
  border: '#cbd2da',
  destructive: '#b91c1c',
  /** The rail is the darkest surface, so the chrome frames the content. */
  surfaceRail: '#dbe0e7',
} as const;

export const STATUS = {
  idle: '#6b7280',
  running: '#1d4ed8',
  error: '#b91c1c',
  warning: '#b45309',
  success: '#15803d',
} as const;

/**
 * Monaco's colours. `background` must equal the surrounding card exactly or a
 * seam shows at the editor's edge. Cursor and selection stay neutral on
 * purpose — the accent never appears inside the editor, so an accent change
 * needs no re-theme.
 */
export const EDITOR = {
  background: NEUTRAL.card,
  lineHighlight: '#f4f6f8',
  lineNumber: '#9aa3b0',
  indentGuide: '#e8ebef',
  selection: '#d7dde5',
  widgetBackground: NEUTRAL.popover,
  widgetBorder: NEUTRAL.border,
} as const;

export type AccentName = 'blue' | 'violet' | 'emerald' | 'amber' | 'rose';

export interface Accent {
  name: AccentName;
  label: string;
  /** Fill for primary buttons, the active nav bar, links and focus rings. */
  primary: string;
  /**
   * Text on top of `primary`. Amber is the reason this is per-accent rather
   * than always white: a mid-amber fill under white text is nowhere near
   * 4.5:1, so it takes the dark foreground instead.
   */
  primaryForeground: string;
}

/** Order is the order the swatches appear in the picker. Blue is the default. */
export const ACCENTS: readonly Accent[] = [
  { name: 'blue', label: 'Blue', primary: '#2563eb', primaryForeground: '#ffffff' },
  { name: 'violet', label: 'Violet', primary: '#7c3aed', primaryForeground: '#ffffff' },
  { name: 'emerald', label: 'Emerald', primary: '#047857', primaryForeground: '#ffffff' },
  { name: 'amber', label: 'Amber', primary: '#f59e0b', primaryForeground: '#1c1f24' },
  { name: 'rose', label: 'Rose', primary: '#be123c', primaryForeground: '#ffffff' },
] as const;

export const DEFAULT_ACCENT: AccentName = 'blue';

export function isAccentName(value: unknown): value is AccentName {
  return ACCENTS.some((accent) => accent.name === value);
}

/** The mono stack, shared by the CSS `--font-mono` token and Monaco. */
export const FONT_MONO =
  "ui-monospace, SFMono-Regular, 'SF Mono', Menlo, Consolas, 'Liberation Mono', monospace";
