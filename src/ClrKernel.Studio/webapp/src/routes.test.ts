import { describe, expect, it } from 'vitest';
import {
  connectionsPath,
  editPath,
  branchOf,
  isEditorPath,
  isFilesShellPath,
  isFullBleed,
  legacyFilesPath,
  viewOf,
  jobRunsPath,
  legacyEditPath,
  jobsFilePath,
  pathFromSplat,
  sectionOf,
  switchProject,
} from './routes';

describe('editPath', () => {
  it('keeps a notebook path readable', () => {
    expect(editPath('default', 'mine', 'reports/monthly.nb.md'))
      .toBe('/files/default/mine/edit/reports/monthly.nb.md');
  });

  it('names the branch as one segment, whoever owns it', () => {
    expect(editPath('dw', 'user-6652fd16-bc3b-4750-a18c-b603c9cdac85', 'etl.nb.md'))
      .toBe('/files/dw/user-6652fd16-bc3b-4750-a18c-b603c9cdac85/edit/etl.nb.md');
  });

  it('escapes what is inside a segment but not the separators', () => {
    expect(editPath('default', 'mine', 'my reports/a b.nb.md'))
      .toBe('/files/default/mine/edit/my%20reports/a%20b.nb.md');
    expect(editPath('default', 'mine', '/leading/slash.nb.md'))
      .toBe('/files/default/mine/edit/leading/slash.nb.md');
  });

  it('round-trips through the splat the router hands back', () => {
    for (const path of ['etl.nb.md', 'reports/monthly.nb.md', 'my reports/a b.nb.md']) {
      const built = editPath('default', 'mine', path);
      expect(pathFromSplat(built.split('/mine/edit/')[1])).toBe(path);
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
      .toBe('/files/default/mine/edit/etl.nb.md');
    expect(editPath('default', 'mine', 'etl.nb.md', 'source'))
      .toBe('/files/default/mine/source/etl.nb.md');
    expect(editPath('default', 'test', 'reports/monthly.nb.md', 'diff'))
      .toBe('/files/default/test/diff/reports/monthly.nb.md');
  });

  it('is read back out of a path, and only out of one that has one', () => {
    expect(viewOf('/files/default/mine/source/etl.nb.md')).toBe('source');
    expect(viewOf('/files/default/mine/diff/etl.nb.md')).toBe('diff');
    expect(viewOf('/files/default/mine/preview/logo.svg')).toBe('preview');
    expect(viewOf('/files/default')).toBeNull();
    // A segment that is not a view reads as none, rather than as a view nothing
    // renders. `preview` used to be this example, which is what makes the case
    // worth keeping: the list is the authority and it grew.
    expect(viewOf('/files/default/mine/render/etl.nb.md')).toBeNull();
    expect(viewOf('/jobs/default/test/nightly')).toBeNull();
  });

  it('keeps the editor layout on every one of them', () => {
    // Source and Diff fill the pane exactly as the notebook does; missing one
    // here is a page that scrolls twice rather than an error.
    expect(isEditorPath('/files/default/mine/source/etl.nb.md')).toBe(true);
    expect(isEditorPath('/files/default/mine/diff/etl.nb.md')).toBe(true);
  });
});

describe('switchProject', () => {
  it('goes to the same section in the other project', () => {
    expect(switchProject('/files/default/mine/edit/etl.nb.md', 'finance')).toBe('/files/finance');
    expect(switchProject('/files/default', 'finance')).toBe('/files/finance');
  });

  it('has nowhere to go from a page that is not about a project', () => {
    for (const path of ['/', '/monitoring', '/settings/accounts', '/channels', '/runs/abc']) {
      expect(switchProject(path, 'finance')).toBeNull();
      expect(sectionOf(path)).toBeNull();
    }
  });
});

describe('isFullBleed', () => {
  /**
   * What decides whether the page gets the standard padding or manages its own
   * gutters. Files is both now: the shell before a file is open is the same
   * explorer beside an empty pane, so it cannot be the one with a margin.
   */
  it('covers the whole Files area, and not the doors to it', () => {
    expect(isFullBleed('/files/default/mine')).toBe(true);
    expect(isFullBleed('/files/default/mine/edit/etl.nb.md')).toBe(true);
    expect(isFullBleed('/connections')).toBe(true);

    // Both doors redirect and paint nothing on the way: `/files` to a project,
    // `/files/:project` to the branch you were last on there.
    expect(isFullBleed('/files')).toBe(false);
    expect(isFullBleed('/files/default')).toBe(false);
    expect(isFullBleed('/monitoring')).toBe(false);
    expect(isFullBleed('/')).toBe(false);
  });

  it('tells the shell and a file apart by segment count', () => {
    expect(isFilesShellPath('/files/default/mine')).toBe(true);
    expect(isFilesShellPath('/files/default/mine/')).toBe(true);
    expect(isFilesShellPath('/files/default/mine/edit/etl.nb.md')).toBe(false);
    // The door, which is a redirect rather than a place.
    expect(isFilesShellPath('/files/default')).toBe(false);
    expect(isFilesShellPath('/files')).toBe(false);
    expect(isFilesShellPath('/settings/files')).toBe(false);
  });

  it('reads the branch out of the address, which is where it lives now', () => {
    expect(branchOf('/files/default/mine')).toBe('mine');
    expect(branchOf('/files/default/mine/edit/etl.nb.md')).toBe('mine');
    expect(branchOf('/files/dw/user-ada/diff/reports/monthly.nb.md')).toBe('user-ada');
    expect(branchOf('/files/default')).toBeNull();
    expect(branchOf('/monitoring')).toBeNull();
  });

  it('redirects a link written before the branch moved in front of the view', () => {
    // The views are a closed set and no branch is called `edit`, so the third
    // segment is what tells an old link from a new one.
    expect(legacyFilesPath('/files/default/edit/mine/etl.nb.md'))
      .toBe('/files/default/mine/edit/etl.nb.md');
    expect(legacyFilesPath('/files/dw/diff/test/reports/monthly.nb.md'))
      .toBe('/files/dw/test/diff/reports/monthly.nb.md');
    // Already new, so not a legacy link — segment 2 is a branch, not a view.
    expect(legacyFilesPath('/files/default/mine/edit/etl.nb.md')).toBeNull();
    expect(legacyFilesPath('/files/default/mine')).toBeNull();
  });
});

describe('isEditorPath', () => {
  it('is the editor only where the editor is', () => {
    expect(isEditorPath('/files/default/mine/edit/etl.nb.md')).toBe(true);
    expect(isEditorPath('/files/default')).toBe(false);
    expect(isEditorPath('/monitoring')).toBe(false);
  });
});

describe('legacyEditPath', () => {
  it('moves an old shared link to where the file lives now', () => {
    expect(legacyEditPath('?project=default&path=reports%2Fmonthly.nb.md&branch=test'))
      .toBe('/files/default/test/edit/reports/monthly.nb.md');
  });

  it('fills in what an older link left out', () => {
    expect(legacyEditPath('?path=etl.nb.md')).toBe('/files/default/mine/edit/etl.nb.md');
  });

  it('sends a link with no file at all to the file list', () => {
    expect(legacyEditPath('?project=finance')).toBe('/files/finance');
  });
});

describe('job paths', () => {
  // A job has no page. It is an entry in a file, so it opens as that file's
  // Overview, and its history is the one grid filtered to it.
  it('a job opens as the file that defines it', () => {
    expect(jobsFilePath('default', 'test', 'etl.jobs.yaml'))
      .toBe('/files/default/test/overview/etl.jobs.yaml');
  });

  it('its runs are the monitoring grid, filtered', () => {
    expect(jobRunsPath('default', 'prod', 'nightly'))
      .toBe('/monitoring?project=default&env=prod&job=nightly');
  });

  it('escapes a job name that is not url-safe', () => {
    expect(jobRunsPath('default', 'test', 'nightly/close'))
      .toBe('/monitoring?project=default&env=test&job=nightly%2Fclose');
  });
});

describe('connectionsPath', () => {
  it('names no project — a connection belongs to the server, not to a repo', () => {
    expect(connectionsPath()).toBe('/connections');
    expect(connectionsPath('abc123')).toBe('/connections/abc123');
  });
});
