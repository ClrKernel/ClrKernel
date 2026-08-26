import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';
import { ACCENT_KEY } from './accent';
import { ACCENTS, DEFAULT_ACCENT } from './palette';

const indexHtml = readFileSync(
  fileURLToPath(new URL('../../index.html', import.meta.url)),
  'utf8',
);

/**
 * The accent has to be on `<html>` before the first paint, which means an inline
 * script that runs before any module loads — so it necessarily repeats the
 * storage key and the list of names. This is what stops that copy going stale:
 * add a sixth accent and forget the script, and this fails rather than the new
 * accent silently falling back to blue on every reload.
 */
describe('the pre-paint accent script', () => {
  it('reads the same localStorage key the app writes', () => {
    expect(indexHtml).toContain(`localStorage.getItem('${ACCENT_KEY}')`);
  });

  it('knows every accent name', () => {
    for (const accent of ACCENTS) {
      expect(indexHtml, `index.html does not list '${accent.name}'`).toContain(`'${accent.name}'`);
    }
  });

  it('falls back to the same default', () => {
    expect(indexHtml).toContain(`: '${DEFAULT_ACCENT}'`);
  });

  it('runs in the head, before the app module', () => {
    expect(indexHtml.indexOf(ACCENT_KEY)).toBeLessThan(indexHtml.indexOf('/src/main.tsx'));
  });
});
