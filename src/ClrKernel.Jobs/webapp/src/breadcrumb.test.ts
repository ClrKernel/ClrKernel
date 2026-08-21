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
    ['/jobs', 'Jobs'],
    ['/notebooks', 'Notebooks'],
    ['/channels', 'Channels'],
    ['/settings', 'Settings'],
  ])('%s is a single crumb', (pathname, label) => {
    expect(breadcrumbFor(pathname)).toEqual([{ label }]);
  });

  it('puts the job under Jobs and carries its environment as a badge', () => {
    expect(breadcrumbFor('/jobs/dev/nightly')).toEqual([
      { label: 'Jobs', to: '/jobs' },
      { label: 'nightly', badge: 'dev' },
    ]);
  });

  it('names an unsaved job rather than showing the literal route segment', () => {
    expect(breadcrumbFor('/jobs/prod/new')[1].label).toBe('New job');
  });

  it('decodes an escaped job name', () => {
    expect(breadcrumbFor('/jobs/dev/nightly%20run')[1].label).toBe('nightly run');
  });

  it('takes the notebook editor’s subject from ?path=, and it is always dev', () => {
    expect(breadcrumbFor('/edit', '?path=demo.nb.md')).toEqual([
      { label: 'Notebooks', to: '/notebooks' },
      { label: 'demo.nb.md', badge: 'dev' },
    ]);
  });

  it('keeps the untruncated notebook path for the title attribute', () => {
    const long = 'reporting/monthly/very-long-notebook-name-for-testing.nb.md';
    const crumb = breadcrumbFor('/edit', `?path=${long}`)[1];
    expect(crumb.full).toBe(long);
    expect(crumb.label).not.toBe(long);
  });

  it('files a run under Jobs', () => {
    expect(breadcrumbFor('/runs/42')).toEqual([
      { label: 'Jobs', to: '/jobs' },
      { label: 'Run 42' },
    ]);
  });

  it('says so when the route is unknown', () => {
    expect(breadcrumbFor('/nowhere')).toEqual([{ label: 'Not found' }]);
  });
});
