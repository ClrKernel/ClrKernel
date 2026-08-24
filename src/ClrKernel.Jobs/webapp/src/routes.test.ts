import { describe, expect, it } from 'vitest';
import {
  connectionsPath,
  editPath,
  isEditorPath,
  viewOf,
  jobPath,
  legacyEditPath,
  newJobPath,
  pathFromSplat,
  sectionOf,
  switchProject,
} from './routes';

describe('editPath', () => {
  it('keeps a notebook path readable', () => {
    expect(editPath('default', 'mine', 'reports/monthly.nb.md'))
      .toBe('/files/default/edit/mine/reports/monthly.nb.md');
  });

  it('names the branch as one segment, whoever owns it', () => {
    expect(editPath('dw', 'user-6652fd16-bc3b-4750-a18c-b603c9cdac85', 'etl.nb.md'))
      .toBe('/files/dw/edit/user-6652fd16-bc3b-4750-a18c-b603c9cdac85/etl.nb.md');
  });

  it('escapes what is inside a segment but not the separators', () => {
    expect(editPath('default', 'mine', 'my reports/a b.nb.md'))
      .toBe('/files/default/edit/mine/my%20reports/a%20b.nb.md');
    expect(editPath('default', 'mine', '/leading/slash.nb.md'))
      .toBe('/files/default/edit/mine/leading/slash.nb.md');
  });

  it('round-trips through the splat the router hands back', () => {
    for (const path of ['etl.nb.md', 'reports/monthly.nb.md', 'my reports/a b.nb.md']) {
      const built = editPath('default', 'mine', path);
      expect(pathFromSplat(built.split('/edit/mine/')[1])).toBe(path);
    }
  });

  it('survives a splat that is not valid escaping', () => {
    expect(pathFromSplat('100%/done.nb.md')).toBe('100%/done.nb.md');
    expect(pathFromSplat(undefined)).toBe('');
  });
});

describe('the view is a URL', () => {
  it('sits where `edit` does, so the three are siblings', () => {
    expect(editPath('default', 'mine', 'etl.nb.md'))
      .toBe('/files/default/edit/mine/etl.nb.md');
    expect(editPath('default', 'mine', 'etl.nb.md', 'source'))
      .toBe('/files/default/source/mine/etl.nb.md');
    expect(editPath('default', 'test', 'reports/monthly.nb.md', 'diff'))
      .toBe('/files/default/diff/test/reports/monthly.nb.md');
  });

  it('is read back out of a path, and only out of one that has one', () => {
    expect(viewOf('/files/default/source/mine/etl.nb.md')).toBe('source');
    expect(viewOf('/files/default/diff/mine/etl.nb.md')).toBe('diff');
    expect(viewOf('/files/default')).toBeNull();
    expect(viewOf('/files/default/preview/mine/etl.nb.md')).toBeNull();
    expect(viewOf('/jobs/default/test/nightly')).toBeNull();
  });

  it('keeps the editor layout on every one of them', () => {
    // Source and Diff fill the pane exactly as the notebook does; missing one
    // here is a page that scrolls twice rather than an error.
    expect(isEditorPath('/files/default/source/mine/etl.nb.md')).toBe(true);
    expect(isEditorPath('/files/default/diff/mine/etl.nb.md')).toBe(true);
  });
});

describe('switchProject', () => {
  it('goes to the same section in the other project', () => {
    expect(switchProject('/jobs/default/mine/nightly', 'finance')).toBe('/jobs/finance');
    expect(switchProject('/files/default/edit/mine/etl.nb.md', 'finance')).toBe('/files/finance');
    expect(switchProject('/files/default', 'finance')).toBe('/files/finance');
  });

  it('has nowhere to go from a page that is not about a project', () => {
    for (const path of ['/', '/settings/accounts', '/channels', '/runs/abc']) {
      expect(switchProject(path, 'finance')).toBeNull();
      expect(sectionOf(path)).toBeNull();
    }
  });
});

describe('isEditorPath', () => {
  it('is the editor only where the editor is', () => {
    expect(isEditorPath('/files/default/edit/mine/etl.nb.md')).toBe(true);
    expect(isEditorPath('/files/default')).toBe(false);
    expect(isEditorPath('/jobs/default/mine/nightly')).toBe(false);
  });
});

describe('legacyEditPath', () => {
  it('moves an old shared link to where the file lives now', () => {
    expect(legacyEditPath('?project=default&path=reports%2Fmonthly.nb.md&branch=test'))
      .toBe('/files/default/edit/test/reports/monthly.nb.md');
  });

  it('fills in what an older link left out', () => {
    expect(legacyEditPath('?path=etl.nb.md')).toBe('/files/default/edit/mine/etl.nb.md');
  });

  it('sends a link with no file at all to the file list', () => {
    expect(legacyEditPath('?project=finance')).toBe('/files/finance');
  });
});

describe('job paths', () => {
  it('escapes a job name that is not url-safe', () => {
    expect(jobPath('default', 'test', 'nightly/close')).toBe('/jobs/default/test/nightly%2Fclose');
    expect(newJobPath('default', 'mine', 'reports/monthly.nb.md'))
      .toBe('/jobs/default/mine/new?notebook=reports%2Fmonthly.nb.md');
    expect(newJobPath('default', 'mine')).toBe('/jobs/default/mine/new');
  });
});

describe('connectionsPath', () => {
  it('names no project — a connection belongs to the server, not to a repo', () => {
    expect(connectionsPath()).toBe('/connections');
    expect(connectionsPath('abc123')).toBe('/connections/abc123');
  });
});
