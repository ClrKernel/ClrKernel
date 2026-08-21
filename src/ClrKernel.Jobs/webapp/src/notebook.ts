import type { ApiCell, ApiCellRun, ApiLanguage, ApiSession, ApiSyncCell } from './api';
import type { NotebookOutput } from './ipynb';

/**
 * Notebook editing logic, kept free of React and Monaco so it can be tested
 * directly — the same rule ipynb.ts follows. Everything here is a pure function
 * over cells; the components only render the result.
 */

/** A cell plus the editor-only state that never reaches the file. */
export interface EditorCell extends ApiCell {
  id: string;
}

let nextId = 0;

/** Ids are the kernel's cellId for a run, so they must be unique per session. */
export function newCellId(): string {
  nextId += 1;
  return `n${nextId}`;
}

/**
 * Ids are minted fresh, never positional. The session keys a cell's outputs by
 * the id it was run under, so a positional id would file a reloaded notebook's
 * results under whatever cell now sits at that index — delete the first cell,
 * save, and every output shifts up one. Losing run state on reload is honest;
 * showing it against the wrong cell is not.
 */
export function withIds(cells: ApiCell[]): EditorCell[] {
  return cells.map((cell) => ({ ...cell, id: newCellId() }));
}

/**
 * The Monaco language for a cell. Cell language ids are the kernel's
 * (`shellscript`, `csharp-script`); Monaco has its own names, and for languages
 * it has no grammar for, plain text beats a wrong highlighter.
 */
export function monacoLanguage(languageId: string | null | undefined, tag?: string | null): string {
  const id = (languageId ?? '').toLowerCase();
  if (id === 'sql') {
    return 'sql';
  }
  if (id === 'powershell') {
    return 'powershell';
  }
  if (id === 'shellscript') {
    return 'shell';
  }
  if (id === 'mermaid' || id === 'dax' || id === 'http') {
    // Monaco ships no grammar for these; the kernel owns their real editing story.
    return 'plaintext';
  }
  if (!id) {
    // No language id: prose, or a C# block (csharp/cs/c#).
    return tag == null ? 'markdown' : 'csharp';
  }
  return 'plaintext';
}

/**
 * The cells to keep open on the kernel, for completion and hover.
 *
 * Two things this is deliberately not:
 *
 * - **Not `monacoLanguage`.** That is a syntax-highlighting choice; this is what the
 *   server dispatches its language services on. Monaco calls a C# cell `csharp`, and
 *   the kernel has no such language — `csharp-script` is the name that falls through
 *   to the script engine, and it is what VS Code sends, so both editors take one path.
 * - **Not the markdown cells.** They would be opened as C# documents, and completion
 *   in a code cell gathers context from every open document in the notebook — prose
 *   parsed as C#. Dropping them here is also what closes one that used to be code.
 */
export function toSyncCells(cells: EditorCell[]): ApiSyncCell[] {
  return cells
    .filter((cell) => cell.kind === 'code')
    .map((cell) => ({
      id: cell.id,
      languageId: cell.languageId ?? 'csharp-script',
      source: cell.source,
    }));
}

/**
 * The Monaco language for a whole file — the Source tab and the production diff,
 * where there are no cells to ask. A notebook that is not `.nb.md` (`.ipynb`,
 * `.dib`, `.csx`) opens as source, so those need an answer too.
 */
export function fileLanguage(path: string): string {
  const name = path.toLowerCase();
  if (name.endsWith('.ipynb')) {
    return 'json';
  }
  if (name.endsWith('.yaml') || name.endsWith('.yml')) {
    return 'yaml';
  }
  if (name.endsWith('.csx') || name.endsWith('.dib')) {
    return 'csharp';
  }
  return 'markdown';
}

/** The label shown in a cell's language picker. */
export function languageLabel(cell: ApiCell, languages: ApiLanguage[]): string {
  if (cell.kind === 'markdown') {
    return 'Markdown';
  }
  const language = languages.find((l) => l.id === cell.languageId);
  return language?.displayName ?? 'C#';
}

/**
 * The language behind this cell when it has connection providers — the kernel
 * says which do, so a Connect button appears on `#!sql` and `#!dax` cells and
 * nowhere else without the browser knowing what SQL is.
 */
export function connectableLanguage(
  languageId: string | null | undefined,
  languages: ApiLanguage[],
): ApiLanguage | null {
  const language = languages.find((l) => l.id === languageId);
  return language?.hasConnections ? language : null;
}

/** Picker options: Markdown, C#, then every language the kernel declared. */
export function languageOptions(languages: ApiLanguage[]): { value: string; label: string }[] {
  return [
    { value: 'markdown', label: 'Markdown' },
    { value: 'csharp', label: 'C#' },
    ...languages.map((l) => ({ value: l.id, label: l.displayName })),
  ];
}

