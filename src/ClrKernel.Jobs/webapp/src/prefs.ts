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

export interface LayoutPrefs {
  sidebarWidth: number;
  sidebarCollapsed: boolean;
  /** Editor pane's share of the work area, 0-1. */
  splitRatio: number;
  /** The file explorer down the left of the editor — a different panel from the
   *  Focus-mode contents sidebar above, and separately sized. */
  explorerWidth: number;
  explorerCollapsed: boolean;
}

export const DEFAULT_LAYOUT: LayoutPrefs = {
  sidebarWidth: 240,
  sidebarCollapsed: false,
  splitRatio: 0.5,
  explorerWidth: 218,
  explorerCollapsed: false,
};

export interface NotebookState {
  mode?: 'normal' | 'focus';
  activeCellId?: string;
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
    splitRatio: clamp(layout.splitRatio, 0.1, 0.9),
    explorerWidth: clamp(layout.explorerWidth, MIN_EXPLORER, MAX_EXPLORER),
    explorerCollapsed: layout.explorerCollapsed === true,
  };
}

export function saveLayout(layout: LayoutPrefs): void {
  write(GLOBAL_KEY, layout);
}

export const MIN_SIDEBAR = 180;
export const MAX_SIDEBAR = 640;

export const MIN_EXPLORER = 150;
export const MAX_EXPLORER = 420;

export function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(Math.max(value, min), max) : min;
}

/** Per-notebook state, keyed by path inside one object so it is one entry. */
export function loadNotebookState(path: string): NotebookState {
  const all = read<Record<string, NotebookState>>(NOTEBOOK_KEY, {});
  return all[path] ?? {};
}

export function saveNotebookState(path: string, state: NotebookState): void {
  const all = read<Record<string, NotebookState>>(NOTEBOOK_KEY, {});
  write(NOTEBOOK_KEY, { ...all, [path]: { ...all[path], ...state } });
}
