/**
 * An object's name as its own database writes it, for "Copy name" in the
 * connection tree. SQL Server brackets each part and can name another database in
 * the same query; PostgreSQL, Oracle and ODBC quote with double quotes, and their
 * database is the connection itself — a three-part name is a syntax error there.
 */
export function qualifiedName(
  type: string,
  part: { database?: string | null; schema?: string | null; object?: string | null },
): string {
  if (type.toLowerCase() === 'sqlserver') {
    return [part.database, part.schema, part.object]
      .filter(Boolean)
      .map((p) => `[${String(p).replace(/]/g, ']]')}]`)
      .join('.');
  }
  // ODBC files objects of a driver with no schemas under "(default)".
  return [part.schema === '(default)' ? null : part.schema, part.object]
    .filter(Boolean)
    .map((p) => `"${String(p).replace(/"/g, '""')}"`)
    .join('.');
}
