import { describe, expect, it } from 'vitest';
import { changedTree, type Change } from './commitTree';

function change(path: string, status = 'M'): Change {
  return { status, path, oldPath: null };
}

describe('changedTree', () => {
  it('writes a folder once, over the files in it', () => {
    const rows = changedTree([
      change('reports/monthly.nb.md'),
      change('reports/weekly.nb.md'),
    ]);
    expect(rows.map((r) => `${r.depth}:${r.name}`))
      .toEqual(['0:reports', '1:monthly.nb.md', '1:weekly.nb.md']);
  });

  it('does not file one folder under another because the names match', () => {
    // `c/b` is not `a/b`. Comparing depth by depth without carrying the
    // divergence down put w.md under `a/b` and never drew `c` at all.
    const rows = changedTree([change('a/b/y.md'), change('c/b/w.md')]);
    expect(rows.map((r) => r.key)).toEqual([
      'a/', 'a/b/', 'a/b/y.md', 'c/', 'c/b/', 'c/b/w.md',
    ]);
  });

  it('leaves a root file at the top', () => {
    const rows = changedTree([change('etl.nb.md'), change('sub/x.md')]);
    expect(rows.map((r) => `${r.depth}:${r.name}`))
      .toEqual(['0:etl.nb.md', '0:sub', '1:x.md']);
  });

  it('carries the change onto the file row and nothing onto the folder', () => {
    const rows = changedTree([change('sub/gone.md', 'D')]);
    expect(rows[0].change).toBeNull();
    expect(rows[1].change?.status).toBe('D');
  });
});
