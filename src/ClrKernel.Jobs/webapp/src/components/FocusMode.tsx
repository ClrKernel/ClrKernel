import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { CollapsedRail, StatusBadge } from './common';
import Markdown from 'react-markdown';
import type { ApiLanguage } from '../api';
import { useFocusEditor } from '../monaco/useMonaco';
import { useCanRun, useCanWrite } from '../sessionContext';
import {
  hasEditorServices,
  monacoLanguage,
  type CellRunState,
  type EditorCell,
} from '../notebook';
import { clamp, MAX_SIDEBAR, MIN_SIDEBAR, type LayoutPrefs } from '../prefs';
import { MIN_THUMBNAIL_WIDTH } from '../thumbnail';
import { buildToc, sectionIds, stepCell, visibleLeaves } from '../toc';
import { CellMenu } from './CellEditor';
import { LanguageOptions, languageLabelFor } from './common';
import { Output } from './NotebookView';
import { Splitter } from './Splitter';
import { ContentsViewToggle } from './ContentsViewToggle';
import { ThumbnailZoom } from './ThumbnailZoom';
import { useCellDrag } from './useCellDrag';
import { stepIndex } from '../reorder';
import { Thumbnails } from './Thumbnails';
import { TocTree } from './TocTree';

/**
 * The nearest ancestor that actually scrolls, or null for the window.
 *
 * Found by walking rather than by id: it stays correct if the shell's structure
 * changes, and it keeps this component from importing the shell just to learn
 * one string.
 */
function scrollParent(node: HTMLElement): HTMLElement | null {
  for (let el = node.parentElement; el != null; el = el.parentElement) {
    const overflow = getComputedStyle(el).overflowY;
    if (overflow === 'auto' || overflow === 'scroll') {
      return el;
    }
  }
  return null;
}


/** Shared empty set, so the thumbnail path allocates nothing per keystroke. */
const _nothingCollapsed: ReadonlySet<string> = new Set();

/** Neither pane may be squeezed to nothing by the splitter. */
const MIN_PANE = 80;

/**
 * One cell at a time: its editor above, its output below, a table of contents on
 * the left. The SSMS query/results shape, for working through a long notebook
 * without scrolling past everything else.
 *
 * The work area is fixed to the viewport, so the page itself never scrolls —
 * each pane scrolls on its own and neither moves the other.
 */
