import { describe, expect, it } from 'vitest';
import {
  aliases,
  clauseAt,
  mentioned,
  qualifierBefore,
  quoteIfNeeded,
  suggestionsFor,
  unquote,
  type SqlSchema,
} from './sqlCompletion';

const schema: SqlSchema = {
  database: 'dw',
  truncated: false,
  objects: [
    { schema: 'shop', name: 'Orders', kind: 'table', columns: ['OrderId', 'Customer', 'Total'] },
    { schema: 'shop', name: 'OrderLines', kind: 'table', columns: ['OrderLineId', 'OrderId'] },
    { schema: 'dbo', name: 'Order Archive', kind: 'table', columns: ['OrderId', 'Archived At'] },
    { schema: 'shop', name: 'ActiveOrders', kind: 'view', columns: ['OrderId'] },
  ],
};

/** The labels offered, in the order they are offered. */
function labels(sql: string, cursorAt?: string): string[] {
  const toCursor = cursorAt ?? sql;
  return suggestionsFor(sql, toCursor, schema).map((s) => s.label);
}

describe('qualifierBefore', () => {
  it('finds the name before a trailing dot', () => {
    expect(qualifierBefore('SELECT o.')).toBe('o');
  });

  it('keeps the qualifier while the column is being typed', () => {
    expect(qualifierBefore('SELECT o.Cust')).toBe('o');
  });

  it('reads through a quoted identifier', () => {
    expect(qualifierBefore('SELECT [Order Archive].')).toBe('Order Archive');
  });

  it('is null when there is no dot to speak of', () => {
    expect(qualifierBefore('SELECT Cust')).toBeNull();
    expect(qualifierBefore('')).toBeNull();
  });

  it('does not mistake a decimal for a qualifier', () => {
    expect(qualifierBefore('WHERE Total > 1.5')).toBeNull();
  });
});

describe('aliases', () => {
  it('reads the bare form', () => {
    expect(aliases('SELECT * FROM Orders o').get('o')).toBe('Orders');
  });

  it('and the AS form', () => {
    expect(aliases('SELECT * FROM Orders AS o').get('o')).toBe('Orders');
  });

  it('takes the object name off a qualified one', () => {
    expect(aliases('SELECT * FROM dw.shop.Orders o').get('o')).toBe('Orders');
  });

  it('reads every table in a join', () => {
    const found = aliases('FROM shop.Orders o JOIN shop.OrderLines l ON l.OrderId = o.OrderId');
    expect(found.get('o')).toBe('Orders');
    expect(found.get('l')).toBe('OrderLines');
  });

  it('does not read a keyword as an alias', () => {
    // `FROM Orders WHERE` must not make an alias called `where`.
    expect(aliases('SELECT * FROM Orders WHERE Total > 0').has('where')).toBe(false);
    expect(aliases('FROM Orders JOIN OrderLines ON 1=1').has('join')).toBe(false);
    expect(aliases('FROM Orders GROUP BY Total').has('group')).toBe(false);
  });

  it('handles a quoted table name', () => {
    expect(aliases('FROM [Order Archive] a').get('a')).toBe('Order Archive');
  });
});

describe('clauseAt', () => {
  it('knows an object is wanted after FROM', () => {
    expect(clauseAt('SELECT * FROM ')).toBe('object');
    expect(clauseAt('SELECT * FROM Ord')).toBe('object');
    expect(clauseAt('... JOIN ')).toBe('object');
  });

  it('knows a dot changes everything', () => {
    expect(clauseAt('SELECT o.')).toBe('after-dot');
  });

  it('is general elsewhere', () => {
    expect(clauseAt('SELECT ')).toBe('general');
    expect(clauseAt('SELECT * FROM Orders WHERE ')).toBe('general');
  });
});

describe('after a dot', () => {
  it('offers the columns of the aliased table', () => {
    expect(labels('SELECT o. FROM shop.Orders o', 'SELECT o.'))
      .toEqual(['OrderId', 'Customer', 'Total']);
  });

  it('in the order the table declares them', () => {
    // Not alphabetical: a table's own order is the one its author chose.
    expect(labels('SELECT o. FROM shop.Orders o', 'SELECT o.')[0]).toBe('OrderId');
  });

  it('works when the alias is declared after the cursor', () => {
    // Which is the common case: you get to the end and go back for a column.
    expect(labels('SELECT o.Cust FROM shop.Orders o', 'SELECT o.Cust')).toContain('Customer');
  });

  it('offers a table’s columns when it is named rather than aliased', () => {
    expect(labels('SELECT Orders. FROM shop.Orders', 'SELECT Orders.')).toContain('Total');
  });

  it('offers what is in a schema when the qualifier is one', () => {
    expect(labels('SELECT * FROM shop.', 'SELECT * FROM shop.'))
      .toEqual(['Orders', 'OrderLines', 'ActiveOrders']);
  });

  it('prefers the alias over a table that happens to share its name', () => {
    const sql = 'SELECT Orders. FROM shop.OrderLines Orders';
    expect(labels(sql, 'SELECT Orders.')).toEqual(['OrderLineId', 'OrderId']);
  });

  it('offers nothing for a qualifier that is neither', () => {
    expect(labels('SELECT nonsense.', 'SELECT nonsense.')).toEqual([]);
  });
});

describe('after FROM', () => {
  it('offers schemas first, then qualified objects', () => {
    const found = labels('SELECT * FROM ');
    expect(found.slice(0, 2)).toEqual(['shop', 'dbo']);
    expect(found).toContain('shop.Orders');
  });

  it('quotes a name that needs it and leaves the rest alone', () => {
    expect(labels('SELECT * FROM ')).toContain('dbo.[Order Archive]');
  });
});

describe('mid-statement', () => {
  it('puts the columns of the tables in play before the table list', () => {
    const found = labels('SELECT  FROM shop.Orders', 'SELECT ');
    expect(found.slice(0, 3)).toEqual(['OrderId', 'Customer', 'Total']);
    expect(found).toContain('shop.Orders');
  });

  it('offers a shared column name once, not once per table', () => {
    const found = labels(
      'SELECT  FROM shop.Orders o JOIN shop.OrderLines l ON 1=1', 'SELECT ');
    expect(found.filter((l) => l === 'OrderId')).toHaveLength(1);
  });

  it('falls back to the table list when no table is named yet', () => {
    expect(labels('SELECT ')).toContain('shop.Orders');
  });
});

describe('the guards', () => {
  it('offers nothing without a schema', () => {
    expect(suggestionsFor('SELECT ', 'SELECT ', null)).toEqual([]);
  });

  it('offers nothing when the schema is empty', () => {
    expect(suggestionsFor('SELECT ', 'SELECT ', { ...schema, objects: [] })).toEqual([]);
  });
});

describe('identifiers', () => {
  it('unquotes both spellings', () => {
    expect(unquote('[Order Archive]')).toBe('Order Archive');
    expect(unquote('"Orders"')).toBe('Orders');
    expect(unquote('Orders')).toBe('Orders');
  });

  it('quotes only what needs it', () => {
    expect(quoteIfNeeded('Orders')).toBe('Orders');
    expect(quoteIfNeeded('Order Archive')).toBe('[Order Archive]');
    expect(quoteIfNeeded('2Fast')).toBe('[2Fast]');
  });
});

describe('mentioned', () => {
  it('lists the objects a query is about', () => {
    expect(mentioned('FROM shop.Orders o JOIN shop.OrderLines l ON 1=1'))
      .toEqual(['Orders', 'OrderLines']);
  });
});
