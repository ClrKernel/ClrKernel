import { describe, expect, it } from 'vitest';
import { qualifiedName } from './connectionNames';

describe('qualifiedName', () => {
  it('brackets all three parts on SQL Server', () => {
    expect(qualifiedName('SqlServer', { database: 'dw', schema: 'dbo', object: 'Dim]Date' }))
      .toBe('[dw].[dbo].[Dim]]Date]');
  });

  it('double-quotes schema and object elsewhere, with no database part', () => {
    expect(qualifiedName('Oracle', { database: 'FREEPDB1', schema: 'HR', object: 'EMPLOYEES' }))
      .toBe('"HR"."EMPLOYEES"');
    expect(qualifiedName('Postgres', { database: 'app', schema: 'public', object: 'say "hi"' }))
      .toBe('"public"."say ""hi"""');
  });

  it('drops the placeholder schema of an ODBC driver that has none', () => {
    expect(qualifiedName('Odbc', { schema: '(default)', object: 'orders' })).toBe('"orders"');
  });
});