export function FocusMode({
  cells, path, languages, runState, activeId, canRun, busy, cleared, layout, clipboard,
  connectionType,
  onActivate, onChange, onLanguage, onRun, onClearOutput, onLayout, onDelete, onInsert,
  onMove, onCut, onCopy, onPaste,
}: {
  cells: EditorCell[];
  path: string;
  languages: ApiLanguage[];
  runState: Record<string, CellRunState>;
  activeId: string | null;
  canRun: boolean;
  busy: boolean;
  cleared: ReadonlySet<string>;
  /** The notebook's connection type, for marking dialects that cannot run on it. */
  connectionType?: string | null;
  layout: LayoutPrefs;
  onActivate: (cellId: string) => void;
  onChange: (cellId: string, source: string) => void;
  onLanguage: (cellId: string, value: string) => void;
  onRun: (cellId: string) => void;
  onClearOutput: (cellId: string) => void;
  onLayout: (layout: LayoutPrefs) => void;
  onDelete: (cellId: string) => void;
  onInsert: (afterId: string | null, kind: 'code' | 'markdown') => void;
  /** Reorder, from a drag in either view or from Alt+↑/↓ in the contents. */
  onMove: (from: number, to: number) => void;
  /** Something has been cut or copied, so Paste has somewhere to come from. */
  clipboard: boolean;
  onCut: (cellId: string) => void;
  onCopy: (cellId: string) => void;
  onPaste: (cellId: string, where: 'above' | 'below') => void;
}) {
  const work = useRef<HTMLDivElement | null>(null);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const toc = useMemo(() => buildToc(cells), [cells]);
  const active = cells.find((cell) => cell.id === activeId) ?? null;
  const activeRun = active == null ? null : (runState[active.id] ?? null);
  const outputs = active == null || cleared.has(active.id) ? [] : (activeRun?.outputs ?? []);
  const isMarkdown = active?.kind === 'markdown';
  const picked = isMarkdown ? 'markdown' : (active?.languageId ?? 'csharp');

  // A section that appears while collapsed state is keyed by cell id needs no
  // migration: unknown ids are simply expanded, which is the right default.
  const knownSections = useMemo(() => new Set(sectionIds(toc)), [toc]);
  useEffect(() => {
    setCollapsed((current) => {
      const next = new Set([...current].filter((id) => knownSections.has(id)));
      return next.size === current.size ? current : next;
    });
  }, [knownSections]);

  const step = useCallback(
    (delta: number) => {
      const next = stepCell(cells, activeId, delta);
      if (next != null) {
        onActivate(next);
      }
    },
    [cells, activeId, onActivate],
  );

  // Focus Mode is a reading layout too, so a viewer keeps it — without the
  // controls, and with the editor read-only.
  const canWrite = useCanWrite();
  const mayRun = useCanRun();
  // One drag for both views: the same gesture, the same arithmetic, and a
  // reorder that behaved differently depending on which view you were in would
  // be two features wearing one name. A viewer drags nothing.
  const drag = useCellDrag(onMove, canWrite);
  const { container: editorRef, relayout } = useFocusEditor({
    readOnly: !canWrite,
    binding: active == null ? null : {
      path,
      cellId: active.id,
      languageId: active.languageId ?? 'csharp-script',
      enabled: hasEditorServices(active.languageId, languages),
    },
    language: isMarkdown ? 'markdown' : monacoLanguage(active?.languageId, active?.tag, languages),
    value: active?.source ?? '',
    onChange: (source) => {
      if (active != null) {
        onChange(active.id, source);
      }
    },
    onRun: () => {
      if (active != null && canRun && !busy) {
        onRun(active.id);
      }
    },
    onRunAndAdvance: () => {
      if (active != null && canRun && !busy) {
        onRun(active.id);
      }
      step(1);
    },
    onStep: step,
  });

  // The work area ends at the bottom of the window. A CSS calc() would have to
  // guess the height of everything above it — toolbar, tabs, and banners that
  // come and go — and be wrong whenever one appears, leaving the output pane
  // hanging off the bottom of a page that is not allowed to scroll.
  useEffect(() => {
    const fit = () => {
      const node = work.current;
      if (node == null) {
        return;
      }
      // Measure against the scroll container, not the window. The shell makes
      // the content region the only thing that scrolls, so the document never
      // overflows and a documentElement-based correction would always be a
      // no-op — silently, which is the worst way for it to be wrong.
      const scroller = scrollParent(node);
      const top = node.getBoundingClientRect().top;
      const floor = scroller?.getBoundingClientRect().bottom ?? window.innerHeight;
      const wanted = Math.max(floor - top, 260);
      node.style.height = `${wanted}px`;
      // Whatever sits below — page padding, a footer, an ancestor's margin — is
      // somebody else's layout decision. Rather than enumerate it, take the
      // measurement the browser just made and give the excess back. One pass
      // converges because nothing below depends on this height.
      const excess = scroller
        ? scroller.scrollHeight - scroller.clientHeight
        : document.documentElement.scrollHeight - window.innerHeight;
      if (excess > 0) {
        node.style.height = `${Math.max(wanted - excess, 260)}px`;
      }
    };
    fit();
    window.addEventListener('resize', fit);
    // Banners appearing or disappearing move the work area without resizing the
    // window, so watch the page too.
    const observer = new ResizeObserver(fit);
    if (work.current?.parentElement != null) {
      observer.observe(work.current.parentElement);
    }
    return () => {
      window.removeEventListener('resize', fit);
      observer.disconnect();
    };
  }, []);

  // Monaco does not reflow when its container changes size, and a stale layout
  // is not merely ugly: the text is drawn in the old geometry while clicks are
  // mapped through the new one, so the caret lands a line or two off. Every
  // resize path ends here.
  useEffect(() => {
    const node = work.current;
    if (node == null) {
      return;
    }
    const observer = new ResizeObserver(() => relayout());
    observer.observe(node);
    window.addEventListener('resize', relayout);
    return () => {
      observer.disconnect();
      window.removeEventListener('resize', relayout);
    };
  }, [relayout]);

  useEffect(() => {
    relayout();
  }, [layout.sidebarWidth, layout.sidebarCollapsed, layout.splitRatio, relayout]);

  function onSidebarDrag(clientX: number): void {
    const left = work.current?.getBoundingClientRect().left ?? 0;
    const width = work.current?.getBoundingClientRect().width ?? MAX_SIDEBAR;
    onLayout({
      ...layout,
      sidebarCollapsed: false,
      sidebarWidth: clamp(clientX - left, MIN_SIDEBAR, Math.min(MAX_SIDEBAR, width * 0.4)),
    });
  }

  function onSplitDrag(clientY: number): void {
    const box = work.current?.getBoundingClientRect();
    if (box == null || box.height <= MIN_PANE * 2) {
      return;
    }
    const ratio = (clientY - box.top) / box.height;
    const min = MIN_PANE / box.height;
    onLayout({ ...layout, splitRatio: clamp(ratio, min, 1 - min) });
  }

  function onTreeKeyDown(event: React.KeyboardEvent): void {
    // Deliberately no Escape handler anywhere in this component: Esc belongs to
    // Monaco (dismiss suggest, exit find) and must never leave Focus Mode.
    if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp' && event.key !== 'Enter') {
      return;
    }
    event.preventDefault();

    // Alt+↑/↓ moves the cell rather than the selection — the keyboard half of
    // drag-to-reorder, without which a notebook could only be reordered with a
    // pointer. VS Code binds the same chord to moving a line; this is the same
    // idea one level up.
    if (event.altKey && event.key !== 'Enter') {
      const from = cells.findIndex((cell) => cell.id === activeId);
      const to = stepIndex(from, event.key === 'ArrowDown' ? 1 : -1, cells.length);
      if (from >= 0 && to !== from && canWrite) {
        onMove(from, to);
      }
      return;
    }
    if (event.key === 'Enter') {
      return; // the row is already active; Enter is the confirm that changes nothing
    }
    // Thumbnails have no collapsed sections, so every cell is reachable there.
    // Using the outline's collapsed set would make ↓ skip cells that are plainly
    // on screen.
    const leaves = visibleLeaves(
      toc, layout.contentsView === 'thumbnails' ? _nothingCollapsed : collapsed);
    const at = leaves.findIndex((leaf) => leaf.cellId === activeId);
    const next = leaves[clamp(at + (event.key === 'ArrowDown' ? 1 : -1), 0, leaves.length - 1)];
    if (next != null) {
      onActivate(next.cellId);
      (event.currentTarget as HTMLElement)
        .querySelector<HTMLElement>(`[data-cell="${next.cellId}"]`)
        ?.focus();
    }
  }

  const toggleSection = useCallback((id: string) => {
    setCollapsed((current) => {
      const next = new Set(current);
      if (!next.delete(id)) {
        next.add(id);
      }
      return next;
    });
  }, []);

  return (
    <div className="focus-shell" ref={work}>
      {layout.sidebarCollapsed ? (
        <CollapsedRail
          label="Show contents"
          onExpand={() => onLayout({ ...layout, sidebarCollapsed: false })}
        />
      ) : (
        <>
          <div className="focus-sidebar" style={{ width: layout.sidebarWidth }}>
            <div className="focus-sidebar-head">
              <span>Contents</span>
              <span className="spacer" />
              <ContentsViewToggle
                view={layout.contentsView}
                onView={(contentsView) => onLayout({
                  ...layout,
                  contentsView,
                  // A 4:3 preview in a 180px sidebar is a grey smudge. Widen
                  // rather than render something useless — and only ever wider,
                  // so switching back and forth cannot creep the sidebar along.
                  sidebarWidth: contentsView === 'thumbnails'
                    ? Math.max(layout.sidebarWidth, MIN_THUMBNAIL_WIDTH)
                    : layout.sidebarWidth,
                })}
              />
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="h-6 px-2 text-sm"
                title="Hide contents"
                aria-label="Hide contents"
                onClick={() => onLayout({ ...layout, sidebarCollapsed: true })}
              >
                ⟨
              </Button>
            </div>
            {cells.length === 0 ? (
              <p className="focus-empty">No cells yet.</p>
            ) : layout.contentsView === 'thumbnails' ? (
              <>
              <ThumbnailZoom
                zoom={layout.thumbnailZoom}
                onZoom={(thumbnailZoom) => onLayout({ ...layout, thumbnailZoom })}
              />
              <Thumbnails
                nodes={toc}
                activeId={activeId}
                runState={runState}
                languages={languages}
                width={layout.sidebarWidth}
                zoom={layout.thumbnailZoom}
                drag={drag}
                onActivate={onActivate}
                onKeyDown={onTreeKeyDown}
              />
              </>
            ) : (
              <TocTree
                nodes={toc}
                activeId={activeId}
                collapsed={collapsed}
                runState={runState}
                languages={languages}
                drag={drag}
                onActivate={onActivate}
                onToggle={toggleSection}
                onKeyDown={onTreeKeyDown}
              />
            )}
          </div>
          <Splitter
            orientation="vertical"
            label="Contents width"
            onDrag={onSidebarDrag}
            onReset={() => onLayout({ ...layout, sidebarWidth: 260 })}
          />
        </>
      )}

      <div className="focus-work">
        {active == null ? (
          <div className="focus-empty-state">
            <p>This notebook has no cells.</p>
            {canWrite && (
              <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={() => onInsert(null, 'code')}>
                + Code
              </Button>
            )}
          </div>
        ) : (
          <>
            <div className="focus-cell-toolbar">
              <span className="focus-cell-title">
                Cell [{activeRun?.executionCount ?? ' '}]
              </span>
              {canRun && mayRun && !isMarkdown && (
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                  disabled={busy}
                  onClick={() => onRun(active.id)}
                  title="Run this cell (⌘/Ctrl+Enter)"
                >
                  ▶ Run
                </Button>
              )}
              {activeRun && <StatusBadge status={activeRun.status} />}
              {activeRun?.stale && (
                <Badge variant="outline" className="font-normal" title="This cell changed since it ran">
                  edited since run
                </Badge>
              )}
              <span className="spacer" />
              {canWrite && (
              <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                onClick={() => onClearOutput(active.id)}
                title="Clear this cell's output"
              >
                Clear output
              </Button>
              )}
              {canWrite ? (
              <Select value={picked} onValueChange={(value) => onLanguage(active.id, value)}>
                <SelectTrigger size="sm" className="h-6 w-auto gap-1 border-0 bg-transparent px-1.5 text-sm shadow-none" aria-label="Cell language">
                  <SelectValue>{languageLabelFor(picked, languages)}</SelectValue>
                </SelectTrigger>
                <SelectContent>
                  <LanguageOptions languages={languages} providerType={connectionType} />
                </SelectContent>
              </Select>
              ) : (
                <span className="px-1.5 text-sm text-muted-subtle">
                  {languageLabelFor(picked, languages)}
                </span>
              )}
              {canWrite && (
              <>
              <Button variant="outline" size="sm" className="h-6 px-2 text-sm text-destructive hover:bg-destructive/10 hover:text-destructive"
                onClick={() => onDelete(active.id)}
                title="Delete this cell"
              >
                ✕
              </Button>
              {/* The same menu the cell list has. Focus Mode shows one cell at a
                  time, so a paste lands beside this one and the view follows it. */}
              <CellMenu
                clipboard={clipboard}
                onCut={() => onCut(active.id)}
                onCopy={() => onCopy(active.id)}
                onPaste={(where) => onPaste(active.id, where)}
              />
              </>
              )}
            </div>

            {/* Height comes from the split ratio alone, so long output can never
                push the editor around — the splitter is the only thing that
                sizes these. */}
            <div className="focus-editor-pane" style={{ flexBasis: `${layout.splitRatio * 100}%` }}>
              <div className="focus-editor" ref={editorRef} />
            </div>

            <Splitter
              orientation="horizontal"
              label="Editor and output"
              onDrag={onSplitDrag}
              onReset={() => onLayout({ ...layout, splitRatio: 0.5 })}
            />

            <OutputPane
              cellId={active.id}
              outputs={outputs}
              stale={activeRun?.stale ?? false}
              markdown={isMarkdown ? active.source : null}
            />
          </>
        )}
      </div>
    </div>
  );
}

