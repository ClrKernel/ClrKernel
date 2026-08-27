/**
 * Which settings can go where in a `*.jobs.yaml`, from the schema the server
 * publishes at `GET /api/jobs/schema`.
 *
 * React-free and Monaco-free so the awkward part can be tested by calling a
 * function: the awkward part is not rendering a list, it is working out which
 * list — root, a job, or the `notify` block — from a half-typed file.
 *
 * ponytail: reads the indentation, does not parse the YAML. A jobs file is two
 * levels deep and the parser would have to cope with a document that is, by
 * definition, mid-edit and often invalid. If this ever has to understand flow
 * mappings or anchors, that is the moment to reach for a real parser.
 */

export interface SchemaField {
  name: string;
  type: string;
  description: string;
  example?: string;
}

/** The subset of JSON Schema this reads. The server sends draft-07. */
export interface JobsSchemaDocument {
  properties?: Record<string, SchemaProperty>;
  definitions?: Record<string, { properties?: Record<string, SchemaProperty> }>;
}

interface SchemaProperty {
  type?: string;
  description?: string;
  examples?: string[];
  $ref?: string;
}

function fieldsOf(properties: Record<string, SchemaProperty> | undefined): SchemaField[] {
  return Object.entries(properties ?? {}).map(([name, property]) => ({
    name,
    // A `$ref` carries the type in the thing it points at; for a completion list
    // "object" is the honest word for all of them.
    type: property.type ?? 'object',
    description: property.description ?? '',
    example: property.examples?.[0],
  }));
}

/** How far a line is indented, or null for a blank or comment-only line. */
function indentOf(line: string): number | null {
  if (line.trim() === '' || line.trimStart().startsWith('#')) {
    return null;
  }
  return line.length - line.trimStart().length;
}

/** The key a line declares (`notify:` → `notify`), or null. */
function keyOf(line: string): string | null {
  return keyAt(line)?.name ?? null;
}

/**
 * The key a line declares and the column it starts at.
 *
 * The column matters for `- name: daily`: that key belongs to the list item's
 * block, which starts where the key does — not at the dash, and not at a fixed
 * two columns past it, since `-   name:` is equally legal.
 */
function keyAt(line: string): { name: string; column: number } | null {
  const match = /^(\s*-?\s*)([A-Za-z][\w-]*)\s*:/.exec(line);
  return match ? { name: match[2], column: match[1].length } : null;
}

/**
 * The settings offerable at the cursor.
 *
 * `before` is the text to the left of the cursor on the current line; `above` is
 * every line before it. Keys already written in the same block are left out —
 * offering `name:` again to a job that has one is noise.
 */
export function completionsAt(
  schema: JobsSchemaDocument, above: string[], before: string,
): SchemaField[] {
  // Only at the start of a value-less line. After `cron: ` the answer is a cron
  // expression, and this knows nothing about those.
  if (/:\s*\S/.test(before) || before.trim().startsWith('#')) {
    return [];
  }

  const root = fieldsOf(schema.properties);
  const entry = fieldsOf(schema.definitions?.entry?.properties);
  const notify = fieldsOf(schema.definitions?.notify?.properties);

  const indent = before.length - before.trimStart().length;
  // A `- ` opens a job whatever else is on the line.
  const inItem = /^\s*-\s*/.test(before);

  // The enclosing line: the first one above us that is less indented. What it is
  // decides which set of settings we are inside.
  let candidates = inItem ? entry : root;
  let blockStart = 0;
  const written = new Set<string>();
  for (let i = above.length - 1; i >= 0; i--) {
    const lineIndent = indentOf(above[i]);
    if (lineIndent == null || lineIndent >= indent) {
      continue;
    }
    const line = above[i];
    const key = keyOf(line);
    if (/^\s*-\s/.test(line)) {
      // `  - name: daily` — the dash opens a job and its own key already belongs
      // to that job's block, two columns in from the dash. Missing this is why
      // `name:` used to be offered to a job that had one.
      candidates = entry;
      const declared = keyAt(line);
      if (declared && declared.column === indent) {
        written.add(declared.name);
      }
    } else if (key === 'notify') {
      candidates = notify;
    } else if (key === 'jobs' || key === 'defaults') {
      candidates = entry;
    } else {
      candidates = root;
    }
    blockStart = i + 1;
    break;
  }

  // What this block already says. Stop at the first line that leaves the block —
  // anything shallower belongs to a different job.
  for (const line of above.slice(blockStart)) {
    const lineIndent = indentOf(line);
    if (lineIndent == null) {
      continue;
    }
    if (lineIndent < indent) {
      break;
    }
    if (lineIndent === indent) {
      const key = keyOf(line);
      if (key) {
        written.add(key);
      }
    }
  }
  return candidates.filter((field) => !written.has(field.name));
}
