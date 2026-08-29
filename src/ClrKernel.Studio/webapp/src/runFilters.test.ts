import { describe, expect, it } from 'vitest';
import {
  activeFilters, dayAfter, dayStart, fromSearch, NO_FILTERS, runsQuery, sortBy, toSearch,
  type RunFilters,
} from './runFilters';

describe('runsQuery', () => {
  it('sends only the filters that are set', () => {
    const query = new URLSearchParams(runsQuery(NO_FILTERS));
    expect([...query.keys()].sort()).toEqual(['limit', 'sort']);
  });

  it('carries every filter to the server', () => {
    const filters: RunFilters = {
      ...NO_FILTERS,
      project: 'etl',
      env: 'prod',
      status: 'Failed',
      trigger: 'Manual',
      job: 'nightly',
      path: 'etl/nightly.nb.md',
      actor: 'a1b2',
    };
    const query = new URLSearchParams(runsQuery(filters));
    expect(query.get('project')).toBe('etl');
    expect(query.get('status')).toBe('Failed');
    expect(query.get('path')).toBe('etl/nightly.nb.md');
    expect(query.get('actor')).toBe('a1b2');
  });

  it('pages by offset, not by a cursor the server does not have', () => {
    expect(new URLSearchParams(runsQuery({ ...NO_FILTERS, page: 2 }, 50)).get('offset')).toBe('100');
    expect(new URLSearchParams(runsQuery(NO_FILTERS, 50)).get('offset')).toBeNull();
  });
});

describe('the date range', () => {
  // A day the reader picked is that whole day where they are, so the upper bound
  // is the midnight *after* it — an exclusive bound one day on, not the same
  // instant as the lower one, which would select nothing.
  it('covers the whole of the day that was picked', () => {
    expect(dayStart('2026-03-04')).toBe(new Date('2026-03-04T00:00:00').toISOString());
    expect(dayAfter('2026-03-04')).toBe(new Date('2026-03-05T00:00:00').toISOString());
    expect(new Date(dayAfter('2026-03-04')!).getTime())
      .toBeGreaterThan(new Date(dayStart('2026-03-04')!).getTime());
  });

  it('is an instant, not a date, because the server compares in UTC', () => {
    expect(dayStart('2026-03-04')).toMatch(/Z$/);
  });

  it('ignores something that is not a date rather than sending NaN', () => {
    expect(dayStart('not-a-date')).toBeNull();
    expect(new URLSearchParams(runsQuery({ ...NO_FILTERS, from: 'nonsense' })).get('since')).toBeNull();
  });
});

describe('sortBy', () => {
  it('sorts a new column descending and flips the one already sorted', () => {
    const byJob = sortBy(NO_FILTERS, 'jobName');
    expect(byJob).toMatchObject({ sort: 'jobName', asc: false });
    expect(sortBy(byJob, 'jobName')).toMatchObject({ sort: 'jobName', asc: true });
    expect(sortBy(sortBy(byJob, 'jobName'), 'status')).toMatchObject({ sort: 'status', asc: false });
  });

  // Page 4 of one order is not page 4 of another: rows the reader never saw
  // would be skipped, and rows they did would repeat.
  it('goes back to the first page', () => {
    expect(sortBy({ ...NO_FILTERS, page: 3 }, 'status').page).toBe(0);
  });
});

describe('the address bar', () => {
  it('round-trips every filter, so a filtered grid is a link', () => {
    const filters: RunFilters = {
      project: 'etl', env: 'prod', status: 'Failed', trigger: 'Schedule',
      job: 'nightly', path: 'a.nb.md', actor: 'a1b2',
      from: '2026-03-01', to: '2026-03-04',
      sort: 'status', asc: true, page: 2,
    };
    expect(fromSearch(toSearch(filters))).toEqual(filters);
  });

  it('is empty when nothing is filtered', () => {
    expect(toSearch(NO_FILTERS)).toBe('');
    expect(fromSearch('')).toEqual(NO_FILTERS);
  });

  // The sort reaches an ORDER BY on the server, which rejects what it does not
  // know — so a hand-edited URL falls back rather than costing a round trip.
  it('refuses a sort column the server would reject', () => {
    expect(fromSearch('?sort=drop%20table').sort).toBe('started');
  });

  it('lists what is narrowing the view, for the chips', () => {
    expect(activeFilters({ ...NO_FILTERS, status: 'Failed', job: 'nightly' }))
      .toEqual([{ key: 'status', value: 'Failed' }, { key: 'job', value: 'nightly' }]);
    expect(activeFilters({ ...NO_FILTERS, sort: 'status', asc: true })).toEqual([]);
  });
});
