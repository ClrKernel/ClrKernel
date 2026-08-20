import { describe, expect, it } from 'vitest';
import type { ApiCell, ApiLanguage } from './api';
import {
  cellsToRun,
  isDirty,
  languageOptions,
  monacoLanguage,
  moveCell,
  removeCell,
  setCellLanguage,
  toApiCells,
  withIds,
} from './notebook';

const languages: ApiLanguage[] = [
  { id: 'sql', displayName: 'SQL', defaultSelector: '#!sql', selectors: ['#!sql'], languageTags: ['sql', 'tsql'] },
  {
    id: 'shellscript', displayName: 'Shell', defaultSelector: '#!bash',
    selectors: ['#!bash', '#!zsh'], languageTags: ['bash', 'zsh', 'sh', 'shell'],
  },
  { id: 'dax', displayName: 'DAX', defaultSelector: '#!dax', selectors: ['#!dax'], languageTags: ['dax'] },
];

const cell = (over: Partial<ApiCell> = {}): ApiCell => ({
  kind: 'code', tag: 'csharp', languageId: null, source: '', ...over,
});

describe('monacoLanguage', () => {
  it('maps kernel language ids onto Monaco grammars', () => {
    expect(monacoLanguage('sql')).toBe('sql');
    expect(monacoLanguage('powershell')).toBe('powershell');
    expect(monacoLanguage('shellscript')).toBe('shell');
  });

  it('falls back to plain text rather than a wrong highlighter', () => {
    // Monaco ships no grammar for these; guessing would colour them wrongly.
    expect(monacoLanguage('dax')).toBe('plaintext');
    expect(monacoLanguage('mermaid')).toBe('plaintext');
    expect(monacoLanguage('http')).toBe('plaintext');
  });

  it('treats a tagged cell with no language as C#, and an untagged one as prose', () => {
    expect(monacoLanguage(null, 'csharp')).toBe('csharp');
    expect(monacoLanguage(null, null)).toBe('markdown');
  });
});

describe('cellsToRun', () => {
  const cells = withIds([
    cell({ source: 'a' }),
    cell({ kind: 'markdown', tag: null, source: 'prose' }),
    cell({ source: 'b' }),
    cell({ source: 'c' }),
  ]);

  it('runs one cell', () => {
    expect(cellsToRun(cells, 2, 'one').map((c) => c.source)).toEqual(['b']);
  });

  it('runs everything before, excluding the cell itself', () => {
    expect(cellsToRun(cells, 2, 'before').map((c) => c.source)).toEqual(['a']);
  });

  it('runs the cell and everything after', () => {
    expect(cellsToRun(cells, 2, 'after').map((c) => c.source)).toEqual(['b', 'c']);
  });

  it('runs all code cells', () => {
    expect(cellsToRun(cells, 0, 'all').map((c) => c.source)).toEqual(['a', 'b', 'c']);
  });

  it('never runs markdown', () => {
    expect(cellsToRun(cells, 1, 'one')).toEqual([]);
  });
});

describe('structural edits', () => {
  const cells = withIds([cell({ source: 'a' }), cell({ source: 'b' }), cell({ source: 'c' })]);

  it('moves a cell and leaves the ends alone', () => {
    expect(moveCell(cells, 0, 2).map((c) => c.source)).toEqual(['b', 'c', 'a']);
    expect(moveCell(cells, 0, -1)).toBe(cells);
    expect(moveCell(cells, 2, 3)).toBe(cells);
  });

  it('removes a cell', () => {
    expect(removeCell(cells, 1).map((c) => c.source)).toEqual(['a', 'c']);
  });
});

describe('setCellLanguage', () => {
  it('clears the tag so the server computes one for the new language', () => {
    // A tag that survives is a tag the file already had — those are never rewritten.
    const sql = setCellLanguage(withIds([cell()])[0], 'sql', languages);
    expect(sql).toMatchObject({ kind: 'code', tag: null, languageId: 'sql' });
  });

  it('keeps C# and Markdown expressible without a descriptor', () => {
    const start = withIds([cell({ tag: 'sql', languageId: 'sql' })])[0];
    expect(setCellLanguage(start, 'csharp', languages)).toMatchObject({ kind: 'code', tag: 'csharp', languageId: null });
    expect(setCellLanguage(start, 'markdown', languages)).toMatchObject({ kind: 'markdown', tag: null });
  });

  it('offers Markdown and C# alongside every kernel language', () => {
    expect(languageOptions(languages).map((o) => o.value)).toEqual([
      'markdown', 'csharp', 'sql', 'shellscript', 'dax',
    ]);
  });
});

describe('isDirty', () => {
  const saved: ApiCell[] = [cell({ source: 'a' }), cell({ kind: 'markdown', tag: null, source: 'prose' })];

  it('is false for an untouched notebook', () => {
    // The guard that stops opening-and-saving from rewriting a file, which would
    // commit and invalidate the notebook's promotion evidence.
    expect(isDirty(withIds(saved), saved)).toBe(false);
  });

  it('sees an edit, a language change and a reorder', () => {
    const cells = withIds(saved);
    expect(isDirty([{ ...cells[0], source: 'changed' }, cells[1]], saved)).toBe(true);
    expect(isDirty([setCellLanguage(cells[0], 'sql', languages), cells[1]], saved)).toBe(true);
    expect(isDirty(moveCell(cells, 0, 1), saved)).toBe(true);
  });

  it('drops editor-only fields on the way out', () => {
    expect(toApiCells(withIds(saved))[0]).not.toHaveProperty('id');
  });
});
