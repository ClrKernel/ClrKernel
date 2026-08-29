import { describe, expect, it } from 'vitest';
import type { ApiResultSet } from './api';
import {
  NULL_KEY,
  clipboardText,
  compareCells,
  countLabel,
  csvText,
  distinctValues,
  emptyFilter,
  isColumnFiltered,
  isFiltering,
  passes,
  visibleOrder,
  type GridFilter,
} from './resultGrid';

function set(rows: (string | null)[][], types = ['string', 'string']): ApiResultSet {
  return { columns: ['a', 'b'], types, rows, truncated: false };
}

/** A filter with the given fields set over a two-column grid. */
function filter(over: Partial<GridFilter> = {}): GridFilter {
  return { ...emptyFilter(2), ...over };
}

describe('compareCells', () => {
  it('sorts a numeric column numerically', () => {
    expect(compareCells('9', '10', true)).toBeLessThan(0);
    // The same values as text are the bug the column kind exists to avoid.
    expect(compareCells('9', '10', false)).toBeGreaterThan(0);
  });

  it('keeps nulls together at one end', () => {
    expect(compareCells(null, 'a', false)).toBeLessThan(0);
    expect(compareCells('a', null, false)).toBeGreaterThan(0);
    expect(compareCells(null, null, false)).toBe(0);
  });

  it('falls back to text when a numeric column holds something that is not a number', () => {
    expect(compareCells('n/a', '10', true)).toBeGreaterThan(0);
  });
});

describe('the global filter', () => {
  const rows = set([['alpha', 'one'], ['beta', 'two'], ['gamma', 'ONE']]);

  it('matches any column, case-insensitively', () => {
    expect(visibleOrder(rows, filter({ text: 'one' }), null)).toEqual([0, 2]);
  });

  it('ignores surrounding space, so a stray one does not empty the grid', () => {
    expect(visibleOrder(rows, filter({ text: '  beta  ' }), null)).toEqual([1]);
  });

  it('never matches a null, because a null contains nothing', () => {
    const withNull = set([[null, 'x'], ['a', 'y']]);
    expect(visibleOrder(withNull, filter({ text: 'a' }), null)).toEqual([1]);
  });
});

describe('a column filter', () => {
  const rows = set([['alpha', 'one'], ['beta', 'one'], ['gamma', 'two']]);

  it('applies to its own column only', () => {
    expect(visibleOrder(rows, filter({ columns: ['a', ''] }), null)).toEqual([0, 1, 2]);
    expect(visibleOrder(rows, filter({ columns: ['al', ''] }), null)).toEqual([0]);
  });

  it('combines with another column by AND', () => {
    expect(visibleOrder(rows, filter({ columns: ['a', 'one'] }), null)).toEqual([0, 1]);
  });

  it('combines with the global filter by AND', () => {
    expect(visibleOrder(rows, filter({ text: 'two', columns: ['gam', ''] }), null)).toEqual([2]);
    expect(visibleOrder(rows, filter({ text: 'two', columns: ['alp', ''] }), null)).toEqual([]);
  });

  it('treats a null cell as empty, so filtering a column hides its nulls', () => {
    const withNull = set([[null, 'x'], ['a', 'y']]);
    expect(visibleOrder(withNull, filter({ columns: ['a', ''] }), null)).toEqual([1]);
  });
});

describe('the value picker', () => {
  const rows = set([['alpha', 'one'], [null, 'two'], ['beta', 'three'], ['alpha', 'four']]);

  it('offers every distinct value once, null first', () => {
    expect(distinctValues(rows, 0)).toEqual([NULL_KEY, 'alpha', 'beta']);
  });

  it('sorts a numeric column as numbers', () => {
    const numbers = set([['10', 'x'], ['9', 'y'], ['100', 'z']], ['number', 'string']);
    expect(distinctValues(numbers, 0)).toEqual(['9', '10', '100']);
  });

  it('offers values the current filter hides, or you could never widen it back', () => {
    // Only 'beta' is selected, and 'alpha' is still on offer.
    const narrowed = filter({ selected: [new Set(['beta']), null] });
    expect(visibleOrder(rows, narrowed, null)).toEqual([2]);
    expect(distinctValues(rows, 0)).toContain('alpha');
  });

  it('keeps a chosen null distinct from the text "null"', () => {
    const literal = set([[null, 'x'], ['null', 'y']]);
    expect(visibleOrder(literal, filter({ selected: [new Set([NULL_KEY]), null] }), null))
      .toEqual([0]);
    expect(visibleOrder(literal, filter({ selected: [new Set(['null']), null] }), null))
      .toEqual([1]);
  });

  it('shows nothing when every value is unticked', () => {
    expect(visibleOrder(rows, filter({ selected: [new Set(), null] }), null)).toEqual([]);
  });
});

