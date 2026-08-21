import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { ACCENTS, EDITOR, NEUTRAL, STATUS, isAccentName } from './palette';

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
    ['muted', NEUTRAL.muted],
    ['muted-foreground', NEUTRAL.mutedForeground],
    ['border', NEUTRAL.border],
    ['destructive', NEUTRAL.destructive],
    ['surface-rail', NEUTRAL.surfaceRail],
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
   * The first palette put page, card, border and rail inside a 10-unit band and
   * the whole app read as one flat grey; the second over-corrected into a grey
   * page with white cards, which read heavy. The model now is a white canvas
   * with tinted chrome, so what has to hold is: the chrome is tinted enough to
   * be chrome, the rail is a step past it, and the border reads on white.
   */
  it('separates chrome from canvas by enough to see', () => {
    const value = (hex: string) => parseInt(hex.slice(1, 3), 16);
    expect(value(NEUTRAL.background)).toBe(0xff);
    expect(value(NEUTRAL.card)).toBe(0xff);
    // Chrome is tinted against the white canvas...
    expect(value(NEUTRAL.card) - value(NEUTRAL.muted)).toBeGreaterThanOrEqual(6);
    // ...and the rail is a further step past the chrome bars.
    expect(value(NEUTRAL.muted) - value(NEUTRAL.surfaceRail)).toBeGreaterThanOrEqual(5);
    // A border has to be visible against the lightest surface it sits on.
    expect(value(NEUTRAL.card) - value(NEUTRAL.border)).toBeGreaterThanOrEqual(30);
  });

  /** A white card on a white page is only a card because it is lifted. */
  it('lifts panels off the canvas', () => {
    expect(token(':root {', 'shadow-card')).toMatch(/rgb\(0 0 0 \/ 0\.0[3-9]\)/);
  });

  it.each(ACCENTS)('$label overrides primary, its foreground and the ring', (accent) => {
    const selector = `[data-accent='${accent.name}'] {`;
    expect(token(selector, 'primary')).toBe(accent.primary);
    expect(token(selector, 'primary-foreground')).toBe(accent.primaryForeground);
    // The focus ring is the accent: a neutral ring on a coloured button reads
    // as an unstyled focus state.
    expect(token(selector, 'ring')).toBe(accent.primary);
  });
});
