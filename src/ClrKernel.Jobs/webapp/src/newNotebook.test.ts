import { describe, expect, it } from 'vitest';
import { notebookPath } from './newNotebook';

describe('notebookPath', () => {
  it('adds the extension when there is none', () => {
    expect(notebookPath('etl')).toBe('etl.nb.md');
    expect(notebookPath('reports/monthly')).toBe('reports/monthly.nb.md');
  });

  it('leaves an extension the server already opens', () => {
    expect(notebookPath('etl.nb.md')).toBe('etl.nb.md');
    expect(notebookPath('old.ipynb')).toBe('old.ipynb');
    expect(notebookPath('scratch.CSX')).toBe('scratch.CSX');
  });

  it('tidies what people actually type', () => {
    expect(notebookPath('  ./reports//monthly  ')).toBe('reports/monthly.nb.md');
    expect(notebookPath('/etl')).toBe('etl.nb.md');
    expect(notebookPath('reports\\monthly')).toBe('reports/monthly.nb.md');
  });

  it('refuses what is not a path', () => {
    expect(notebookPath('')).toBeNull();
    expect(notebookPath('   ')).toBeNull();
    expect(notebookPath('/')).toBeNull();
    expect(notebookPath('../secrets')).toBeNull();
    expect(notebookPath('a/../../b')).toBeNull();
  });

  it('reads a plain .md as the notebook it was meant to be', () => {
    // The server opens `.nb.md`, not `.md` — and `notes.md.nb.md` is nobody's
    // idea of what they typed.
    expect(notebookPath('notes.md')).toBe('notes.nb.md');
    expect(notebookPath('reports/notes.MD')).toBe('reports/notes.nb.md');
  });
});
