import { describe, expect, it } from 'vitest';
import type { ApiCell, ApiLanguage, ApiSession } from './api';
import {
  cellsToRun,
  fileLanguage,
  isDirty,
  keepIds,
  languageOptions,
  mergeStatus,
  monacoLanguage,
  moveCell,
  removeCell,
  setCellLanguage,
  toApiCells,
  toRunCells,
  toSyncCells,
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

describe('fileLanguage', () => {
  it('answers for every file the editor can open, not just .nb.md', () => {
    expect(fileLanguage('a/b.nb.md')).toBe('markdown');
    expect(fileLanguage('Report.IPYNB')).toBe('json');
    expect(fileLanguage('etl.jobs.yaml')).toBe('yaml');
    expect(fileLanguage('setup.csx')).toBe('csharp');
    expect(fileLanguage('legacy.dib')).toBe('csharp');
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

  it('has nothing above the first cell', () => {
    // The run endpoint rejects an empty list, so the button that would produce
    // one is disabled — this is the fact it is disabled on.
    expect(cellsToRun(cells, 0, 'before')).toEqual([]);
  });
});

describe('toRunCells', () => {
  it('carries the cell id, which a save deliberately drops', () => {
    // The session keys each cell's outputs by the id it received. Without one,
    // the server falls back to position in the request, so running cell three
    // alone would file its output against cell one.
    const cells = withIds([cell({ source: 'a' }), cell({ source: 'b' })]);
    const posted = toRunCells([cells[1]]);
    expect(posted[0].id).toBe(cells[1].id);
    expect(posted[0].source).toBe('b');
    expect(toApiCells(cells)[0].id).toBeUndefined();
  });
});

describe('toSyncCells', () => {
  it('names C# the way the kernel does, not the way Monaco does', () => {
    // The trap: monacoLanguage() answers 'csharp' for the same cell, and that is
    // the right answer for highlighting and the wrong one for the wire — the
    // server dispatches its language services off what we send here, and it has
    // no language called 'csharp'. 'csharp-script' is what VS Code sends.
    const cells = withIds([cell({ tag: 'csharp', languageId: null, source: 'var a = 1;' })]);
    expect(monacoLanguage(cells[0].languageId, cells[0].tag)).toBe('csharp');
    expect(toSyncCells(cells)).toEqual([
      { id: cells[0].id, languageId: 'csharp-script', source: 'var a = 1;' },
    ]);
  });

  it('passes a real language id through untouched', () => {
    const cells = withIds([cell({ tag: 'sql', languageId: 'sql', source: 'select 1' })]);
    expect(toSyncCells(cells)[0].languageId).toBe('sql');
  });

  it('leaves markdown out, so prose is never parsed as code', () => {
    // Every open document in a notebook feeds completion context. A markdown cell
    // opened as C# would put its prose into the completion of every code cell —
    // and dropping it here is also what closes one that used to be code.
    const cells = withIds([
      cell({ kind: 'markdown', tag: null, source: '# Heading' }),
      cell({ tag: 'csharp', source: 'code' }),
    ]);
    expect(toSyncCells(cells).map((c) => c.source)).toEqual(['code']);
  });
});

describe('withIds', () => {
  it('mints fresh ids rather than numbering by position', () => {
    // Delete the first cell, save, reload: a positional id would hand the new
    // first cell the old first cell's outputs. Losing them is honest; showing
    // someone else's is not.
    const first = withIds([cell({ source: 'a' }), cell({ source: 'b' })]);
    const reloaded = withIds([cell({ source: 'b' })]);
    expect(reloaded[0].id).not.toBe(first[0].id);
    expect(new Set(first.map((c) => c.id)).size).toBe(2);
  });
});

describe('keepIds', () => {
  it('carries ids across a save, so saving does not clear what just ran', () => {
    const before = withIds([cell({ source: 'a' }), cell({ source: 'b' })]);
    const readBack: ApiCell[] = [cell({ source: 'a' }), cell({ source: 'b' })];
    expect(keepIds(readBack, before).map((c) => c.id)).toEqual(before.map((c) => c.id));
  });

  it('mints fresh ids when the server re-parsed a different number of cells', () => {
    // The alignment assumption is gone, so run state is dropped rather than
    // shifted onto the wrong cells.
    const before = withIds([cell({ source: 'a' }), cell({ source: 'b' })]);
    const merged: ApiCell[] = [cell({ source: 'a\nb' })];
    expect(keepIds(merged, before)[0].id).not.toBe(before[0].id);
  });
});

describe('mergeStatus', () => {
  const cells = withIds([cell({ source: 'a' }), cell({ source: 'b' })]);
  const session = (over: Partial<ApiSession> = {}): ApiSession => ({
    started: true, running: false, ...over,
  });

  it('joins a cell to what the session says it did', () => {
    const status = session({
      cells: {
        [cells[0].id]: {
          status: 'succeeded', executionCount: 3, truncated: false,
          outputs: [{ output_type: 'stream', text: 'hi' }],
        },
      },
    });
    const merged = mergeStatus(cells, status, {});
    expect(merged[cells[0].id]).toMatchObject({ status: 'succeeded', executionCount: 3, stale: false });
    expect(merged[cells[1].id]).toBeUndefined();
  });

  it('marks output stale once the cell it came from is edited', () => {
    const status = session({
      cells: { [cells[0].id]: { status: 'succeeded', executionCount: 1, truncated: false, outputs: [] } },
    });
    const ran = { [cells[0].id]: 'a' };
    expect(mergeStatus(cells, status, ran)[cells[0].id].stale).toBe(false);

    const edited = [{ ...cells[0], source: 'a + 1' }, cells[1]];
    expect(mergeStatus(edited, status, ran)[cells[0].id].stale).toBe(true);
  });

  it('ignores state the session still holds for cells that are gone', () => {
    const status = session({
      cells: { 'deleted-cell': { status: 'succeeded', executionCount: 1, truncated: false, outputs: [] } },
    });
    expect(mergeStatus(cells, status, {})).toEqual({});
  });

  it('is empty before anything has run', () => {
    expect(mergeStatus(cells, null, {})).toEqual({});
    expect(mergeStatus(cells, session({ started: false }), {})).toEqual({});
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
