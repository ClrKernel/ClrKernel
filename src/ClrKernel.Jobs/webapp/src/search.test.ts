import { describe, expect, it } from 'vitest';
import { matchesQuery, showsSearch, withQuery } from './search';

describe('matchesQuery', () => {
  it('keeps everything when the query is empty or blank', () => {
    expect(matchesQuery('', 'anything')).toBe(true);
    expect(matchesQuery('   ', 'anything')).toBe(true);
  });

  it('matches across fields and ignores order', () => {
    expect(matchesQuery('daily prod', 'daily-load', 'prod')).toBe(true);
    expect(matchesQuery('prod daily', 'daily-load', 'prod')).toBe(true);
  });

  it('requires every term', () => {
    expect(matchesQuery('daily missing', 'daily-load', 'prod')).toBe(false);
  });

  it('ignores case and empty fields', () => {
    expect(matchesQuery('DAILY', 'daily-load', null, undefined)).toBe(true);
  });
});

describe('withQuery', () => {
  it('adds, replaces and removes q', () => {
    expect(withQuery('', 'load')).toBe('?q=load');
    expect(withQuery('?q=old', 'new')).toBe('?q=new');
    expect(withQuery('?q=old', '')).toBe('');
  });

  /** The editor keeps its subject in ?path=; clobbering it would close the file. */
  it('leaves other parameters alone', () => {
    expect(withQuery('?path=a%2Fb.nb.md', 'x')).toBe('?path=a%2Fb.nb.md&q=x');
    expect(withQuery('?path=a%2Fb.nb.md&q=x', '')).toBe('?path=a%2Fb.nb.md');
  });
});

describe('showsSearch', () => {
  it('is on the two pages it filters and nowhere else', () => {
    expect(showsSearch('/')).toBe(true);
    expect(showsSearch('/jobs')).toBe(true);
    expect(showsSearch('/edit')).toBe(false);
    expect(showsSearch('/settings')).toBe(false);
  });
});
