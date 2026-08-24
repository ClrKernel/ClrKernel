import { useMemo, useRef, useState } from 'react';
import { Button } from '@/components/ui/button';
import type { ApiResultSet } from '../api';
import { clipboardText, csvText, sortedOrder } from '../resultGrid';

/** How many rows are in the DOM at once. Everything else is padding. */
const WINDOW = 80;

/**
 * Row height in px. Fixed, because a virtualized list has to know where a row is
 * without measuring it — and a results grid has no reason to have ragged rows.
 *
 * `.result-grid-scroll td` in styles.css states the same number and derives its
 * line height from it rather than from padding. The two have to agree: an explicit
 * height on a row is a minimum, not a clamp, so a row that renders a fraction
 * taller than this makes the window drift against scrollTop and rows skip as you
 * scroll.
 */
const ROW_HEIGHT = 26;

type Sort = { column: number; direction: 1 | -1 } | null;

/**
 * A result set as a grid: click a header to sort, copy with headers, save as CSV.
 *
 * Its own component rather than the kernel's `InteractiveTable`: that grid is
 * server-rendered HTML built from a `DisplayTable`, and this page has JSON rows,
 * needs virtualization for ten thousand of them, and needs a copy and a CSV that
 * an HTML string cannot give it.
 *
 * Only a window of rows is in the DOM. Ten thousand rows × a dozen columns is a
 * hundred and twenty thousand cells, and a browser asked to lay that out stops
 * being a browser for a while.
 */
export function ResultGrid({ set }: { set: ApiResultSet }) {
  const [sort, setSort] = useState<Sort>(null);
  const [top, setTop] = useState(0);
  const scroller = useRef<HTMLDivElement | null>(null);

  // Row *indices* are sorted, not the rows: the rows are the big array and this
  // way sorting never copies them.
  const order = useMemo(() => sortedOrder(set, sort), [set, sort]);

  const first = Math.max(0, Math.floor(top / ROW_HEIGHT) - 10);
  const visible = order.slice(first, first + WINDOW);

  function toggle(column: number) {
    setSort((current) =>
      current?.column === column
        ? current.direction === 1 ? { column, direction: -1 } : null
        : { column, direction: 1 });
    scroller.current?.scrollTo({ top: 0 });
  }

  return (
    <div className="result-grid">
      <div className="result-grid-toolbar">
        {/* No total when it was capped. Knowing one costs a second query, and
            "first N" is what was actually measured. */}
        <span className="text-sm text-muted-subtle">
          {set.truncated ? 'first ' : ''}
          {set.rows.length.toLocaleString()} row{set.rows.length === 1 ? '' : 's'}
          {set.truncated ? ' — the cap stopped it there' : ''}
        </span>
        <span className="spacer" />
        <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
          onClick={() => copy(clipboardText(set, order))} title="Copy every row, with headers">
          Copy
        </Button>
        <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
          onClick={() => download(set, order)} title="Save every row as CSV">
          CSV
        </Button>
      </div>

      <div className="result-grid-scroll" ref={scroller} onScroll={(e) => setTop(e.currentTarget.scrollTop)}>
        <table>
          <thead>
            <tr>
              <th className="result-grid-gutter" aria-label="Row number" />
              {set.columns.map((column, i) => (
                <th key={i} onClick={() => toggle(i)} title="Sort by this column">
                  {column}
                  <span aria-hidden="true">
                    {sort?.column === i ? (sort.direction === 1 ? ' ▲' : ' ▼') : ''}
                  </span>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {/* The rows above and below the window exist as height, so the
                scrollbar is the size it would be if they were all rendered. */}
            {first > 0 && (
              <tr style={{ height: first * ROW_HEIGHT }} aria-hidden="true">
                <td colSpan={set.columns.length + 1} />
              </tr>
            )}
            {visible.map((row, i) => (
              <tr key={row} style={{ height: ROW_HEIGHT }}>
                <td className="result-grid-gutter">{first + i + 1}</td>
                {set.rows[row].map((cell, c) => (
                  <td key={c} className={cellClass(set.types[c], cell)} title={cell ?? 'NULL'}>
                    {cell == null ? <span className="result-grid-null">NULL</span> : cell}
                  </td>
                ))}
              </tr>
            ))}
            {first + WINDOW < order.length && (
              <tr style={{ height: (order.length - first - WINDOW) * ROW_HEIGHT }} aria-hidden="true">
                <td colSpan={set.columns.length + 1} />
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

/** NULL and an empty string look identical in a grid and mean entirely different
 *  things, so NULL says so and is styled apart. */
function cellClass(type: string, value: string | null): string {
  return [type === 'number' ? 'result-grid-number' : '', value == null ? 'result-grid-nullish' : '']
    .filter(Boolean).join(' ');
}

function copy(value: string) {
  void navigator.clipboard.writeText(value);
}

function download(set: ApiResultSet, order: number[]) {
  // A blob url rather than a data: one — a data url of ten thousand rows runs
  // into the browser's url length limit and silently produces nothing.
  const url = URL.createObjectURL(new Blob([csvText(set, order)], { type: 'text/csv;charset=utf-8' }));
  const link = document.createElement('a');
  link.href = url;
  link.download = 'results.csv';
  link.click();
  URL.revokeObjectURL(url);
}
