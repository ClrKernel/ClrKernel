import { beforeEach, describe, expect, it } from 'vitest';
import { clamp, loadNotebookState, saveNotebookState } from './prefs';

const KEY = 'clrkernel-studio-notebook-state';

// vitest runs in node, where there is no localStorage. A Map is the whole of
// what prefs.ts uses, and stubbing it beats pulling in jsdom for two methods.
beforeEach(() => {
  const store = new Map<string, string>();
  (globalThis as { localStorage?: unknown }).localStorage = {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  };
});

/** What is actually in storage, past what the typed API admits to. */
function raw(path: string): Record<string, unknown> {
  return JSON.parse(localStorage.getItem(KEY) ?? '{}')[path] ?? {};
}

describe('per-notebook state', () => {
  it('keeps each notebook’s answers apart', () => {
    saveNotebookState('a.nb.md', { mode: 'focus', activeCell: 3 });
    saveNotebookState('b.nb.md', { mode: 'normal' });

    expect(loadNotebookState('a.nb.md')).toEqual({ mode: 'focus', activeCell: 3 });
    expect(loadNotebookState('b.nb.md').mode).toBe('normal');
    expect(loadNotebookState('never-opened.nb.md')).toEqual({});
  });

  it('merges rather than replaces, so one field does not wipe the other', () => {
    saveNotebookState('a.nb.md', { mode: 'focus' });
    saveNotebookState('a.nb.md', { activeCell: 2 });
    expect(loadNotebookState('a.nb.md')).toEqual({ mode: 'focus', activeCell: 2 });
  });

  it('drops the id that used to be stored in its place', () => {
    // Written by a previous version. It is a per-page-load id, so it is not
    // worth migrating — only worth not leaving behind.
    localStorage.setItem(KEY, JSON.stringify({
      'a.nb.md': { mode: 'focus', activeCellId: 'n7' },
    }));

    expect(loadNotebookState('a.nb.md').activeCell).toBeUndefined();
    saveNotebookState('a.nb.md', { activeCell: 1 });

    expect(raw('a.nb.md')).toEqual({ mode: 'focus', activeCell: 1 });
  });

  it('survives a value somebody hand-edited into nonsense', () => {
    localStorage.setItem(KEY, '{not json');
    expect(loadNotebookState('a.nb.md')).toEqual({});
    // And a position out of range is somebody else's problem to clamp; what
    // matters here is that reading it back does not throw.
    saveNotebookState('a.nb.md', { activeCell: 99 });
    expect(loadNotebookState('a.nb.md').activeCell).toBe(99);
  });
});

describe('clamp', () => {
  it('holds a remembered position inside the notebook it is indexing', () => {
    expect(clamp(99, 0, 2)).toBe(2);
    expect(clamp(-1, 0, 2)).toBe(0);
    expect(clamp(1, 0, 2)).toBe(1);
  });

  it('and answers with the floor for a value that is not a number at all', () => {
    expect(clamp(NaN, 0, 2)).toBe(0);
  });
});