/**
 * Applies a language-picker choice. The tag is cleared so the server computes a
 * fresh one for the new language — a tag that stays is a tag the file already
 * had, which we never rewrite.
 */
export function setCellLanguage(cell: EditorCell, value: string, languages: ApiLanguage[]): EditorCell {
  if (value === 'markdown') {
    return { ...cell, kind: 'markdown', tag: null, languageId: null };
  }
  if (value === 'csharp') {
    return { ...cell, kind: 'code', tag: 'csharp', languageId: null };
  }
  const language = languages.find((l) => l.id === value);
  return { ...cell, kind: 'code', tag: null, languageId: language?.id ?? null };
}

/** The cells a run button executes, in order. Markdown never runs. */
export function cellsToRun(
  cells: EditorCell[],
  index: number,
  mode: 'one' | 'before' | 'after' | 'all',
): EditorCell[] {
  const slice =
    mode === 'one'
      ? cells.slice(index, index + 1)
      : mode === 'before'
        ? cells.slice(0, index)
        : mode === 'after'
          ? cells.slice(index)
          : cells;
  return slice.filter((cell) => cell.kind === 'code');
}

export function moveCell(cells: EditorCell[], from: number, to: number): EditorCell[] {
  if (to < 0 || to >= cells.length || from === to) {
    return cells;
  }
  const next = [...cells];
  const [moved] = next.splice(from, 1);
  next.splice(to, 0, moved);
  return next;
}

export function insertCell(cells: EditorCell[], index: number, cell: EditorCell): EditorCell[] {
  const next = [...cells];
  next.splice(index, 0, cell);
  return next;
}

export function removeCell(cells: EditorCell[], index: number): EditorCell[] {
  return cells.filter((_, i) => i !== index);
}

export function emptyCell(kind: 'code' | 'markdown' = 'code'): EditorCell {
  return {
    id: newCellId(),
    kind,
    tag: kind === 'code' ? 'csharp' : null,
    languageId: null,
    source: '',
    blankLinesAfter: 1,
    closed: true,
  };
}

/** What gets sent to the server on save — editor-only fields dropped. */
export function toApiCells(cells: EditorCell[]): ApiCell[] {
  return cells.map((cell) => ({
    kind: cell.kind,
    tag: cell.tag ?? null,
    languageId: cell.languageId ?? null,
    source: cell.source,
    blankLinesAfter: cell.blankLinesAfter ?? 1,
    closed: cell.closed ?? true,
  }));
}

/**
 * Re-attaches the ids of the cells just saved to what the server read back, so
 * a save does not throw away every cell's output. Positional matching is safe
 * here and only here: this is a round trip of the array we ourselves posted. If
 * the server's re-parse disagrees about how many cells that is, ids are minted
 * fresh instead — orphaned run state beats run state shown against the wrong cell.
 */
export function keepIds(reloaded: ApiCell[], previous: EditorCell[]): EditorCell[] {
  const aligned = reloaded.length === previous.length;
  return reloaded.map((cell, i) => ({ ...cell, id: aligned ? previous[i].id : newCellId() }));
}

/**
 * What a run posts. Same cells as a save, but carrying their ids: the session
 * keys each cell's outputs by the id it received, so an id-less run would file
 * everything under its position in the request — "run cell five" alone would
 * come back as cell one.
 */
export function toRunCells(cells: EditorCell[]): ApiCell[] {
  return toApiCells(cells).map((cell, i) => ({ ...cell, id: cells[i].id }));
}

/** True when the notebook differs from what was loaded — drives the Save button. */
export function isDirty(cells: EditorCell[], saved: ApiCell[]): boolean {
  return JSON.stringify(toApiCells(cells)) !== JSON.stringify(toApiCells(withIds(saved)));
}

/** What one cell did in the session, as the editor renders it. */
export interface CellRunState {
  status: string;
  executionCount: number | null;
  outputs: NotebookOutput[];
  /** The cell was edited after this ran, so the output below it is no longer
   *  what the code says. Dimmed rather than dropped — the same call VS Code makes. */
  stale: boolean;
}

/**
 * Joins the cells on screen to what the session says they did. Cells the session
 * has never run simply have no entry; ids the session still holds for cells that
 * are gone are ignored.
 */
export function mergeStatus(
  cells: EditorCell[],
  session: ApiSession | null,
  ranSource: Record<string, string>,
): Record<string, CellRunState> {
  const merged: Record<string, CellRunState> = {};
  for (const cell of cells) {
    const run: ApiCellRun | undefined = session?.cells?.[cell.id];
    if (!run) {
      continue;
    }
    merged[cell.id] = {
      status: run.status,
      executionCount: run.executionCount ?? null,
      outputs: run.outputs ?? [],
      // `truncated` needs no rendering: the session already appends a visible
      // marker as the last output when it stops keeping them.
      stale: cell.id in ranSource && ranSource[cell.id] !== cell.source,
    };
  }
  return merged;
}
