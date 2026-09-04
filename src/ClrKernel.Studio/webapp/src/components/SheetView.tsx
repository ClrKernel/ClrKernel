import { useEffect, useState } from 'react';
import type { SheetPage } from '../api';
import { columnLabel } from '../notebook';

/**
 * A spreadsheet the way a spreadsheet looks: lettered columns across the top,
 * numbered rows down the side, and the workbook's tabs along the bottom.
 *
 * Not an editor and not trying to be one — no formulas, no formatting, no
 * selection. The question this answers is "what is in this file", which today
 * means downloading it and opening Excel.
 */
export function SheetView({ sheets, rowLimit }: { sheets: SheetPage[]; rowLimit: number }) {
  const [active, setActive] = useState(0);
  // A different file may have fewer tabs than the one before it.
  useEffect(() => setActive(0), [sheets]);

  const sheet = sheets[Math.min(active, sheets.length - 1)];
  if (sheet == null) {
    return <p className="px-4 text-base text-muted-foreground">This file has no sheets.</p>;
  }
  // Ragged rows: the grid is as wide as its widest row, and short rows get empty
  // cells rather than a torn right edge.
  const width = sheet.rows.reduce((w, row) => Math.max(w, row.length), 0);
  const truncated = sheet.totalRows > sheet.rows.length;

  return (
    <div className="flex min-h-0 flex-1 flex-col px-4 pb-4">
      <div className="min-h-0 flex-1 overflow-auto rounded-lg border border-border bg-card">
        <table className="border-separate border-spacing-0 text-xs">
          <thead>
            <tr>
              {/* The corner. Sticky on both axes, so it stays over the join when
                  the grid is scrolled diagonally. */}
              <th className="sticky top-0 left-0 z-20 border-r border-b border-border bg-muted px-2 py-1" />
              {Array.from({ length: width }, (_, i) => (
                <th
                  key={i}
                  className="sticky top-0 z-10 min-w-24 border-r border-b border-border bg-muted px-2 py-1 text-center font-medium text-muted-subtle"
                >
                  {columnLabel(i)}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sheet.rows.map((row, r) => (
              <tr key={r}>
                <th className="sticky left-0 z-10 border-r border-b border-border bg-muted px-2 py-1 text-right font-normal text-muted-subtle tabular-nums">
                  {r + 1}
                </th>
                {Array.from({ length: width }, (_, c) => (
                  <td
                    key={c}
                    // whitespace-pre so a cell that contains a newline — which a
                    // quoted csv field may — reads as the two lines it is.
                    className="max-w-96 truncate border-r border-b border-border px-2 py-1 whitespace-pre"
                    title={row[c] ?? undefined}
                  >
                    {row[c] ?? ''}
                  </td>
                ))}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <div className="flex items-center justify-between gap-3 pt-2">
        {/* Excel shows a tab even for a one-sheet workbook, and a csv has no tabs
            at all — it is one table, and a strip saying so is furniture. */}
        <div className="flex min-w-0 gap-1 overflow-x-auto">
          {sheets.length > 1 && sheets.map((s, i) => (
            <button
              key={s.name + i}
              type="button"
              onClick={() => setActive(i)}
              className={
                'shrink-0 rounded-t-md border-x border-t px-3 py-1 text-xs outline-none '
                + 'focus-visible:ring-2 focus-visible:ring-ring '
                + (i === (active < sheets.length ? active : 0)
                  ? 'border-border bg-card font-medium'
                  : 'border-transparent text-muted-subtle hover:text-foreground')
              }
            >
              {s.name}
            </button>
          ))}
        </div>
        <p className="shrink-0 text-xs text-muted-subtle tabular-nums">
          {truncated
            ? `first ${sheet.rows.length.toLocaleString()} of ${sheet.totalRows.toLocaleString()} rows`
            : `${sheet.totalRows.toLocaleString()} ${sheet.totalRows === 1 ? 'row' : 'rows'}`}
          {truncated && ` — this shows the first ${rowLimit.toLocaleString()}`}
        </p>
      </div>
    </div>
  );
}
