/**
 * The parts of the results grid that are not React: how rows sort, how they
 * filter, and what lands on the clipboard or in a CSV. Separate so they can be
 * tested — the module's stated rule everywhere else in here — and because a
 * quoting bug in a CSV is silent until somebody opens the file a week later.
 *
 * The filter semantics are deliberately the notebook grid's, down to the details:
 * `InteractiveTable` in `ClrKernel.Formatting.Html` is what people already use to
 * read a `#!sql` cell's output, and a grid that filtered *nearly* the same way
 * would be worse than one that filtered differently — you would only find out
 * where it diverged by being surprised by it.
 */

import type { ApiResultSet } from './api';

/**
 * How a null is keyed in a distinct-value selection. A sentinel rather than
 * `null`, because the selection is a `Set<string>` and an actual data value of
 * `"null"` has to stay distinguishable from the absence of a value. Same sentinel
 * the notebook grid uses, for the same reason.
 */
export const NULL_KEY = '\u0000null';

export interface GridFilter {
  /** Matches against every column at once. A row passes if any cell contains it. */
  text: string;
  /** Per column: a substring, or '' for no filter on that column. */
  columns: string[];
  /** Per column: the allowed values, or null for "all of them". */
  selected: (Set<string> | null)[];
}

export type GridSort = { column: number; direction: 1 | -1 } | null;

export function emptyFilter(columnCount: number): GridFilter {
  return {
    text: '',
    columns: Array.from({ length: columnCount }, () => ''),
    selected: Array.from({ length: columnCount }, () => null),
  };
}

/** Whether anything at all is being filtered out. */
export function isFiltering(filter: GridFilter): boolean {
  return filter.text.trim().length > 0
    || filter.columns.some((c) => c.length > 0)
    || filter.selected.some((s) => s != null);
}

/** Whether one column carries a filter — what lights its funnel up. */
export function isColumnFiltered(filter: GridFilter, column: number): boolean {
  return (filter.columns[column] ?? '').length > 0 || filter.selected[column] != null;
}

export function valueKey(value: string | null): string {
  return value == null ? NULL_KEY : value;
}

/**
 * Whether a row survives every filter. They combine with AND: narrowing one
 * column never widens what another one is hiding.
 */
export function passes(row: (string | null)[], filter: GridFilter): boolean {
  const global = filter.text.trim().toLowerCase();
  if (global.length > 0
    && !row.some((cell) => cell != null && cell.toLowerCase().includes(global))) {
    return false;
  }
  for (let i = 0; i < row.length; i++) {
    const needle = filter.columns[i];
    // A null cell is matched as the empty string, so typing anything at all in a
    // column's box hides that column's nulls — which is what filtering it means.
    if (needle && !(row[i] ?? '').toLowerCase().includes(needle.toLowerCase())) {
      return false;
    }
    const allowed = filter.selected[i];
    if (allowed != null && !allowed.has(valueKey(row[i]))) {
      return false;
    }
  }
  return true;
}

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

/**
 * Row indices in display order — filtered, then sorted.
 *
 * Indices, not rows: the rows are the big array and sorting or filtering them
 * would copy it.
 */
export function visibleOrder(set: ApiResultSet, filter: GridFilter, sort: GridSort): number[] {
  const indices: number[] = [];
  for (let i = 0; i < set.rows.length; i++) {
    if (passes(set.rows[i], filter)) {
      indices.push(i);
    }
  }
  if (sort == null) {
    return indices;
  }
  const numeric = set.types[sort.column] === 'number';
  return indices.sort((a, b) =>
    compareCells(set.rows[a][sort.column], set.rows[b][sort.column], numeric) * sort.direction);
}

/**
 * The distinct values of one column, in sort order, with null first.
 *
 * Over every loaded row rather than over the ones the current filter leaves: a
 * picker that only offered surviving values could never be used to widen a
 * selection, and unticking your way into a corner with no way back out is a worse
 * fate than a long list.
 */
export function distinctValues(set: ApiResultSet, column: number): string[] {
  const seen = new Set<string>();
  for (const row of set.rows) {
    seen.add(valueKey(row[column]));
  }
  const numeric = set.types[column] === 'number';
  return [...seen].sort((a, b) => {
    if (a === NULL_KEY || b === NULL_KEY) {
      return a === NULL_KEY ? (b === NULL_KEY ? 0 : -1) : 1;
    }
    return compareCells(a, b, numeric);
  });
}

/**
 * What the toolbar says about how many rows there are.
 *
 * Never a total the server did not measure: when the cap stopped the read short
 * there is no "of M" to give, because knowing M costs a second query.
 */
export function countLabel(set: ApiResultSet, visible: number): string {
  const rows = `row${visible === 1 ? '' : 's'}`;
  const loaded = set.rows.length;
  if (visible === loaded) {
    return set.truncated
      ? `first ${loaded.toLocaleString()} ${rows} — the cap stopped it there`
      : `${loaded.toLocaleString()} ${rows}`;
  }
  return set.truncated
    ? `${visible.toLocaleString()} of the first ${loaded.toLocaleString()} ${rows}`
    : `${visible.toLocaleString()} of ${loaded.toLocaleString()} ${rows}`;
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
