import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { ACCENTS, EDITOR, ENV, GRID, NEUTRAL, ROW, STATUS, isAccentName } from './palette';

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
    ['env-dev', ENV.dev.fg],
    ['env-dev-bg', ENV.dev.bg],
    ['env-dev-border', ENV.dev.border],
    ['env-prod', ENV.prod.fg],
    ['env-prod-bg', ENV.prod.bg],
    ['env-prod-border', ENV.prod.border],
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
