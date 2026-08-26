import { beforeEach, describe, expect, it } from 'vitest';
import { dropSubtree, readCache, without, writeCache } from './connectionCache';

const empty = { children: {}, details: {}, open: [] };

describe('the object-tree cache', () => {
  beforeEach(() => writeCache(empty));

  it('survives being read back, which is the whole point of it', () => {
    writeCache({ children: { 'c:1': [] }, details: {}, open: ['c:1'] });
    expect(readCache().open).toEqual(['c:1']);
  });

  it('forgets a node and everything under it', () => {
    writeCache({
      children: { 'c:1': [], 'c:1/d:a': [], 'c:2': [] },
      details: { 'c:1/d:a/o:T': { columns: [], keys: [], indexes: [] } },
      open: ['c:1', 'c:1/d:a', 'c:2'],
    });
    dropSubtree('c:1');

    expect(Object.keys(readCache().children)).toEqual(['c:2']);
    expect(readCache().details).toEqual({});
    expect(readCache().open).toEqual(['c:2']);
  });

  it('leaves a sibling alone even when its name starts the same way', () => {
    // Refreshing the schema Sales must not throw away SalesArchive with it.
    writeCache({
      children: { 'c:1/d:a/s:Sales': [], 'c:1/d:a/s:SalesArchive': [] },
      details: {},
      open: ['c:1/d:a/s:Sales', 'c:1/d:a/s:SalesArchive'],
    });
    dropSubtree('c:1/d:a/s:Sales');

    expect(Object.keys(readCache().children)).toEqual(['c:1/d:a/s:SalesArchive']);
    expect(readCache().open).toEqual(['c:1/d:a/s:SalesArchive']);
  });
});

describe('without', () => {
  it('keeps everything outside the subtree', () => {
    expect(without({ 'a': 1, 'a/x': 2, 'b': 3 }, 'a')).toEqual({ b: 3 });
  });

  it('does not treat a longer sibling name as a child', () => {
    expect(without({ 'ab': 1, 'a/x': 2 }, 'a')).toEqual({ ab: 1 });
  });

  it('is a copy, not a mutation', () => {
    const original = { a: 1, b: 2 };
    expect(without(original, 'a')).toEqual({ b: 2 });
    expect(original).toEqual({ a: 1, b: 2 });
  });
});
