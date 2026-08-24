/**
 * The parts of the results grid that are not React: how rows sort, and what
 * lands on the clipboard or in a CSV. Separate so they can be tested — the
 * module's stated rule everywhere else in here — and because a quoting bug in a
 * CSV is silent until somebody opens the file a week later.
 */

import type { ApiResultSet } from './api';

/**
 * Orders two cells of one column.
 *
 * Nulls sort together at one end rather than scattering through the order, and a
 * numeric column compares numerically — `10` after `9`, which is the whole reason
 * the server sends a column kind at all.
 */
export function compareCells(a: string | null, b: string | null, numeric: boolean): number {
  if (a == null || b == null) {
    return a == null ? (b == null ? 0 : -1) : 1;
  }
  if (numeric) {
    const left = Number(a);
    const right = Number(b);
    if (!Number.isNaN(left) && !Number.isNaN(right)) {
      return left - right;
    }
  }
  return a.localeCompare(b);
}

/** Row indices in display order. Indices, not rows: the rows are the big array
 *  and sorting them would copy it. */
export function sortedOrder(
  set: ApiResultSet,
  sort: { column: number; direction: 1 | -1 } | null,
): number[] {
  const indices = set.rows.map((_, i) => i);
  if (sort == null) {
    return indices;
  }
  const numeric = set.types[sort.column] === 'number';
  return indices.sort((a, b) =>
    compareCells(set.rows[a][sort.column], set.rows[b][sort.column], numeric) * sort.direction);
}

/** Tab-separated with a header row — what a spreadsheet takes from the clipboard. */
export function clipboardText(set: ApiResultSet, order: number[]): string {
  return [
    set.columns.join('\t'),
    ...order.map((r) => set.rows[r].map((c) => c ?? '').join('\t')),
  ].join('\n');
}

/**
 * RFC 4180 CSV: every field quoted, embedded quotes doubled, CRLF between rows.
 *
 * Quoting everything rather than only the fields that need it — a value with a
 * comma, a newline or a quote in it is exactly the value nobody tests with, and
 * unconditional quoting has no case left to get wrong. A NULL is an empty
 * *unquoted* field, which is how it stays distinguishable from an empty string.
 */
export function csvText(set: ApiResultSet, order: number[]): string {
  const quote = (value: string | null) =>
    value == null ? '' : `"${value.replace(/"/g, '""')}"`;
  return [
    set.columns.map((c) => quote(c)).join(','),
    ...order.map((r) => set.rows[r].map(quote).join(',')),
  ].join('\r\n');
}