describe('what the funnel and the Clear button look at', () => {
  it('is lit by a text filter or a selection, on that column alone', () => {
    expect(isColumnFiltered(filter({ columns: ['x', ''] }), 0)).toBe(true);
    expect(isColumnFiltered(filter({ columns: ['x', ''] }), 1)).toBe(false);
    expect(isColumnFiltered(filter({ selected: [new Set(['a']), null] }), 0)).toBe(true);
    expect(isColumnFiltered(filter(), 0)).toBe(false);
  });

  it('is not lit by the global filter, which belongs to no column', () => {
    expect(isColumnFiltered(filter({ text: 'x' }), 0)).toBe(false);
    expect(isFiltering(filter({ text: 'x' }))).toBe(true);
  });

  it('says nothing is filtered when nothing is', () => {
    expect(isFiltering(filter())).toBe(false);
    expect(isFiltering(filter({ text: '   ' }))).toBe(false);
  });
});

describe('filtering and sorting together', () => {
  it('sorts what survived, not what was there', () => {
    const rows = set([['3', 'keep'], ['1', 'drop'], ['2', 'keep']], ['number', 'string']);
    expect(visibleOrder(rows, filter({ columns: ['', 'keep'] }), { column: 0, direction: 1 }))
      .toEqual([2, 0]);
  });

  it('leaves the rows themselves alone', () => {
    const rows = set([['b', '1'], ['a', '2']]);
    visibleOrder(rows, filter(), { column: 0, direction: 1 });
    expect(rows.rows[0][0]).toBe('b');
  });
});

describe('countLabel', () => {
  const ten = set(Array.from({ length: 10 }, (_, i) => [String(i), 'x']));

  it('counts plainly when nothing is filtered', () => {
    expect(countLabel(ten, 10)).toBe('10 rows');
  });

  it('says how many of how many once something is', () => {
    expect(countLabel(ten, 3)).toBe('3 of 10 rows');
  });

  it('never claims a total the server did not measure', () => {
    const capped = { ...ten, truncated: true };
    expect(countLabel(capped, 10)).toBe('first 10 rows — the cap stopped it there');
    expect(countLabel(capped, 3)).toBe('3 of the first 10 rows');
  });

  it('gets the singular right', () => {
    expect(countLabel(ten, 1)).toBe('1 of 10 row');
  });
});

describe('passes', () => {
  it('is true for everything when no filter is set', () => {
    expect(passes(['anything', null], filter())).toBe(true);
  });
});

describe('csvText', () => {
  it('quotes every field and doubles embedded quotes', () => {
    const rows = set([['say "hi"', 'plain']]);
    expect(csvText(rows, [0])).toBe('"a","b"\r\n"say ""hi""","plain"');
  });

  it('survives a comma and a newline inside a value', () => {
    const rows = set([['one, two', 'line\nbreak']]);
    expect(csvText(rows, [0])).toBe('"a","b"\r\n"one, two","line\nbreak"');
  });

  it('writes NULL as an empty unquoted field, so it stays distinct from an empty string', () => {
    const rows = set([[null, '']]);
    expect(csvText(rows, [0])).toBe('"a","b"\r\n,""');
  });

  it('writes the rows in display order', () => {
    const rows = set([['b', '1'], ['a', '2']]);
    expect(csvText(rows, [1, 0])).toContain('"a","2"\r\n"b","1"');
  });

  it('exports what the filter left, because that is what is on screen', () => {
    const rows = set([['keep', '1'], ['drop', '2']]);
    const order = visibleOrder(rows, filter({ columns: ['keep', ''] }), null);
    expect(csvText(rows, order)).toBe('"a","b"\r\n"keep","1"');
  });
});

describe('clipboardText', () => {
  it('is tab separated with a header row', () => {
    expect(clipboardText(set([['x', 'y']]), [0])).toBe('a\tb\nx\ty');
  });

  it('writes a null as nothing rather than as the word null', () => {
    expect(clipboardText(set([[null, 'y']]), [0])).toBe('a\tb\n\ty');
  });
});
