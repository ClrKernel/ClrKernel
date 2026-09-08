import { describe, expect, it } from 'vitest';
import { breadcrumbFor, MAX_CRUMB, middleTruncate } from './breadcrumb';

describe('middleTruncate', () => {
  it('leaves anything within the limit alone', () => {
    expect(middleTruncate('demo.nb.md')).toBe('demo.nb.md');
  });

  it('keeps both ends and never exceeds the limit', () => {
    const long = 'reporting/monthly/very-long-notebook-name-for-testing.nb.md';
    const short = middleTruncate(long);
    expect(short.length).toBeLessThanOrEqual(MAX_CRUMB);
    expect(short).toContain('…');
    // The tail is what distinguishes two notebooks in the same folder, so it
    // has to survive.
    expect(short.endsWith('.nb.md')).toBe(true);
    expect(short.startsWith('reporting/')).toBe(true);
  });

  it('is exact at the boundary', () => {
    const exact = 'x'.repeat(MAX_CRUMB);
    expect(middleTruncate(exact)).toBe(exact);
    expect(middleTruncate(exact + 'y').length).toBe(MAX_CRUMB);
  });
});

describe('breadcrumbFor', () => {
  it('names the dashboard at the root', () => {
    expect(breadcrumbFor('/')).toEqual([{ label: 'Dashboard' }]);
  });

  it.each([
    ['/channels', 'Channels'],
    ['/settings', 'Settings'],
  ])('%s is a single crumb', (pathname, label) => {
    expect(breadcrumbFor(pathname)).toEqual([{ label }]);
  });

  // Monitoring is a view of the Dashboard, not a section, and the trail is where
  // that is said — the rail has one entry for both.
  it.each([
    ['/monitoring', 'Monitoring'],
    ['/notifications', 'Notifications'],
  ])('puts %s under the dashboard', (pathname, label) => {
    expect(breadcrumbFor(pathname)).toEqual([{ label: 'Dashboard', to: '/' }, { label }]);
  });

  // A job has no page of its own any more, so nothing here knows a project from
  // a run id — the trail goes back to the grid that lists every run.
  it('files a run under the grid rather than guessing a project', () => {
    expect(breadcrumbFor('/runs/abc')).toEqual([
      { label: 'Monitoring', to: '/monitoring' },
      { label: 'Run abc' },
    ]);
  });

  // The order is the point of this trail, and it is the thing a test of *which*
  // crumbs are present would not catch: the project used to sit in front of the
  // section and the branch hung off the file name as a pill.
  const shape = (pathname: string) =>
    breadcrumbFor(pathname).map((c) => c.slot ?? c.label);

  it('narrows left to right: section, project, branch, file', () => {
    expect(shape('/files/default/mine/edit/demo.nb.md'))
      .toEqual(['Files', 'project', 'branch', 'demo.nb.md']);
  });

  it('stops at the project on the shell — the explorer has the branch picker', () => {
    expect(shape('/files/default/mine')).toEqual(['Files', 'project']);
    expect(breadcrumbFor('/files/default/mine')[0].to).toBeUndefined();
  });

  it('takes the editor’s subject from the path, and the branch is a switcher', () => {
    expect(breadcrumbFor('/files/default/mine/edit/demo.nb.md')).toEqual([
      { label: 'Files', to: '/files/default/mine' },
      { label: 'default', slot: 'project' },
      { label: 'mine', slot: 'branch' },
      { label: 'demo.nb.md' },
    ]);
  });

  it('is the same trail whichever way you are reading the file', () => {
    for (const view of ['edit', 'source', 'diff']) {
      expect(shape(`/files/default/mine/${view}/demo.nb.md`))
        .toEqual(['Files', 'project', 'branch', 'demo.nb.md']);
    }
  });

  // Not a view, so not a file: the commit page keeps the trail it can support.
  it('leaves the commit page at the project', () => {
    expect(shape('/files/default/test/commit/a1b2c3d4')).toEqual(['Files', 'project']);
  });

  it('keeps a nested notebook path whole', () => {
    expect(breadcrumbFor('/files/default/test/edit/reports/monthly.nb.md')[3].label)
      .toBe('reports/monthly.nb.md');
  });

  it('keeps the untruncated notebook path for the title attribute', () => {
    const long = 'reporting/monthly/very-long-notebook-name-for-testing.nb.md';
    const crumb = breadcrumbFor(`/files/default/mine/edit/${long}`)[3];
    expect(crumb.full).toBe(long);
    expect(crumb.label).not.toBe(long);
  });

  it('says so when the route is unknown', () => {
    expect(breadcrumbFor('/nowhere')).toEqual([{ label: 'Not found' }]);
  });

  it('names the settings section you are on', () => {
    expect(breadcrumbFor('/settings')).toEqual([{ label: 'Settings' }]);
    expect(breadcrumbFor('/settings/security')).toEqual([
      { label: 'Settings', to: '/settings' },
      { label: 'Security' },
    ]);
  });
});

describe('the connections area', () => {
  it('is one crumb at the top level', () => {
    expect(breadcrumbFor('/connections').map((c) => c.label)).toEqual(['Connections']);
  });

  it('keeps a link back to the list once you are inside one', () => {
    const crumbs = breadcrumbFor('/connections/abc123');
    expect(crumbs.map((c) => c.label)).toEqual(['Connections', 'Query']);
    expect(crumbs[0].to).toBe('/connections');
  });
});
