import { useEffect, useMemo, useRef, useState } from 'react';
import { Filter, X } from 'lucide-react';
import { Button } from '@/components/ui/button';
import type { ApiResultSet } from '../api';
import {
  NULL_KEY,
  clipboardText,
  countLabel,
  csvText,
  distinctValues,
  emptyFilter,
  isColumnFiltered,
  isFiltering,
  visibleOrder,
  type GridFilter,
  type GridSort,
} from '../resultGrid';

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

/**
 * A result set as a grid: click a header to sort, filter it a column at a time,
 * copy with headers, save as CSV.
 *
 * Its own component rather than the kernel's `InteractiveTable`: that grid is
 * server-rendered HTML built from a `DisplayTable`, and this page has JSON rows,
 * needs virtualization for ten thousand of them, and needs a copy and a CSV that
 * an HTML string cannot give it. The *behaviour* is that grid's, though — the
 * filter rules live in `resultGrid.ts` and match it deliberately, because this is
 * the same act as reading a `#!sql` cell's output and it should not feel like a
 * different tool.
 */
export function ResultGrid({ set }: { set: ApiResultSet }) {
  const [sort, setSort] = useState<GridSort>(null);
  const [filter, setFilter] = useState<GridFilter>(() => emptyFilter(set.columns.length));
  const [top, setTop] = useState(0);
  const [picker, setPicker] = useState<number | null>(null);
  const scroller = useRef<HTMLDivElement | null>(null);

  // A new result set is a new grid: keeping the previous one's filters would hide
  // rows of a query nobody has filtered yet.
  useEffect(() => {
    setFilter(emptyFilter(set.columns.length));
    setSort(null);
    setPicker(null);
    scroller.current?.scrollTo({ top: 0 });
  }, [set]);

  const order = useMemo(() => visibleOrder(set, filter, sort), [set, filter, sort]);

  const first = Math.max(0, Math.floor(top / ROW_HEIGHT) - 10);
  const visible = order.slice(first, first + WINDOW);

  /** Any change to what is shown starts again from the top — a scroll position
   *  two thousand rows down means nothing once four hundred rows are left. */
  function change(next: GridFilter | ((current: GridFilter) => GridFilter)) {
    setFilter(next);
    setTop(0);
    scroller.current?.scrollTo({ top: 0 });
  }

  function toggleSort(column: number) {
    setSort((current) =>
      current?.column === column
        ? current.direction === 1 ? { column, direction: -1 } : null
        : { column, direction: 1 });
    setTop(0);
    scroller.current?.scrollTo({ top: 0 });
  }

  return (
    <div className="result-grid">
      <div className="result-grid-toolbar">
        <input
          type="search"
          value={filter.text}
          onChange={(e) => change((current) => ({ ...current, text: e.target.value }))}
          placeholder="Filter all columns…"
          aria-label="Filter all columns"
          className="result-grid-search"
        />
        {isFiltering(filter) && (
          <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
            onClick={() => change(emptyFilter(set.columns.length))}
            title="Clear every filter">
            Clear
          </Button>
        )}
        <span className="text-sm text-muted-subtle">{countLabel(set, order.length)}</span>
        <span className="spacer" />
        <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
          onClick={() => copy(clipboardText(set, order))}
          title="Copy the rows you can see, with headers">
          Copy
        </Button>
        <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
          onClick={() => download(set, order)} title="Save the rows you can see as CSV">
          CSV
        </Button>
      </div>

      <div className="result-grid-scroll" ref={scroller} onScroll={(e) => setTop(e.currentTarget.scrollTop)}>
        <table>
          <thead>
            <tr className="result-grid-head">
              <th className="result-grid-gutter" aria-label="Row number" />
              {set.columns.map((column, i) => (
                // The whole heading sorts, not just the words in it — a click in
                // the padding of a wide column meaning nothing is the kind of dead
                // spot you only notice by it not working. The label stays a button
                // so it is still reachable from the keyboard; it has no handler of
                // its own, and its click bubbles to here for one toggle rather than
                // two. The funnel stops its own click for the same reason.
                <th key={i} onClick={() => toggleSort(i)} title={`Sort by ${column}`}>
                  <button className="result-grid-sort">
                    {column}
                    <span aria-hidden="true">
                      {sort?.column === i ? (sort.direction === 1 ? ' ▲' : ' ▼') : ''}
                    </span>
                  </button>
                  <FunnelButton
                    column={column}
                    active={isColumnFiltered(filter, i)}
                    open={picker === i}
                    onToggle={() => setPicker((current) => (current === i ? null : i))}
                  />
                </th>
              ))}
            </tr>
            {/* A box under each heading, the way the notebook grid does it: the
                common case is "show me the rows where this column contains x",
                and making that a popup would be two clicks for one keystroke. */}
            <tr className="result-grid-filters">
              <th className="result-grid-gutter" />
              {set.columns.map((column, i) => (
                <th key={i}>
                  <input
                    value={filter.columns[i]}
                    onChange={(e) => change((current) => ({
                      ...current,
                      columns: current.columns.map((c, j) => (j === i ? e.target.value : c)),
                    }))}
                    placeholder="filter"
                    aria-label={`Filter ${column}`}
                    // The row above sorts on click; this one is for typing in.
                    onClick={(e) => e.stopPropagation()}
                  />
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
            {order.length === 0 && (
              <tr>
                <td colSpan={set.columns.length + 1} className="result-grid-empty">
                  No row matches these filters.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {picker != null && (
        <ValuePicker
          set={set}
          column={picker}
          selected={filter.selected[picker]}
          onChange={(selected) => change((current) => ({
            ...current,
            selected: current.selected.map((s, j) => (j === picker ? selected : s)),
          }))}
          onClose={() => setPicker(null)}
        />
      )}
    </div>
  );
}

/**
 * The funnel in a column heading. It carries its own rect to the popup rather
 * than the popup finding it: the popup is `fixed`, so the only thing it needs
 * from the DOM is where to appear.
 */
function FunnelButton({ column, active, open, onToggle }: {
  column: string;
  active: boolean;
  open: boolean;
  onToggle: () => void;
}) {
  return (
    <button
      className={`result-grid-funnel${active ? ' is-active' : ''}`}
      data-column={column}
      aria-label={`Filter values in ${column}`}
      aria-expanded={open}
      onClick={(e) => {
        // The heading around this one sorts. Opening a filter is not asking for
        // the column to be sorted as well.
        e.stopPropagation();
        onToggle();
      }}
    >
      <Filter className="size-3" aria-hidden="true" />
    </button>
  );
}

/**
 * The distinct values of one column, with a search box and tick boxes.
 *
 * Positioned `fixed` from its funnel's rect rather than absolutely inside the
 * table. An absolute popup is clipped by the first scrolling ancestor and it has
 * one — `.result-grid-scroll`. No z-index escapes that; leaving the container
 * does. Same reasoning as the notebook cell's output menu, which solved it first.
 */
function ValuePicker({ set, column, selected, onChange, onClose }: {
  set: ApiResultSet;
  column: number;
  selected: Set<string> | null;
  onChange: (selected: Set<string> | null) => void;
  onClose: () => void;
}) {
  const [search, setSearch] = useState('');
  const box = useRef<HTMLDivElement | null>(null);
  const values = useMemo(() => distinctValues(set, column), [set, column]);
  const [at] = useState(() => {
    const funnel = document.querySelectorAll('.result-grid-funnel')[column];
    const rect = funnel?.getBoundingClientRect();
    return rect == null
      ? { top: 100, left: 100 }
      : {
        // Near the right edge it opens leftward rather than off-screen.
        left: Math.min(rect.left, window.innerWidth - 260),
        top: rect.bottom + 4,
      };
  });

  useEffect(() => {
    function onDown(event: MouseEvent) {
      if (!box.current?.contains(event.target as Node)) {
        onClose();
      }
    }
    // A fixed popup does not travel with the page, so scrolling or resizing
    // closes it rather than leaving it stranded over something unrelated.
    document.addEventListener('mousedown', onDown);
    window.addEventListener('scroll', onClose, true);
    window.addEventListener('resize', onClose);
    return () => {
      document.removeEventListener('mousedown', onDown);
      window.removeEventListener('scroll', onClose, true);
      window.removeEventListener('resize', onClose);
    };
  }, [onClose]);

  const shown = values.filter((v) => label(v).toLowerCase().includes(search.toLowerCase()));
  const isTicked = (value: string) => selected == null || selected.has(value);

  function toggle(value: string) {
    const next = new Set(selected ?? values);
    if (next.has(value)) {
      next.delete(value);
    } else {
      next.add(value);
    }
    // All of them ticked is the same as no filter, and saying so keeps the funnel
    // from staying lit after you have put every value back.
    onChange(next.size === values.length ? null : next);
  }

  return (
    <div className="result-grid-popup" style={{ top: at.top, left: at.left }} ref={box}>
      <div className="result-grid-popup-head">
        <input
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          placeholder="Search values…"
          aria-label="Search values"
          autoFocus
        />
        <button onClick={onClose} aria-label="Close">
          <X className="size-3" aria-hidden="true" />
        </button>
      </div>
      <div className="result-grid-popup-actions">
        <button onClick={() => onChange(null)}>Select all</button>
        <span aria-hidden="true">·</span>
        <button onClick={() => onChange(new Set())}>Clear</button>
      </div>
      <div className="result-grid-popup-list">
        {shown.length === 0 && <p className="text-sm text-muted-subtle">No value matches.</p>}
        {shown.map((value) => (
          <label key={value}>
            <input type="checkbox" checked={isTicked(value)} onChange={() => toggle(value)} />
            <span>{label(value)}</span>
          </label>
        ))}
      </div>
    </div>
  );
}

/** What a distinct value is called in the list. The null sentinel is not a value
 *  anybody typed, so it is named rather than shown. */
function label(value: string): string {
  return value === NULL_KEY ? '(null)' : value;
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