/**
 * The results half. Sticks to the bottom while output streams in, and lets go
 * the moment you scroll up to read something — re-engaging only when you come
 * back to the bottom yourself.
 */
function OutputPane({
  cellId, outputs, stale, markdown,
}: {
  cellId: string;
  outputs: CellRunState['outputs'];
  stale: boolean;
  markdown: string | null;
}) {
  const box = useRef<HTMLDivElement | null>(null);
  const stick = useRef(true);

  // A new cell starts stuck to the bottom again; carrying the old cell's scroll
  // intent across would be surprising.
  useEffect(() => {
    stick.current = true;
    if (box.current != null) {
      box.current.scrollTop = 0;
    }
  }, [cellId]);

  useEffect(() => {
    const node = box.current;
    if (node != null && stick.current) {
      node.scrollTop = node.scrollHeight;
    }
  }, [outputs]);

  return (
    <div
      className={stale ? 'focus-output-pane cell-outputs-stale' : 'focus-output-pane'}
      ref={box}
      onScroll={() => {
        const node = box.current;
        if (node != null) {
          // Within a couple of pixels counts as "at the bottom": fractional
          // scroll heights mean an exact comparison is never true.
          stick.current = node.scrollHeight - node.scrollTop - node.clientHeight < 4;
        }
      }}
    >
      {markdown != null ? (
        <div className="cell-preview focus-markdown-preview">
          <Markdown>{markdown}</Markdown>
        </div>
      ) : outputs.length === 0 ? (
        <p className="focus-empty">No output — run this cell to see results.</p>
      ) : (
        outputs.map((output, i) => <Output key={i} output={output} />)
      )}
    </div>
  );
}
