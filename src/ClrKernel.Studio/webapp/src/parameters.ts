/**
 * The parameters a notebook declares: the `var name = value;` lines of the cell
 * whose first line is `// parameters` — the same cell the runner injects a
 * job's values after. Read here so the jobs form can offer them by name rather
 * than asking somebody to remember what the notebook is expecting.
 */
export interface NotebookParameter {
  name: string;
  /** The default as written, quotes stripped, so it reads as a value not code. */
  defaultValue: string;
}

const MARKER = /^\s*\/\/\s*parameters\b/i;
// ponytail: `var x = 1;` and the common typed forms. A declaration this misses
// still works as a parameter; it just is not offered by name.
const DECLARATION = /^\s*(?:var|int|long|double|decimal|bool|string|float)\s+([A-Za-z_]\w*)\s*=\s*(.+?);\s*(?:\/\/.*)?$/;

export function notebookParameters(
  cells: ReadonlyArray<{ kind: string; source: string }>,
): NotebookParameter[] {
  const cell = cells.find((c) =>
    c.kind === 'code' && MARKER.test(c.source.replace(/\r\n/g, '\n').split('\n').find((l) => l.trim() !== '') ?? ''));
  if (cell == null) {
    return [];
  }
  const out: NotebookParameter[] = [];
  for (const line of cell.source.replace(/\r\n/g, '\n').split('\n')) {
    const m = DECLARATION.exec(line);
    if (m) {
      out.push({ name: m[1], defaultValue: m[2].trim().replace(/^"(.*)"$/, '$1') });
    }
  }
  return out;
}
