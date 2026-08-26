import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import {
  ACCENTS, DARK_ACCENTS, DARK_EDITOR, DARK_ENV, DARK_FILE, DARK_GRID, DARK_NEUTRAL, DARK_ROW,
  DARK_STATUS, EDITOR, ENV, FILE, GRID, NEUTRAL, ROW, STATUS, accentsFor, isAccentName,
  paletteFor,
} from './palette';

// Read, not import: vitest stubs CSS imports to an empty string, so `?raw`
// would silently hand this test nothing to check.
const tokensCss = readFileSync(fileURLToPath(new URL('./tokens.css', import.meta.url)), 'utf8');

/** The value of `--name` inside the given selector's block. */
function token(selector: string, name: string): string {
  const block = tokensCss.split(selector)[1];
  expect(block, `no ${selector} block in tokens.css`).toBeDefined();
  const match = block.slice(0, block.indexOf('}')).match(new RegExp(`--${name}:\\s*([^;]+);`));
  expect(match, `no --${name} under ${selector}`).not.toBeNull();
  return match![1].trim();
}

function srgb(channel: number): number {
  const c = channel / 255;
  return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function luminance(hex: string): number {
  const n = parseInt(hex.replace('#', ''), 16);
  return (
    0.2126 * srgb((n >> 16) & 0xff) + 0.7152 * srgb((n >> 8) & 0xff) + 0.0722 * srgb(n & 0xff)
  );
}

export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

/**
 * The spec's "all five accents pass 4.5:1 on primary buttons" is not something
 * you can eyeball, and amber is the one that fails if you assume white text.
 */
describe('accent contrast', () => {
  it.each(ACCENTS)('$label reaches 4.5:1 on a primary button', (accent) => {
    expect(contrast(accent.primary, accent.primaryForeground)).toBeGreaterThanOrEqual(4.5);
  });

  it('has five accents with distinct names', () => {
    expect(new Set(ACCENTS.map((a) => a.name)).size).toBe(5);
  });

  it('recognises its own names and rejects anything else', () => {
    expect(ACCENTS.every((a) => isAccentName(a.name))).toBe(true);
    expect(isAccentName('chartreuse')).toBe(false);
  });
});

/**
 * Monaco takes literal hex and CSS takes tokens, so the palette necessarily
 * exists twice. This is what stops the two copies drifting — and `--code-bg`
 * in particular, because a mismatch there is a visible seam at every cell edge.
 */
describe('tokens.css mirrors palette.ts', () => {
  it.each([
    ['background', NEUTRAL.background],
    ['foreground', NEUTRAL.foreground],
    ['card', NEUTRAL.card],
    ['popover', NEUTRAL.popover],
    ['muted', NEUTRAL.panel],
    ['muted-foreground', NEUTRAL.mutedForeground],
    ['border', NEUTRAL.border],
    ['border-subtle', NEUTRAL.borderSubtle],
    ['input', NEUTRAL.input],
    ['accent', NEUTRAL.hover],
    ['muted-subtle', NEUTRAL.mutedSubtle],
    ['surface-panel', NEUTRAL.panel],
    ['surface-panel-strong', NEUTRAL.panelStrong],
    ['destructive', NEUTRAL.destructive],
    ['env-test', ENV.test.fg],
    ['env-test-bg', ENV.test.bg],
    ['env-test-border', ENV.test.border],
    ['env-prod', ENV.prod.fg],
    ['env-prod-bg', ENV.prod.bg],
    ['env-prod-border', ENV.prod.border],
    ['file-notebook', FILE.notebook],
    ['file-code', FILE.code],
    ['file-config', FILE.config],
    ['code-fg', EDITOR.foreground],
    ['line-number', EDITOR.lineNumber],
    ['syntax-keyword', EDITOR.keyword],
    ['syntax-string', EDITOR.string],
    ['syntax-number', EDITOR.number],
    ['syntax-directive', EDITOR.directive],
    ['grid-header', GRID.header],
    ['row-failed', ROW.failed],
    ['row-failed-border', ROW.failedBorder],
    ['status-idle', STATUS.idle],
    ['status-running', STATUS.running],
    ['status-error', STATUS.error],
    ['status-warning', STATUS.warning],
    ['status-success', STATUS.success],
  ])('--%s', (name, expected) => {
    expect(token(':root {', name)).toBe(expected);
  });

  it('--code-bg is exactly the editor background Monaco paints', () => {
    expect(token(':root {', 'code-bg')).toBe(EDITOR.background);
  });

  /**
   * Warm Paper inverts the usual light theme: the canvas is a warm cream and
   * cards sit *above* it, lighter. Panels — gutters, footers, the explorer —
   * step down from the canvas instead of up. Getting this ordering backwards
   * is exactly the "everything fades into everything else" failure, so it is
   * asserted rather than eyeballed.
   */
  it('stacks card above canvas above panel', () => {
    const surfaces = [NEUTRAL.card, NEUTRAL.background, NEUTRAL.panel, NEUTRAL.panelStrong];
    for (let i = 1; i < surfaces.length; i++) {
      expect(
        luminance(surfaces[i - 1]) - luminance(surfaces[i]),
        `${surfaces[i - 1]} should sit clearly above ${surfaces[i]}`,
      ).toBeGreaterThan(0.02);
    }
  });

  /**
   * Borders carry the hierarchy in this design, which only works if they are
   * actually visible on the lightest surface — and if the row rule is fainter
   * than the region border, or a table reads as a grid of boxes.
   */
  it('gives borders enough to carry the hierarchy', () => {
    expect(contrast(NEUTRAL.border, NEUTRAL.card)).toBeGreaterThan(1.2);
    expect(luminance(NEUTRAL.borderSubtle)).toBeGreaterThan(luminance(NEUTRAL.border));
    expect(luminance(NEUTRAL.input)).toBeLessThan(luminance(NEUTRAL.border));
  });

  /**
   * "Shadows: essentially none — borders carry hierarchy." A card shadow token
   * is how the previous palette separated surfaces; leaving one defined is an
   * invitation to reintroduce it one component at a time.
   */
  it('has no card elevation, only floating layers', () => {
    expect(tokensCss).not.toMatch(/--shadow-card/);
    expect(token(':root {', 'shadow-popover')).toBe('0 4px 14px rgb(0 0 0 / 0.16)');
  });

  it.each(ACCENTS)('$label overrides primary, its foreground and the ring', (accent) => {
    const selector = `[data-accent='${accent.name}'] {`;
    expect(token(selector, 'primary')).toBe(accent.primary);
    expect(token(selector, 'primary-foreground')).toBe(accent.primaryForeground);
    // The focus ring is the accent: a neutral ring on a coloured button reads
    // as an unstyled focus state.
    expect(token(selector, 'ring')).toBe(accent.primary);
  });

  /**
   * Secondary text lands on three of the four surfaces. `panelStrong` is not in
   * the list on purpose: it is a *selection* fill — the explorer's active row —
   * and selected rows carry `--foreground`, which is checked separately. Muted
   * text on it measures 4.3:1, so putting it there is the thing to avoid.
   */
  it.each([
    ['card', NEUTRAL.card],
    ['background', NEUTRAL.background],
    ['panel', NEUTRAL.panel],
  ])('muted text stays legible on %s', (_name, surface) => {
    expect(contrast(NEUTRAL.mutedForeground, surface)).toBeGreaterThanOrEqual(4.5);
    // The subtle tier is labels and hints, so it takes the 3:1 large-text bar.
    expect(contrast(NEUTRAL.mutedSubtle, surface)).toBeGreaterThanOrEqual(3);
  });

  it('keeps a selected row readable', () => {
    expect(contrast(NEUTRAL.foreground, NEUTRAL.panelStrong)).toBeGreaterThanOrEqual(4.5);
  });
});

const DARK_BLOCK = ":root[data-theme='dark'] {";

/**
 * Dark is the same design inverted, not a second one — so it gets the same
 * guarantees rather than a smaller set of them.
 */
describe('dark accent contrast', () => {
  it.each(DARK_ACCENTS)('$label reaches 4.5:1 on a primary button', (accent) => {
    expect(contrast(accent.primary, accent.primaryForeground)).toBeGreaterThanOrEqual(4.5);
  });

  it('reads as the accent against the dark canvas', () => {
    // The failure this catches: reusing the light primaries, which are chosen to
    // sit on cream and vanish into near-black.
    for (const accent of DARK_ACCENTS) {
      expect(
        contrast(accent.primary, DARK_NEUTRAL.background),
        `${accent.label} on the dark canvas`,
      ).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('takes a dark foreground, which is why the field is per-accent', () => {
    for (const accent of DARK_ACCENTS) {
      expect(luminance(accent.primaryForeground)).toBeLessThan(luminance(accent.primary));
    }
  });

  it('offers the same five, so switching theme never changes your accent', () => {
    expect(DARK_ACCENTS.map((a) => a.name)).toEqual(ACCENTS.map((a) => a.name));
    expect(accentsFor('dark')).toBe(DARK_ACCENTS);
    expect(accentsFor('light')).toBe(ACCENTS);
  });
});

describe('tokens.css mirrors the dark palette', () => {
  it.each([
    ['background', DARK_NEUTRAL.background],
    ['foreground', DARK_NEUTRAL.foreground],
    ['card', DARK_NEUTRAL.card],
    ['popover', DARK_NEUTRAL.popover],
    ['muted', DARK_NEUTRAL.panel],
    ['muted-foreground', DARK_NEUTRAL.mutedForeground],
    ['border', DARK_NEUTRAL.border],
    ['border-subtle', DARK_NEUTRAL.borderSubtle],
    ['input', DARK_NEUTRAL.input],
    ['accent', DARK_NEUTRAL.hover],
    ['muted-subtle', DARK_NEUTRAL.mutedSubtle],
    ['surface-panel', DARK_NEUTRAL.panel],
    ['surface-panel-strong', DARK_NEUTRAL.panelStrong],
    ['destructive', DARK_NEUTRAL.destructive],
    ['env-test', DARK_ENV.test.fg],
    ['env-test-bg', DARK_ENV.test.bg],
    ['env-test-border', DARK_ENV.test.border],
    ['env-prod', DARK_ENV.prod.fg],
    ['env-prod-bg', DARK_ENV.prod.bg],
    ['env-prod-border', DARK_ENV.prod.border],
    ['file-notebook', DARK_FILE.notebook],
    ['file-code', DARK_FILE.code],
    ['file-config', DARK_FILE.config],
    ['code-fg', DARK_EDITOR.foreground],
    ['line-number', DARK_EDITOR.lineNumber],
    ['syntax-keyword', DARK_EDITOR.keyword],
    ['syntax-string', DARK_EDITOR.string],
    ['syntax-number', DARK_EDITOR.number],
    ['syntax-directive', DARK_EDITOR.directive],
    ['grid-header', DARK_GRID.header],
    ['row-failed', DARK_ROW.failed],
    ['row-failed-border', DARK_ROW.failedBorder],
    ['status-idle', DARK_STATUS.idle],
    ['status-running', DARK_STATUS.running],
    ['status-error', DARK_STATUS.error],
    ['status-warning', DARK_STATUS.warning],
    ['status-success', DARK_STATUS.success],
  ])('dark --%s', (name, expected) => {
    expect(token(DARK_BLOCK, name)).toBe(expected);
  });

  it('--code-bg is exactly the editor background Monaco paints in dark', () => {
    expect(token(DARK_BLOCK, 'code-bg')).toBe(DARK_EDITOR.background);
  });

  /**
   * The drift this is really guarding.
   *
   * `:root` still applies under `[data-theme='dark']` — the dark block only
   * overrides. So a colour token added to light and forgotten in dark does not
   * fail, break or warn: it silently keeps its light value, and one cream
   * rectangle appears in a dark app. Fonts and radii are excluded by looking at
   * the value: only things that are colours have to be answered twice.
   */
  it('overrides every colour the light theme defines', () => {
    const light = tokensCss.split(':root {')[1].slice(0, tokensCss.split(':root {')[1].indexOf('}'));
    const dark = tokensCss.split(DARK_BLOCK)[1];
    const darkBody = dark.slice(0, dark.indexOf('}'));

    const colours = [...light.matchAll(/--([\w-]+):\s*([^;]+);/g)]
      .filter(([, , value]) => /^(#|rgb)/.test(value.trim()))
      .map(([, name]) => name);

    expect(colours.length).toBeGreaterThan(30);
    for (const name of colours) {
      expect(darkBody, `--${name} has no dark value, so it stays light`)
        .toContain(`--${name}:`);
    }
  });
});

describe('dark surfaces', () => {
  /**
   * The same stacking rule as light — card above canvas above panel — checked as
   * a contrast *ratio* rather than a luminance delta.
   *
   * At the dark end luminance values are hundredths, so the light theme's
   * "differ by 0.02" would be unsatisfiable by anything that still looked like
   * one palette. A ratio says the same thing in a way that holds at both ends.
   */
  it('stacks card above canvas above panel', () => {
    const surfaces = [
      DARK_NEUTRAL.card, DARK_NEUTRAL.background, DARK_NEUTRAL.panel, DARK_NEUTRAL.panelStrong,
    ];
    for (let i = 1; i < surfaces.length; i++) {
      expect(luminance(surfaces[i - 1])).toBeGreaterThan(luminance(surfaces[i]));
      expect(
        contrast(surfaces[i - 1], surfaces[i]),
        `${surfaces[i - 1]} should sit visibly above ${surfaces[i]}`,
      ).toBeGreaterThan(1.025);
    }
  });

  it('reads text comfortably on every surface', () => {
    for (const surface of [DARK_NEUTRAL.card, DARK_NEUTRAL.background, DARK_NEUTRAL.panel]) {
      expect(contrast(DARK_NEUTRAL.foreground, surface)).toBeGreaterThanOrEqual(4.5);
      expect(contrast(DARK_NEUTRAL.mutedForeground, surface)).toBeGreaterThanOrEqual(4.5);
      // The faintest label colour. Light's equivalent manages 3.65, so holding
      // dark to the same bar is not a lowered one.
      expect(contrast(DARK_NEUTRAL.mutedSubtle, surface)).toBeGreaterThanOrEqual(3.6);
    }
  });

  it('is a genuinely dark palette, not a dimmed light one', () => {
    expect(luminance(DARK_NEUTRAL.background)).toBeLessThan(0.05);
    expect(luminance(NEUTRAL.background)).toBeGreaterThan(0.5);
  });
});

describe('paletteFor', () => {
  it('hands out the set an editor or the output frame needs', () => {
    expect(paletteFor('light').editor).toBe(EDITOR);
    expect(paletteFor('dark').editor).toBe(DARK_EDITOR);
    expect(paletteFor('dark').neutral.card).toBe(DARK_NEUTRAL.card);
    expect(paletteFor('light').grid).toBe(GRID);
  });
});
