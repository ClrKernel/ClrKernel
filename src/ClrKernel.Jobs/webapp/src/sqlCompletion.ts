/**
 * What to suggest in a SQL editor, given the schema behind it and the text around
 * the cursor.
 *
 * React-free and Monaco-free on purpose: this is the part with the judgement in it,
 * and it should be testable by writing a query as a string and reading back what a
 * person would be offered. The Monaco provider is the thin part that turns these
 * into suggestion objects.
 *
 * It is not a SQL parser and must not become one. A query being edited is usually
 * not valid SQL — that is the whole point of completing it — so anything that
 * needed a parse tree would go quiet exactly when it was wanted. What follows works
 * off the last few tokens, which is what stays reliable mid-sentence.
 */

export interface SchemaObject {
  schema: string;
  name: string;
  kind: 'table' | 'view';
  columns: string[];
}

export interface SqlSchema {
  database: string;
  objects: SchemaObject[];
  truncated: boolean;
}

export type SuggestionKind = 'schema' | 'table' | 'view' | 'column';

export interface Suggestion {
  /** What is inserted, and what is shown. */
  label: string;
  kind: SuggestionKind;
  /** The grey text beside it: which table a column came from, and so on. */
  detail?: string;
  /** Lower sorts first. Monaco sorts by the text it is given, so this becomes a
   *  prefix rather than a number it understands. */
  rank: number;
}

/** The clause a position is in, as far as anything here needs to know. */
type Clause = 'after-dot' | 'object' | 'general';

const OBJECT_KEYWORDS = ['from', 'join', 'into', 'update', 'table', 'exec', 'execute'];

/**
 * The identifier immediately before a trailing dot, or null.
 *
 * Only a trailing dot counts: `orders.` is asking about `orders`, while
 * `orders.id` has moved on to typing the column and the qualifier is still
 * `orders`. Both cases are wanted, which is why the word after the dot is skipped
 * rather than treated as part of the qualifier.
 */
