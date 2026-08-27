import type { ApiCell, ApiCellRun, ApiLanguage, ApiSession, ApiSyncCell, TreeNode } from './api';
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
 *
 * `languages` is the kernel's descriptor list. A language that says which syntax
 * to highlight it with is believed: that is how three SQL dialects share one
 * tokenizer without this file learning their names. `grammarId` and not
 * `editorLanguageId` — the latter is an identity, distinct per language, and
 * Monaco has no grammar for it. The table below is the fallback for the kernel
 * languages that predate the field and for the moment before any descriptor has
 * arrived.
 */
export function monacoLanguage(
  languageId: string | null | undefined,
  tag?: string | null,
  languages: ApiLanguage[] = [],
): string {
  const id = (languageId ?? '').toLowerCase();
  const declared = languages.find((l) => l.id.toLowerCase() === id)?.grammarId;
  if (declared != null && KNOWN_TO_MONACO.has(declared)) {
    return declared;
  }
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
 * The Monaco languages this app is prepared to hand a cell model to.
 *
 * A gate rather than a list of what Monaco knows: `monaco/language.ts` registers
 * our LSP providers for exactly these ids, so a language asking for one outside
 * it would get a model with no completion, no hover and no diagnostics — worse
 * than the plaintext it would otherwise have fallen back to. A future dialect
 * that wants its own highlighter is added here and there together.
 */
const KNOWN_TO_MONACO = new Set(['csharp', 'sql', 'powershell', 'shell', 'plaintext', 'markdown']);

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
 * Whether asking the kernel about this cell is worth a round trip.
 *
 * C# has no descriptor — it is the engine's own language rather than a registered
 * cell language — and it is the case that matters most, so an absent id means yes.
 * A registered language answers for itself: Mermaid and HTTP say no, and asking
 * them would cost a request to be told nothing.
 */
export function hasEditorServices(
  languageId: string | null | undefined,
  languages: ApiLanguage[],
): boolean {
  if (!languageId) {
    return true;
  }
  const descriptor = languages.find((l) => l.id === languageId);
  return descriptor == null || descriptor.hasEditorServices !== false;
}

/**
 * Whether this path may be written on your own branch.
 *
 * Mirrors `NotebookTree.IsEditable`, which is the authority — the server refuses
 * the save either way. This exists so the editor can open a file read-only
 * instead of offering a Save that will be rejected.
 *
 * Everything else is browsable and readable. Widening it is a trust-boundary
 * decision rather than a convenience: a worktree contains its own `.git`, and
 * `.scratch` holds the query editor's buffer.
 */
export function fileEditable(path: string): boolean {
  const name = (path ?? '').toLowerCase();
  return name.endsWith('.jobs.yaml')
    || ['.nb.md', '.ipynb', '.dib', '.csx'].some((e) => name.endsWith(e));
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

/** One entry in the cell language picker. */
export interface LanguageOption {
  value: string;
  label: string;
  /** Secondary text under the label — the connection types this language's cells
   *  can run on. Absent for a language that is not provider-bound. */
  detail?: string;
}

/** Picker options, flat: Markdown, C#, then every language the kernel declared.
 *  Still the lookup everything uses to turn a cell's language into its label. */
export function languageOptions(languages: ApiLanguage[]): LanguageOption[] {
  return [
    { value: 'markdown', label: 'Markdown' },
    { value: 'csharp', label: 'C#' },
    ...languages.map((l) => ({
      value: l.id,
      label: l.displayName,
      detail: (l.supportedProviders ?? []).join(' · ') || undefined,
    })),
  ];
}

/** A heading in the picker and the options under it. `label` is null for the
 *  ones that belong to no group, which come first. */
export interface LanguageGroup {
  label: string | null;
  options: LanguageOption[];
}

/**
 * The same options, clustered the way the kernel says to.
 *
 * Grouping comes from each language's `category`, so three SQL dialects sit
 * together under one heading rather than scattered between C# and HTTP — and a
 * fourth arrives in the right place without this file being edited. Ungrouped
 * languages keep their order and come first, because Markdown and C# are what
 * most cells are.
 */
export function languageGroups(languages: ApiLanguage[]): LanguageGroup[] {
  const options = languageOptions(languages);
  const categoryOf = new Map(languages.map((l) => [l.id, l.category ?? null] as const));
  const groups: LanguageGroup[] = [{ label: null, options: [] }];

  for (const option of options) {
    const category = categoryOf.get(option.value) ?? null;
    if (category == null) {
      groups[0].options.push(option);
      continue;
    }
    const existing = groups.find((g) => g.label === category);
    if (existing == null) {
      groups.push({ label: category, options: [option] });
    } else {
      existing.options.push(option);
    }
  }
  return groups.filter((g) => g.options.length > 0);
}

/**
 * Whether a cell in this language can run on a connection of this `$type`.
 *
 * Unknown either way is not a refusal: a language whose descriptor has not
 * arrived, or a connection whose type nobody has said, is a question this cannot
 * answer — and answering "no" would put a warning on a cell that is perfectly
 * fine. The kernel refuses for real at run time and warns through diagnostics;
 * this only decides whether the picker marks the option.
 */
export function runsOnProvider(
  languageId: string | null | undefined,
  providerType: string | null | undefined,
  languages: ApiLanguage[],
): boolean {
  if (!languageId || !providerType) {
    return true;
  }
  const supported = languages.find((l) => l.id === languageId)?.supportedProviders;
  if (supported == null || supported.length === 0) {
    return true;
  }
  return supported.some((p) => p.toLowerCase() === providerType.toLowerCase());
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

/**
 * A copy of a cell for pasting. The id is always fresh: ids key the Monaco
 * model, the session's outputs and React's list, so two cells sharing one is
 * three separate kinds of wrong — and pasting twice from one copy is the
 * ordinary case, not an edge one.
 *
 * What is deliberately not copied is the run: a pasted cell has never run here.
 * Nothing carries it, because run state lives in the session under the id.
 */
export function copyOfCell(cell: EditorCell): EditorCell {
  return { ...cell, id: newCellId() };
}

/** How far back structural changes are kept. Deep enough to walk out of a
 *  mistake, shallow enough that the history is not a second notebook. */
export const UNDO_DEPTH = 50;

export function pushUndo(stack: EditorCell[][], cells: EditorCell[]): EditorCell[][] {
  return [...stack, cells].slice(-UNDO_DEPTH);
}

/**
 * The cells to go back to, given the snapshot taken before a structural change
 * and the notebook as it stands now.
 *
 * Undo here is structural — it puts back a deleted cell, an order, a language —
 * and a cell that still exists keeps the text it has *now*. Restoring the
 * snapshot wholesale would mean Ctrl+Z after deleting one cell quietly threw
 * away everything typed into the others since, and it would do it invisibly:
 * a surviving cell keeps its Monaco model, which is only seeded on creation, so
 * the screen would go on showing the text while the file was written without it.
 *
 * A cell that is gone is not in `current`, so it comes back with the text it had
 * when it left — which is the only copy left, its model having been disposed.
 */
export function restoreCells(snapshot: EditorCell[], current: EditorCell[]): EditorCell[] {
  const live = new Map(current.map((cell) => [cell.id, cell.source]));
  return snapshot.map(
    (cell) => (live.has(cell.id) ? { ...cell, source: live.get(cell.id)! } : cell));
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

/**
 * Every notebook in a tree, as paths relative to its root, depth-first.
 *
 * For the job form's notebook picker: a job's notebook has to exist on the branch
 * the job is written to, and the server refuses one that does not — so offering
 * the list beats letting somebody type a path and meet that refusal at save.
 * Jobs files are left out; a job runs a notebook, never another jobs file.
 */
export function notebookPaths(tree: TreeNode | null | undefined): string[] {
  const found: string[] = [];
  const walk = (nodes: TreeNode[]) => {
    for (const node of nodes) {
      if (node.isDirectory) {
        walk(node.children ?? []);
      } else if (node.kind === 'notebook') {
        found.push(node.path);
      }
    }
  };
  walk(tree?.children ?? []);
  return found;
}
