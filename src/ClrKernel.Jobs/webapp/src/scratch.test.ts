import { describe, expect, it } from 'vitest';
import { isScratch, scratchNotebook, scratchPath, sqlOf, suggestedName } from './scratch';

describe('scratchNotebook / sqlOf', () => {
  it('round-trips a query', () => {
    const sql = 'SELECT TOP 10 *\nFROM Orders';
    expect(sqlOf(scratchNotebook('Warehouse', sql))).toBe(sql);
  });

  it('writes a cell that names the connection, so Save as is immediately runnable', () => {
    expect(scratchNotebook('Warehouse', 'SELECT 1')).toBe(
      '```sql\n#!sql --connection Warehouse\nSELECT 1\n```\n');
  });

  it('keeps blank lines inside the query and trims only the end', () => {
    const sql = 'SELECT 1\n\nUNION ALL\n\nSELECT 2';
    expect(sqlOf(scratchNotebook('W', `${sql}\n\n  `))).toBe(sql);
  });

  it('opens an empty scratch as empty rather than as a stray fence', () => {
    expect(sqlOf(scratchNotebook('W', ''))).toBe('');
  });

  it('reads a file somebody edited in the notebook editor', () => {
    // Not our exact bytes: CRLF, and a cell whose selector carries more flags.
    expect(sqlOf('```sql\r\n#!sql --connection W --name x\r\nSELECT 1\r\n```\r\n')).toBe('SELECT 1');
  });

  it('and something that is not a cell at all, verbatim', () => {
    // A notebook that opens here as empty would read as one this page had lost.
    expect(sqlOf('SELECT 1')).toBe('SELECT 1');
  });
});

describe('scratchPath', () => {
  it('is per connection, under the folder the tree and the repo both skip', () => {
    expect(scratchPath('abc-123')).toBe('.scratch/query-abc-123.nb.md');
    expect(isScratch(scratchPath('abc-123'))).toBe(true);
    expect(isScratch('queries/sales.nb.md')).toBe(false);
  });
});

describe('suggestedName', () => {
  it('guesses from the connection', () => {
    expect(suggestedName('Warehouse (prod)')).toBe('queries/warehouse-prod.nb.md');
  });

  it('and still produces a path when the name has nothing usable in it', () => {
    expect(suggestedName('—')).toBe('queries/query.nb.md');
  });
});
