import { describe, expect, it } from 'vitest';
import type { ApiResultSet } from './api';
import { clipboardText, compareCells, csvText, sortedOrder } from './resultGrid';

function set(rows: (string | null)[][], types = ['string', 'string']): ApiResultSet {
  return { columns: ['a', 'b'], types, rows, truncated: false };
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

describe('sortedOrder', () => {
  it('is the natural order until a column is chosen', () => {
    expect(sortedOrder(set([['b', '1'], ['a', '2']]), null)).toEqual([0, 1]);
  });

  it('sorts by index rather than moving rows', () => {
    const rows = set([['b', '1'], ['a', '2'], ['c', '3']]);
    expect(sortedOrder(rows, { column: 0, direction: 1 })).toEqual([1, 0, 2]);
    expect(sortedOrder(rows, { column: 0, direction: -1 })).toEqual([2, 0, 1]);
    expect(rows.rows[0][0]).toBe('b');
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
});

describe('clipboardText', () => {
  it('is tab separated with a header row', () => {
    expect(clipboardText(set([['x', 'y']]), [0])).toBe('a\tb\nx\ty');
  });

  it('writes a null as nothing rather than as the word null', () => {
    expect(clipboardText(set([[null, 'y']]), [0])).toBe('a\tb\n\ty');
  });
});
