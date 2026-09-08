/** One tab of a workbook, or the whole of a delimited file. */
export interface SheetPage {
  name: string;
  /** Ragged — a csv row is as long as it was written — and null is an empty cell. */
  rows: (string | null)[][];
  /** Before the cap, so a truncated sheet can say what it is a slice of. */
  totalRows: number;
}

/**
 * Reading spreadsheets in the browser, with SheetJS.
 *
 * <p>Client-side, unlike `/notebooks/cells`, because the alternative is a
 * server-side reader that handles the OpenXML family and nothing else: `.xls` is
 * OLE2 and `.ods` is a different zip again, and a tree that lists a file it will
 * not open is worse than either.</p>
 *
 * The library is a megabyte, so it is loaded on demand — a session that never
 * opens a spreadsheet never fetches it.
 */

/** Rows returned per sheet. The browser lays out every row it is handed. */
export const ROW_LIMIT = 1000;

/** Columns per row, for the same reason. */
export const COLUMN_LIMIT = 200;

type Loader = () => Promise<typeof import('../vendor/sheetjs/xlsx.mjs')>;

let loader: Loader = () => import('../vendor/sheetjs/xlsx.mjs');

/** Swapped in tests, which read the module directly rather than through Vite. */
export function useSheetLibrary(load: Loader): void {
  loader = load;
}

/**
 * A workbook's tabs as rows of display text.
 *
 * `raw: false` is the whole reason this uses a library: it applies each cell's
 * number format, so a date reads as a date rather than as the serial number a
 * date is stored as, and 1234.5 reads as whatever the sheet says it is.
 */
export async function readWorkbook(bytes: ArrayBuffer): Promise<SheetPage[]> {
  const XLSX = await loader();
  const book = XLSX.read(bytes, { type: 'array', cellDates: false, cellNF: true });
  return book.SheetNames.map((name) => page(XLSX, book.Sheets[name], name));
}

/** A delimited file, parsed by the same reader so quoting behaves identically. */
export async function readDelimited(
  text: string, delimiter: string, name: string,
): Promise<SheetPage[]> {
  const XLSX = await loader();
  const book = XLSX.read(text, { type: 'string', raw: false, FS: delimiter });
  const first = book.SheetNames[0];
  return [page(XLSX, book.Sheets[first], name)];
}

function page(
  XLSX: Awaited<ReturnType<Loader>>, sheet: unknown, name: string,
): SheetPage {
  // `header: 1` is rows-as-arrays: this is a viewer, and the first row of a
  // spreadsheet is a row, not a set of keys.
  const rows = XLSX.utils.sheet_to_json(sheet as never, {
    header: 1, raw: false, defval: null, blankrows: true,
  }) as (string | null)[][];
  return {
    name,
    rows: rows.slice(0, ROW_LIMIT).map((row) =>
      row.slice(0, COLUMN_LIMIT).map((cell) => (cell === '' ? null : cell))),
    totalRows: rows.length,
  };
}

/** The delimiter a file's extension implies. */
export function delimiterFor(path: string): string {
  return /\.(tsv|tab)$/i.test(path ?? '') ? '\t' : ',';
}
