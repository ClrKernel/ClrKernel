import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { beforeAll, describe, expect, it } from 'vitest';
import { ROW_LIMIT, delimiterFor, readDelimited, readWorkbook, useSheetLibrary } from './sheet';

/**
 * The workbook these read is written by **openpyxl**, not by the library under
 * test, so what is asserted is "it reads what Excel writes" rather than "it
 * agrees with itself".
 */
beforeAll(() => {
  // Vitest resolves the vendored module fine; the indirection exists so the
  // dynamic import in `sheet.ts` is not Vite's to arrange here.
  useSheetLibrary(() => import('../vendor/sheetjs/xlsx.mjs'));
});

const fixture = () =>
  readFileSync(fileURLToPath(new URL('./fixtures/sample.xlsx', import.meta.url)));

describe('workbooks', () => {
  it('reads every tab, in order', async () => {
    const sheets = await readWorkbook(fixture().buffer as ArrayBuffer);
    expect(sheets.map((s) => s.name)).toEqual(['Numbers', 'Notes']);
    expect(sheets[1].rows[0]).toEqual(['only', 'two']);
  });

  /**
   * The assertion the whole approach turns on. A date is stored as a serial
   * number, and a viewer showing 46095 where the spreadsheet shows a date is not
   * a viewer — `raw: false` is what applies the cell's own format.
   */
  it('shows what the spreadsheet shows, not what it stores', async () => {
    const [numbers] = await readWorkbook(fixture().buffer as ArrayBuffer);

    expect(numbers.rows[0]).toEqual(['name', 'when', 'amount', 'note']);
    expect(numbers.rows[1][0]).toBe('alpha');
    expect(numbers.rows[1][1]).toMatch(/2026/);
    expect(numbers.rows[1][1]).not.toMatch(/^460/);
    expect(numbers.rows[1][2]).toBe('1,234.50');
    expect(numbers.rows[1][3]).toBe('quoted, comma');

    expect(numbers.rows[3][1]).toBeNull();
    expect(numbers.totalRows).toBe(4);
  });
});

describe('delimited files', () => {
  it('quotes the way a spreadsheet wrote them', async () => {
    const [page] = await readDelimited(
      'a,b,c\n"has, comma","has ""quotes""",plain\n"multi\nline",,end\n', ',', 'x.csv');

    expect(page.rows[0]).toEqual(['a', 'b', 'c']);
    expect(page.rows[1]).toEqual(['has, comma', 'has "quotes"', 'plain']);
    expect(page.rows[2]).toEqual(['multi\nline', null, 'end']);
    expect(page.totalRows).toBe(3);
  });

  it('splits tabs when the name says tabs', async () => {
    expect(delimiterFor('x.tsv')).toBe('\t');
    expect(delimiterFor('x.tab')).toBe('\t');
    expect(delimiterFor('x.csv')).toBe(',');

    const [page] = await readDelimited('a\tb\n1\t2\n', '\t', 'x.tsv');
    expect(page.rows[1]).toEqual(['1', '2']);
  });

  /** Showing 1000 rows and reporting 1000 is indistinguishable from a short file. */
  it('caps long files and still reports their length', async () => {
    const text = Array.from({ length: ROW_LIMIT + 500 }, (_, i) => `${i + 1},x`).join('\n');
    const [page] = await readDelimited(text, ',', 'big.csv');

    expect(page.rows).toHaveLength(ROW_LIMIT);
    expect(page.totalRows).toBe(ROW_LIMIT + 500);
  });
});
