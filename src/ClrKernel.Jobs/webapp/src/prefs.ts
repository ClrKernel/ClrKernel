/**
 * UI preferences, in localStorage beside the API key.
 *
 * Two scopes, deliberately: how the workspace is *shaped* is global — you want
 * the sidebar the width you dragged it to, in every notebook — while which cell
 * you were on, and whether you were focusing, belong to the notebook you were
 * in. Nothing here is ever written to the notebook file.
 */

const GLOBAL_KEY = 'clrkernel-jobs-layout';
const NOTEBOOK_KEY = 'clrkernel-jobs-notebook-state';
const BRANCH_KEY = 'clrkernel-jobs-branch';

/** Which way the Focus-mode contents sidebar reads its cells. */
export type ContentsView = 'outline' | 'thumbnails';

export interface LayoutPrefs {
  sidebarWidth: number;
  sidebarCollapsed: boolean;
  /** Outline or thumbnails. Beside the width and the collapsed flag because it
   *  is the same kind of choice: how this panel is set up, everywhere. */
  contentsView: ContentsView;
  /** Editor pane's share of the work area, 0-1. */
  splitRatio: number;
  /** The file explorer down the left of the editor — a different panel from the
   *  Focus-mode contents sidebar above, and separately sized. */
  explorerWidth: number;
  explorerCollapsed: boolean;
  /** The Connections area's own two panes. Its own numbers rather than the
   *  editor's: it is a different screen, and dragging the query editor taller has
   *  no business making the notebook editor taller too. */
  connectionsSplit: number;
  connectionsTreeWidth: number;
}

export const DEFAULT_LAYOUT: LayoutPrefs = {
  sidebarWidth: 240,
  sidebarCollapsed: false,
  contentsView: 'outline',
  splitRatio: 0.5,
  explorerWidth: 218,
  explorerCollapsed: false,
  connectionsSplit: 0.45,
  connectionsTreeWidth: 280,
};

export interface NotebookState {
  mode?: 'normal' | 'focus';
  /**
   * Which cell you were on, as its position in the notebook.
   *
   * Not its id. Ids are minted per page load — `n1`, `n2`, … from a counter that
   * restarts — so a stored one means nothing after a refresh, and worse, it can
   * mean something wrong: open a different notebook first and the counter hands
   * `n3` to a completely different cell, so the id that was meant to restore
   * your place lands you somewhere arbitrary instead. A position survives a
   * refresh, and when the file has changed underneath it is approximately right,
   * which is what "put me back where I was" asks for.
   */
  activeCell?: number;
}

// A corrupt or hand-edited value must not take the editor down with it: every
// read falls back to the default rather than throwing on parse.
function read<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    return raw == null ? fallback : { ...fallback, ...(JSON.parse(raw) as T) };
  } catch {
    return fallback;
  }
}

function write(key: string, value: unknown): void {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Private browsing, or a full quota. Layout is not worth an error.
  }
}

export function loadLayout(): LayoutPrefs {
  const layout = read(GLOBAL_KEY, DEFAULT_LAYOUT);
  return {
    // Clamp on read, not just on write: the stored value may predate a change to
    // these bounds, and a sidebar wider than the window cannot be dragged back.
    sidebarWidth: clamp(layout.sidebarWidth, MIN_SIDEBAR, MAX_SIDEBAR),
    sidebarCollapsed: layout.sidebarCollapsed === true,
    contentsView: layout.contentsView === 'thumbnails' ? 'thumbnails' : 'outline',
    splitRatio: clamp(layout.splitRatio, 0.1, 0.9),
    explorerWidth: clamp(layout.explorerWidth, MIN_EXPLORER, MAX_EXPLORER),
    explorerCollapsed: layout.explorerCollapsed === true,
    connectionsSplit: clamp(layout.connectionsSplit, 0.15, 0.85),
    connectionsTreeWidth: clamp(layout.connectionsTreeWidth, MIN_TREE, MAX_TREE),
  };
}

export function saveLayout(layout: LayoutPrefs): void {
  write(GLOBAL_KEY, layout);
}

export const MIN_SIDEBAR = 180;
export const MAX_SIDEBAR = 640;

export const MIN_EXPLORER = 150;
export const MAX_EXPLORER = 420;

/** Object names run long, so the connection tree is allowed to be wider than the
 *  file explorer. */
export const MIN_TREE = 180;
export const MAX_TREE = 640;

export function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(Math.max(value, min), max) : min;
}

/** Per-notebook state, keyed by path inside one object so it is one entry. */
export function loadNotebookState(path: string): NotebookState {
  const all = read<Record<string, NotebookState>>(NOTEBOOK_KEY, {});
  return all[path] ?? {};
}

export function saveNotebookState(path: string, state: NotebookState): void {
  const all = read<Record<string, NotebookState & { activeCellId?: string }>>(NOTEBOOK_KEY, {});
  // `activeCellId` was the previous spelling of `activeCell`, and it is dropped
  // on the first write rather than left sitting in everybody's browser looking
  // like something that is still read.
  const { activeCellId: _dropped, ...kept } = all[path] ?? {};
  write(NOTEBOOK_KEY, { ...all, [path]: { ...kept, ...state } });
}

/**
 * Which branch you were last browsing, per project.
 *
 * Per project because they are different sets of files with the same three
 * names, and remembering one answer for all of them would mean the wrong one
 * everywhere but where you set it.
 *
 * Deliberately *not* used by the editor: the branch is a segment of its URL, so
 * a link always says which copy it means. Resolving it against whatever the
 * person opening it last browsed would make one link a different file per
 * reader — the same argument that put the project in the path.
 */
export function loadBranch(project: string): string | null {
  return read<Record<string, string>>(BRANCH_KEY, {})[project] ?? null;
}

export function saveBranch(project: string, branch: string): void {
  write(BRANCH_KEY, { ...read<Record<string, string>>(BRANCH_KEY, {}), [project]: branch });
}