export function qualifierBefore(textToCursor: string): string | null {
  const match = /([A-Za-z_@#][\w$]*|\[[^\]]*\])\s*\.\s*[\w$]*$/.exec(textToCursor);
  if (match == null) {
    return null;
  }
  return unquote(match[1]);
}

/** `[Order Details]` and `"Orders"` are the same identifier as `Orders` here. */
export function unquote(identifier: string): string {
  const trimmed = (identifier ?? '').trim();
  if (trimmed.startsWith('[') && trimmed.endsWith(']')) {
    return trimmed.slice(1, -1);
  }
  if (trimmed.startsWith('"') && trimmed.endsWith('"')) {
    return trimmed.slice(1, -1);
  }
  return trimmed;
}

/** Wraps an identifier only when it needs it, the way somebody would write it. */
export function quoteIfNeeded(identifier: string): string {
  return /^[A-Za-z_][\w$]*$/.test(identifier) ? identifier : `[${identifier}]`;
}

/**
 * Every `name alias` and `name AS alias` in the text, as alias → object name.
 *
 * Read from the whole statement rather than only the part before the cursor,
 * because people go back and edit: writing `SELECT o.` in a query that already says
 * `FROM Orders o` further down has to work, and it is the common case when you
 * realise you forgot a column.
 */
export function aliases(sql: string): Map<string, string> {
  const found = new Map<string, string>();
  const pattern = new RegExp(
    // FROM/JOIN, then a possibly-qualified name, then an optional AS, then the alias.
    String.raw`\b(?:from|join|update|into)\s+` +
    String.raw`((?:\[[^\]]*\]|"[^"]*"|[\w$]+)(?:\s*\.\s*(?:\[[^\]]*\]|"[^"]*"|[\w$]+)){0,2})` +
    String.raw`(?:\s+as)?\s+(?!on\b|where\b|inner\b|left\b|right\b|full\b|cross\b|join\b|group\b|order\b|having\b|set\b|values\b|select\b)` +
    String.raw`(\[[^\]]*\]|[A-Za-z_][\w$]*)`,
    'gi');
  for (const match of sql.matchAll(pattern)) {
    const parts = match[1].split('.').map(unquote);
    found.set(unquote(match[2]).toLowerCase(), parts[parts.length - 1]);
  }
  return found;
}

/** The object names mentioned anywhere in the text, aliased or not. */
export function mentioned(sql: string): string[] {
  const found: string[] = [];
  const pattern = new RegExp(
    String.raw`\b(?:from|join|update|into)\s+` +
    String.raw`((?:\[[^\]]*\]|"[^"]*"|[\w$]+)(?:\s*\.\s*(?:\[[^\]]*\]|"[^"]*"|[\w$]+)){0,2})`,
    'gi');
  for (const match of sql.matchAll(pattern)) {
    const parts = match[1].split('.').map(unquote);
    found.push(parts[parts.length - 1]);
  }
  return found;
}

/** Which of the three situations the cursor is in. */
export function clauseAt(textToCursor: string): Clause {
  if (qualifierBefore(textToCursor) != null) {
    return 'after-dot';
  }
  // The last complete word before whatever is being typed.
  const words = textToCursor.toLowerCase().match(/[\w$]+/g) ?? [];
  const partial = /[\w$]$/.test(textToCursor);
  const previous = words[words.length - (partial ? 2 : 1)];
  return previous != null && OBJECT_KEYWORDS.includes(previous) ? 'object' : 'general';
}

/**
 * What to offer at the cursor.
 *
 * @param sql the whole editor text, for aliases declared after the cursor
 * @param textToCursor everything up to the cursor, which is what decides the clause
 */
export function suggestionsFor(
  sql: string, textToCursor: string, schema: SqlSchema | null,
): Suggestion[] {
  if (schema == null || schema.objects.length === 0) {
    return [];
  }
  const clause = clauseAt(textToCursor);

  if (clause === 'after-dot') {
    const qualifier = qualifierBefore(textToCursor);
    return afterDot(qualifier, sql, schema);
  }

  const objects = schema.objects.map(objectSuggestion);
  if (clause === 'object') {
    // Schemas first: typing `FROM ` and being offered `dbo` gets you to the tables
    // in one more keystroke, and a database with several schemas is where the list
    // of bare table names stops being useful.
    return [...schemaSuggestions(schema), ...objects];
  }

  // Mid-statement: the columns of what this query is already about are the likeliest
  // thing being typed, so they come before the table list rather than after it.
  return [...columnsOfMentioned(sql, schema), ...objects];
}

function afterDot(qualifier: string | null, sql: string, schema: SqlSchema): Suggestion[] {
  if (qualifier == null) {
    return [];
  }
  const lower = qualifier.toLowerCase();

  // An alias wins over everything: `o.` where the query says `FROM Orders o` is
  // asking about Orders, even if some other table happens to be called `o`.
  const aliased = aliases(sql).get(lower);
  const target = aliased ?? qualifier;
  const objects = schema.objects.filter((o) => o.name.toLowerCase() === target.toLowerCase());
  if (objects.length > 0) {
    return objects.flatMap((o) =>
      o.columns.map((column, i) => ({
        label: quoteIfNeeded(column),
        kind: 'column' as const,
        detail: `${o.schema}.${o.name}`,
        // Declaration order, not alphabetical: a table's own order is the one its
        // author chose and the one people picture.
        rank: i,
      })));
  }

  // Not an object, so a schema: `dbo.` offers what is in dbo.
  const inSchema = schema.objects.filter((o) => o.schema.toLowerCase() === lower);
  return inSchema.map((o) => ({
    label: quoteIfNeeded(o.name),
    kind: o.kind,
    detail: o.kind,
    rank: 0,
  }));
}

function schemaSuggestions(schema: SqlSchema): Suggestion[] {
  const names = [...new Set(schema.objects.map((o) => o.schema))];
  return names.map((name) => ({
    label: quoteIfNeeded(name),
    kind: 'schema' as const,
    detail: 'schema',
    rank: 0,
  }));
}

function objectSuggestion(o: SchemaObject): Suggestion {
  return {
    // Qualified, because that is what belongs in a query somebody else will read —
    // and Monaco filters on the typed word, so typing the bare name still finds it.
    label: `${quoteIfNeeded(o.schema)}.${quoteIfNeeded(o.name)}`,
    kind: o.kind,
    detail: o.kind,
    rank: 1,
  };
}

function columnsOfMentioned(sql: string, schema: SqlSchema): Suggestion[] {
  const names = new Set(mentioned(sql).map((n) => n.toLowerCase()));
  if (names.size === 0) {
    return [];
  }
  const seen = new Set<string>();
  const found: Suggestion[] = [];
  for (const object of schema.objects) {
    if (!names.has(object.name.toLowerCase())) {
      continue;
    }
    for (const column of object.columns) {
      // One entry per name: two joined tables with an Id each should offer Id once,
      // not twice with nothing to tell them apart.
      if (seen.has(column.toLowerCase())) {
        continue;
      }
      seen.add(column.toLowerCase());
      found.push({
        label: quoteIfNeeded(column),
        kind: 'column',
        detail: `${object.schema}.${object.name}`,
        rank: 0,
      });
    }
  }
  return found;
}
