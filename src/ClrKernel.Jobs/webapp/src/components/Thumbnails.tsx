import { useCallback, useEffect, useState } from 'react';
import type { ApiLanguage } from '../api';
import { colorize, colorized } from '../monaco/colorize';
import { monacoLanguage, type CellRunState, type EditorCell } from '../notebook';
import type { TocNode } from '../toc';
import { SCALE, previewSource, thumbnailBox, visibleLines } from '../thumbnail';
import { LanguageChip, StatusDot } from './TocTree';

/**
 * The Contents sidebar's thumbnail view: one column of small syntax-coloured
 * previews, in document order.
 *
 * What a thumbnail is for is **shape recognition, not reading**. At this size
 * the text is a few pixels tall whatever you do, so nothing here tries to make
 * it legible: it renders the first few lines at a fixed small scale and clips,
 * and the language chip and run state do the identifying. That is also why every
 * preview is the same scale — scaling each cell to fit would make a long one
 * microscopic and a short one enormous, and the column would stop reading as a
 * column.
 *
 * No Monaco editor is created for any of this. `colorize` runs the tokenizer and
 * returns markup; twenty editors in a sidebar would make scrolling miserable and
 * burn memory for previews nobody can read.
 */
export function Thumbnails({
  nodes, activeId, runState, languages, width, onActivate, onKeyDown,
}: {
  /** The same tree the outline renders, flattened here — sections become sticky
   *  headers so heading structure is not lost in a flat list. */
  nodes: TocNode[];
  activeId: string | null;
  runState: Record<string, CellRunState>;
  languages: ApiLanguage[];
  /** The sidebar's width, which decides the box and how many lines fit. */
  width: number;
  onActivate: (cellId: string) => void;
  onKeyDown: (event: React.KeyboardEvent) => void;
}) {
  const box = thumbnailBox(width);
  const lines = visibleLines(width);

  return (
    <div
      className="focus-thumbs"
      role="tree"
      aria-label="Notebook contents"
      onKeyDown={onKeyDown}
    >
      {nodes.map((node) => (
        <ThumbnailNode
          key={node.kind === 'leaf' ? node.cellId : `s-${node.id}`}
          node={node}
          activeId={activeId}
          runState={runState}
          languages={languages}
          box={box}
          lines={lines}
          onActivate={onActivate}
        />
      ))}
    </div>
  );
}

function ThumbnailNode({
  node, activeId, runState, languages, box, lines, onActivate,
}: {
  node: TocNode;
  activeId: string | null;
  runState: Record<string, CellRunState>;
  languages: ApiLanguage[];
  box: { width: number; height: number };
  lines: number;
  onActivate: (cellId: string) => void;
}) {
  if (node.kind === 'leaf') {
    return (
      <Thumbnail
        cell={node.cell}
        label={node.label}
        title={node.title}
        active={node.cellId === activeId}
        run={runState[node.cellId] ?? null}
        languages={languages}
        box={box}
        lines={lines}
        onActivate={onActivate}
      />
    );
  }
  return (
    <>
      {/* Sticky rather than a group box: a flat column of previews loses the
          heading structure entirely, and a header that scrolls away with its
          section tells you where you are only while you are already there. */}
      <div className="focus-thumbs-section" title={node.label}>{node.label}</div>
      {node.children.map((child) => (
        <ThumbnailNode
          key={child.kind === 'leaf' ? child.cellId : `s-${child.id}`}
          node={child}
          activeId={activeId}
          runState={runState}
          languages={languages}
          box={box}
          lines={lines}
          onActivate={onActivate}
        />
      ))}
    </>
  );
}

function Thumbnail({
  cell, label, title, active, run, languages, box, lines, onActivate,
}: {
  cell: EditorCell;
  label: string;
  title: string;
  active: boolean;
  run: CellRunState | null;
  languages: ApiLanguage[];
  box: { width: number; height: number };
  lines: number;
  onActivate: (cellId: string) => void;
}) {
  const language = cell.kind === 'markdown'
    ? 'markdown'
    : monacoLanguage(cell.languageId, cell.tag, languages);
  const source = previewSource(cell.source, lines);
  const { html, attach } = useColorized(cell.id, language, source);

  return (
    <div
      role="treeitem"
      aria-selected={active}
      // Only the active one is tabbable, so Tab reaches the column once and the
      // arrow keys take over inside it — the same rule the outline follows.
      tabIndex={active ? 0 : -1}
      data-cell={cell.id}
      className={`focus-thumb${active ? ' focus-thumb-active' : ''}`}
      title={title || label}
      onClick={() => onActivate(cell.id)}
    >
      <div className="focus-thumb-head">
        <StatusDot run={run} kind={cell.kind} />
        <LanguageChip cell={cell} languages={languages} />
        <span className="focus-thumb-count">
          {cell.kind === 'markdown' ? '' : `[${run?.executionCount ?? ' '}]`}
        </span>
        <span className="focus-thumb-label">{label}</span>
      </div>
      <div
        ref={attach}
        className="focus-thumb-box"
        style={{ width: box.width, height: box.height }}
        // The preview is decoration: the row's accessible name is the cell's
        // label and its language, which is what a reader needs. "Thumbnail"
        // would say nothing about which cell this is.
        aria-hidden="true"
      >
        {html == null
          ? <div className="focus-thumb-pending" />
          : (
            <pre
              className="focus-thumb-code"
              style={{
                transform: `scale(${SCALE})`,
                // Top left, so every preview starts at the same place and the
                // column's left edge stays straight.
                transformOrigin: 'top left',
                width: `${100 / SCALE}%`,
              }}
              // Monaco's own output: spans carrying .mtkN classes whose colours
              // come from the stylesheet it maintains for the current theme.
              dangerouslySetInnerHTML={{ __html: html }}
            />
          )}
      </div>
    </div>
  );
}

/**
 * The colorized HTML for one preview, rendered when it is near the viewport.
 *
 * Lazily, because `colorize` is cheap and a two-hundred-cell notebook is not:
 * running the tokenizer two hundred times on open is a visible stall for a
 * sidebar most of which is scrolled out of sight. An IntersectionObserver with a
 * generous margin means a thumbnail is ready before you scroll to it.
 *
 * Returns undefined until it is: the caller draws a placeholder of the right
 * size, so nothing reflows when the real thing arrives.
 */
function useColorized(
  cellId: string, language: string, source: string,
): { html: string | undefined; attach: (node: HTMLElement | null) => void } {
  // The synchronous read first: a cell whose preview is already cached — because
  // you scrolled past it, or switched views — must not flash a placeholder.
  const [html, setHtml] = useState(() => colorized(cellId, language, source));
  // State rather than a ref, because the effect below has to re-run when the
  // element arrives: a ref assignment does not re-render, so an observer set up
  // against `null` on the first pass would never watch anything.
  const [node, setNode] = useState<HTMLElement | null>(null);
  const attach = useCallback((next: HTMLElement | null) => setNode(next), []);

  useEffect(() => {
    const cached = colorized(cellId, language, source);
    if (cached != null) {
      setHtml(cached);
      return;
    }
    setHtml(undefined);

    let live = true;
    const start = () => {
      void colorize(cellId, language, source).then((result) => {
        if (live) {
          setHtml(result);
        }
      });
    };
    if (node == null || typeof IntersectionObserver !== 'function') {
      start();
      return () => { live = false; };
    }
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) {
        observer.disconnect();
        start();
      }
    }, { rootMargin: '400px' });
    observer.observe(node);
    return () => {
      live = false;
      observer.disconnect();
    };
  }, [cellId, language, source, node]);

  return { html, attach };
}
