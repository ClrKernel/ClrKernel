import type { EditorCell } from './notebook';

/**
 * The notebook's table of contents: sections from markdown headings, every cell
 * a leaf under the nearest preceding one.
 *
 * React-free and Monaco-free so the shapes below can be tested directly — the
 * same rule notebook.ts follows. All of the fiddly cases live here (cells before
 * any heading, headings that skip a level, headings deeper than the tree draws)
 * rather than in the component.
 */

/** How deep the tree indents before it gives up and flattens. */
export const MAX_HEADING_DEPTH = 3;

export interface TocLeaf {
  kind: 'leaf';
  cellId: string;
  /** Position in the notebook, which is also the order ↑/↓ and "run and advance" use. */
  index: number;
  cell: EditorCell;
  label: string;
  /** The full line the label was truncated from, for a title tooltip. */
  title: string;
}

export interface TocSection {
  kind: 'section';
  /** Stable across edits that do not change this heading's cell. */
  id: string;
  label: string;
  /** 1-3; deeper headings are clamped so indentation stays readable. */
  depth: number;
  children: TocNode[];
}

export type TocNode = TocSection | TocLeaf;

/** The heading level of a markdown cell, or 0 when it does not start with one. */
export function headingLevel(cell: EditorCell): number {
  if (cell.kind !== 'markdown') {
    return 0;
  }
  const first = cell.source.split('\n').find((line) => line.trim().length > 0) ?? '';
  const match = /^(#{1,6})\s+\S/.exec(first.trim());
  return match == null ? 0 : match[1].length;
}

export function headingText(cell: EditorCell): string {
  const first = cell.source.split('\n').find((line) => line.trim().length > 0) ?? '';
  return first.trim().replace(/^#{1,6}\s+/, '').trim();
}

/** Longest a TOC label runs before it is cut; the full line rides the tooltip. */
const MAX_LABEL = 44;

/**
 * What a leaf says. For code, the first line that is actually code — a cell that
 * opens with a comment block would otherwise be labelled with prose that says
 * nothing about what it does. For markdown, the heading or the first line.
 */
export function leafLabel(cell: EditorCell): { label: string; title: string } {
  const lines = cell.source.split('\n').map((line) => line.trim());
  const meaningful =
    cell.kind === 'code'
      ? (lines.find((line) => line.length > 0 && !isComment(line)) ??
         lines.find((line) => line.length > 0))
      : lines.find((line) => line.length > 0);
  const title = (meaningful ?? '').replace(/^#{1,6}\s+/, '');
  if (title.length === 0) {
    return { label: cell.kind === 'code' ? '(empty)' : '(empty markdown)', title: '' };
  }
  return { label: title.length > MAX_LABEL ? `${title.slice(0, MAX_LABEL - 1)}…` : title, title };
}

// Comment openers across the languages a cell can be, so a labelled cell shows
// the code rather than the note above it. `#!` is excluded on purpose: it opens a
// directive, not a comment, and "#!sql-connect --name prod" is exactly the line
// worth showing.
function isComment(line: string): boolean {
  if (line.startsWith('#!')) {
    return false;
  }
  return ['//', '#', '--', '/*', '*'].some((opener) => line.startsWith(opener));
}

/**
 * Builds the tree. A heading cell becomes a section AND stays a leaf under it, so
 * clicking the heading in the tree can still open that markdown cell — the spec
 * asks for a section header that expands, and for every cell to be reachable.
 */
export function buildToc(cells: EditorCell[]): TocNode[] {
  const root: TocNode[] = [];
  // The section each depth is currently collecting into; index 0 is the root.
  const open: TocSection[] = [];

  const childrenFor = (depth: number): TocNode[] => {
    while (open.length > 0 && open[open.length - 1].depth >= depth) {
      open.pop();
    }
    return open.length === 0 ? root : open[open.length - 1].children;
  };

  cells.forEach((cell, index) => {
    const level = headingLevel(cell);
    const { label, title } = leafLabel(cell);
    const leaf: TocLeaf = { kind: 'leaf', cellId: cell.id, index, cell, label, title };
    if (level === 0) {
      (open.length === 0 ? root : open[open.length - 1].children).push(leaf);
      return;
    }
    // Deeper than the tree draws: keep the cell, drop the extra nesting.
    const depth = Math.min(level, MAX_HEADING_DEPTH);
    const section: TocSection = {
      kind: 'section',
      id: cell.id,
      label: headingText(cell) || label,
      depth,
      children: [leaf],
    };
    childrenFor(depth).push(section);
    open.push(section);
  });

  return root;
}

/** Every section id in a tree — what "expand all" starts from. */
export function sectionIds(nodes: TocNode[]): string[] {
  const ids: string[] = [];
  const walk = (list: TocNode[]) => {
    for (const node of list) {
      if (node.kind === 'section') {
        ids.push(node.id);
        walk(node.children);
      }
    }
  };
  walk(nodes);
  return ids;
}

/**
 * The leaves in the order they are drawn, which is notebook order — what ↑/↓ and
 * "run and advance" step through. Collapsed sections are skipped, because moving
 * the selection somewhere invisible is not moving it anywhere.
 */
export function visibleLeaves(nodes: TocNode[], collapsed: ReadonlySet<string>): TocLeaf[] {
  const leaves: TocLeaf[] = [];
  const walk = (list: TocNode[]) => {
    for (const node of list) {
      if (node.kind === 'leaf') {
        leaves.push(node);
      } else if (!collapsed.has(node.id)) {
        walk(node.children);
      }
    }
  };
  walk(nodes);
  return leaves;
}

/**
 * The cell to make active after the current one goes away — the next one, or the
 * previous when it was last. Null only when nothing is left.
 */
export function neighbourCell(cells: EditorCell[], removedIndex: number): string | null {
  if (cells.length === 0) {
    return null;
  }
  return cells[Math.min(removedIndex, cells.length - 1)].id;
}

/** The next or previous cell in notebook order, staying put at the ends. */
export function stepCell(cells: EditorCell[], activeId: string | null, delta: number): string | null {
  if (cells.length === 0) {
    return null;
  }
  const current = cells.findIndex((cell) => cell.id === activeId);
  if (current < 0) {
    return cells[0].id;
  }
  const next = Math.min(Math.max(current + delta, 0), cells.length - 1);
  return cells[next].id;
}
