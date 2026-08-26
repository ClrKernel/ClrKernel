/**
 * The query editor's buffer, as a notebook on disk.
 *
 * The Connections query editor used to be a `useState` string: it went away when
 * you clicked another connection and again when you reloaded, and the only way
 * out of it was "Open in notebook", which appended to something else. Backing it
 * with a real `.nb.md` in your own worktree makes it what it always was — a
 * one-cell notebook — so it survives, and so "Save as" is a file operation
 * rather than a special case.
 *
 * It lives under a dot-directory, which is doing two jobs. `NotebookTree` skips
 * dot-directories, so scratch files never appear in Files or the explorer; and
 * the repo excludes it, so they never make your branch dirty and never ride
 * along with a push. Both are the server's doing — this module only names it.
 *
 * React-free and unit-tested, the rule for this folder: the round trip is the
 * thing that has to hold, and it needs no DOM.
 */

/** Matches `GitService.ScratchDirectory`. */
export const SCRATCH_DIR = '.scratch';

/** One scratch per connection, so switching back finds what you were writing. */
export function scratchPath(connectionId: string): string {
  return `${SCRATCH_DIR}/query-${connectionId}.nb.md`;
}

/**
 * The notebook text for a query against a connection.
 *
 * A `#!sql --connection x` cell rather than a copy of the connection's settings:
 * the shared connections.json sits beside the notebook, so naming the connection
 * is all the cell needs, and the notebook stays free of anything to keep in step.
 * Written on every save, so the file is always something you could Save as and
 * run — not a fragment that has to be repaired on the way out.
 */
export function scratchNotebook(connectionName: string, sql: string): string {
  return '```sql\n'
    + `#!sql --connection ${connectionName}\n`
    + `${sql.replace(/\s+$/, '')}\n`
    + '```\n';
}

/**
 * The SQL back out of it — what the editor shows.
 *
 * The selector line is the connection, which the page already knows from the one
 * you have selected, so showing it would be showing you a line you must not edit.
 * Anything that is not a fenced cell comes back verbatim: a file somebody has
 * been at in the notebook editor should open here as what it says, not as empty.
 */
export function sqlOf(content: string): string {
  const lines = (content ?? '').replace(/\r\n/g, '\n').split('\n');
  if (lines[0]?.startsWith('```')) {
    lines.shift();
    while (lines.length > 0 && !lines[lines.length - 1].startsWith('```')) {
      lines.pop();
    }
    lines.pop();
  }
  if (lines[0]?.startsWith('#!')) {
    lines.shift();
  }
  return lines.join('\n').replace(/\s+$/, '');
}

/**
 * A name to put in the Save as box, so it is not blank.
 *
 * `.scratch/query-<uuid>.nb.md` is not a name anybody would keep, and a prompt
 * that opens empty makes you invent one. The connection is what the query is
 * about, so that is the guess — in a `queries/` folder, which the server creates
 * on the way past.
 */
export function suggestedName(connectionName: string): string {
  const slug = (connectionName ?? '')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');
  return `queries/${slug || 'query'}.nb.md`;
}

/**
 * The buffer to show when a script was carried in from another connection.
 *
 * Scripting a table under connection B while looking at A navigates, and the
 * navigation reloads the buffer — so inserting into the editor there and then
 * writes the script into A's file and then wipes it off the screen. The script
 * travels as a value instead and is applied on the far side, once the file it
 * belongs to has loaded.
 *
 * Same rule as inserting at the cursor: appended to what is there, alone when
 * there is nothing.
 */
export function pendingInsert(loaded: string, carried: string | null): string {
  if (carried == null || carried.length === 0) {
    return loaded;
  }
  return loaded.trim().length === 0 ? carried : `${loaded}\n${carried}`;
}
