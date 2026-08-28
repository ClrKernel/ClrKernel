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
    ['/files/default', 'Files'],
    ['/channels', 'Channels'],
    ['/settings', 'Settings'],
  ])('%s is a single crumb', (pathname, label) => {
    expect(breadcrumbFor(pathname)).toEqual([{ label }]);
  });

  // Monitoring is a view of the Dashboard, not a section, and the trail is where
  // that is said — the rail has one entry for both.
  it('puts monitoring under the dashboard', () => {
    expect(breadcrumbFor('/monitoring')).toEqual([
      { label: 'Dashboard', to: '/' },
      { label: 'Monitoring' },
    ]);
  });

  // A job has no page of its own any more, so nothing here knows a project from
  // a run id — the trail goes back to the grid that lists every run.
  it('files a run under the grid rather than guessing a project', () => {
    expect(breadcrumbFor('/runs/abc')).toEqual([
      { label: 'Monitoring', to: '/monitoring' },
      { label: 'Run abc' },
    ]);
  });

  it('takes the editor’s subject from the path, and its badge is the branch switcher', () => {
    expect(breadcrumbFor('/files/default/edit/mine/demo.nb.md')).toEqual([
      { label: 'Files', to: '/files/default' },
      { label: 'demo.nb.md', badge: 'branch' },
    ]);
  });

  it('is the same trail whichever way you are reading the file', () => {
    for (const view of ['edit', 'source', 'diff']) {
      expect(breadcrumbFor(`/files/default/${view}/mine/demo.nb.md`)).toEqual([
        { label: 'Files', to: '/files/default' },
        { label: 'demo.nb.md', badge: 'branch' },
      ]);
    }
  });

  it('keeps a nested notebook path whole', () => {
    expect(breadcrumbFor('/files/default/edit/test/reports/monthly.nb.md')[1].label)
      .toBe('reports/monthly.nb.md');
  });

  it('keeps the untruncated notebook path for the title attribute', () => {
    const long = 'reporting/monthly/very-long-notebook-name-for-testing.nb.md';
    const crumb = breadcrumbFor(`/files/default/edit/mine/${long}`)[1];
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
