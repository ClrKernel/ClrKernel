import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DEFAULT_MODE, THEME_KEY, applyThemeMode, isThemeMode, loadThemeMode, resolveTheme, systemTheme,
} from './theme';

const indexHtml = readFileSync(
  fileURLToPath(new URL('../../index.html', import.meta.url)),
  'utf8',
);

/** Enough DOM and storage for a module that only touches `<html>` and one key. */
function stubBrowser(prefersDark: boolean) {
  const store = new Map<string, string>();
  const listeners: Array<() => void> = [];
  const root = { dataset: {} as Record<string, string> };
  (globalThis as Record<string, unknown>).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
  };
  (globalThis as Record<string, unknown>).document = { documentElement: root };
  (globalThis as Record<string, unknown>).window = {
    matchMedia: (query: string) => ({
      matches: prefersDark && query.includes('dark'),
      addEventListener: (_: string, fn: () => void) => listeners.push(fn),
      removeEventListener: () => undefined,
    }),
  };
  return { store, root };
}

afterEach(() => {
  for (const key of ['localStorage', 'document', 'window']) {
    delete (globalThis as Record<string, unknown>)[key];
  }
  vi.restoreAllMocks();
});

describe('theme mode', () => {
  it('follows the system by default', () => {
    stubBrowser(true);
    expect(loadThemeMode()).toBe(DEFAULT_MODE);
    expect(DEFAULT_MODE).toBe('system');
    expect(resolveTheme('system')).toBe('dark');
  });

  it('and an explicit choice overrides it in both directions', () => {
    stubBrowser(true);
    expect(resolveTheme('light')).toBe('light');
    stubBrowser(false);
    expect(resolveTheme('dark')).toBe('dark');
  });

  it('stamps the resolved theme on <html>, never the word "system"', () => {
    // The token layer keys off `[data-theme='dark']` alone. If "system" were
    // written here, the CSS would need a media query as well and the two could
    // disagree — and an explicit choice could no longer override the OS at all.
    const { root, store } = stubBrowser(true);

    expect(applyThemeMode('system')).toBe('dark');
    expect(root.dataset.theme).toBe('dark');
    expect(store.get(THEME_KEY)).toBe('system');

    expect(applyThemeMode('light')).toBe('light');
    expect(root.dataset.theme).toBe('light');
    expect(store.get(THEME_KEY)).toBe('light');
  });

  it('remembers the mode and not the resolution', () => {
    // Storing "dark" for a user on `system` would freeze them at whatever the OS
    // said the first time.
    const { store } = stubBrowser(true);
    applyThemeMode('system');
    expect(store.get(THEME_KEY)).toBe('system');
    expect(loadThemeMode()).toBe('system');
  });

  it('survives storage that throws, which is what private browsing does', () => {
    (globalThis as Record<string, unknown>).localStorage = {
      getItem: () => { throw new Error('denied'); },
      setItem: () => { throw new Error('denied'); },
    };
    (globalThis as Record<string, unknown>).document = { documentElement: { dataset: {} } };
    (globalThis as Record<string, unknown>).window = { matchMedia: () => ({ matches: false }) };

    expect(loadThemeMode()).toBe('system');
    expect(() => applyThemeMode('dark')).not.toThrow();
  });

  it('falls back to light where nothing can answer', () => {
    (globalThis as Record<string, unknown>).window = {};
    expect(systemTheme()).toBe('light');
  });

  it('rejects a hand-edited value', () => {
    expect(isThemeMode('dark')).toBe(true);
    expect(isThemeMode('system')).toBe(true);
    expect(isThemeMode('sepia')).toBe(false);
    const { store } = stubBrowser(false);
    store.set(THEME_KEY, 'sepia');
    expect(loadThemeMode()).toBe('system');
  });
});

/**
 * The theme has to be on `<html>` before the first paint — a white flash on the
 * way into a dark app is the thing people actually notice — which means an
 * inline script that runs before any module loads, and therefore a second copy
 * of the key and the fallback. This is what stops that copy going stale.
 */
describe('the pre-paint theme script', () => {
  it('reads the same localStorage key the app writes', () => {
    expect(indexHtml).toContain(`localStorage.getItem('${THEME_KEY}')`);
  });

  it('resolves "system" itself rather than leaving it to a media query', () => {
    expect(indexHtml).toContain("matchMedia('(prefers-color-scheme: dark)')");
    expect(indexHtml).toContain('root.dataset.theme');
  });

  it('never writes the word "system" onto the element', () => {
    const script = indexHtml.slice(indexHtml.indexOf('<script>'), indexHtml.indexOf('</script>'));
    expect(script).not.toMatch(/dataset\.theme\s*=\s*['"]system['"]/);
  });

  it('runs in the head, before the app module', () => {
    expect(indexHtml.indexOf(THEME_KEY)).toBeLessThan(indexHtml.indexOf('/src/main.tsx'));
  });
});
