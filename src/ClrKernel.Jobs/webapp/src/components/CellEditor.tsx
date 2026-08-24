import { useEffect, useRef, useState } from 'react';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { StatusBadge } from './common';
import Markdown from 'react-markdown';
import type { ApiLanguage } from '../api';
import type { LspDiagnostic } from '../monaco/lsp';
import { useCellEditor } from '../monaco/useMonaco';
import { useCanRun, useCanWrite } from '../sessionContext';
import {
  connectableLanguage,
  hasEditorServices,
  languageOptions,
  monacoLanguage,
  type CellRunState,
  type EditorCell,
} from '../notebook';
import { Output } from './NotebookView';

export type RunMode = 'one' | 'before' | 'after';

interface Props {
  cell: EditorCell;
  index: number;
  count: number;
  languages: ApiLanguage[];
  /** The notebook this cell belongs to — the kernel's sessions are keyed by it,
   *  so a language request has to name it. */
  path: string;
  /** What the kernel says is wrong in this cell. */
  diagnostics?: LspDiagnostic[];
  /** What this cell did in the session, if it has run. */
  run: CellRunState | null;
  /** False when this deployment cannot execute — no git workflow, or a server
   *  bound beyond localhost with no API key. The buttons are hidden, not broken. */
  canRun: boolean;
  /** A run is in flight somewhere in this notebook; the kernel takes one at a time. */
  busy: boolean;
  /** True while this cell's outputs are hidden by "Clear output". */
  cleared: boolean;
  onChange: (source: string) => void;
  onLanguage: (value: string) => void;
  onMove: (to: number) => void;
  onDelete: () => void;
  onRun: (mode: RunMode) => void;
  onClearOutput: () => void;
  onConnect: () => void;
}

/**
 * One notebook cell, laid out the way a VS Code notebook is: a gutter down the
 * left carrying the run button and the execution count, the editor itself, and a
 * footer with what the cell did on one side and how it is configured on the
 * other. A markdown cell shows its rendered prose until you click into it.
 */
export function CellEditor({
  cell, index, count, languages, path, diagnostics, run, canRun, busy, cleared,
  onChange, onLanguage, onMove, onDelete, onRun, onClearOutput, onConnect,
}: Props) {
  const isMarkdown = cell.kind === 'markdown';
  const [editing, setEditing] = useState(false);
  const showPreview = isMarkdown && !editing && cell.source.trim().length > 0;
  const connectable = connectableLanguage(cell.languageId, languages);
  const outputs = cleared ? [] : (run?.outputs ?? []);
  // Viewers get the same notebook without the levers. The server refuses these
  // routes anyway; hiding them is so nobody reaches for something that will fail.
  const canWrite = useCanWrite();
  const mayRun = useCanRun();

  return (
    <div className={`notebook-cell notebook-cell-${cell.kind}${run ? ` cell-${run.status}` : ''}`}>
      <div className="cell-main">
        {/* The gutter: run at the top beside the first line, the execution count
            at the bottom, exactly where a notebook puts them. Code cells only —
            a markdown cell has neither, and an empty 40px column beside prose is
            just a margin that looks like a mistake. */}
        {!isMarkdown && (
          <div className="cell-gutter-bar">
            {canRun && mayRun ? (
              <button
                className="cell-run-button"
                onClick={() => onRun('one')}
                disabled={busy}
                title="Run this cell"
              >
                ▶
              </button>
            ) : (
              <span className="cell-run-spacer" />
            )}
            <span
              className="cell-gutter-index"
              title={run?.executionCount != null ? 'Execution count' : 'Not run yet'}
            >
              {`[${run?.executionCount ?? ' '}]`}
            </span>
          </div>
        )}

        <div className="cell-content">
          {/* Structural actions float over the editor and appear on hover, so a
              resting cell is code and nothing else. */}
          <div className="cell-float-actions">
            {!isMarkdown && canRun && mayRun && (
              <>
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                  onClick={() => onRun('before')}
                  disabled={busy || index === 0}
                  title="Run every cell above this one"
                >
                  ▶ above
                </Button>
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                  onClick={() => onRun('after')}
                  disabled={busy}
                  title="Run this cell and everything below it"
                >
                  ▶ below
                </Button>
              </>
            )}
            {canWrite && (
              <>
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={() => onMove(index - 1)} disabled={index === 0} title="Move up">
                  ↑
                </Button>
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                  onClick={() => onMove(index + 1)}
                  disabled={index === count - 1}
                  title="Move down"
                >
                  ↓
                </Button>
                <Button variant="outline" size="sm" className="h-6 px-2 text-sm text-destructive hover:bg-destructive/10 hover:text-destructive" onClick={onDelete} title="Delete this cell">
                  ✕
                </Button>
              </>
            )}
          </div>

          {showPreview ? (
            <div className="cell-preview" onDoubleClick={() => setEditing(true)} title="Double-click to edit">
              <Markdown>{cell.source}</Markdown>
            </div>
          ) : (
            <CellBody
              cell={cell}
              isMarkdown={isMarkdown}
              path={path}
              languages={languages}
              diagnostics={diagnostics}
              onChange={onChange}
              onBlur={() => setEditing(false)}
            />
          )}

          <div className="cell-footer">
            <span className="cell-footer-status">
              {run && <StatusBadge status={run.status} />}
              {run?.stale && <Badge variant="outline" className="font-normal" title="This cell changed since it ran">edited since run</Badge>}
            </span>
            <span className="spacer" />
            {connectable && canWrite && (
              <Button variant="outline" size="sm" className="h-6 px-2 text-sm"
                onClick={onConnect}
                title={`Build a ${connectable.displayName} connection directive`}
              >
                ⛁ Connect
              </Button>
            )}
            {canWrite ? (
              <Select
                value={isMarkdown ? 'markdown' : (cell.languageId ?? 'csharp')}
                onValueChange={onLanguage}
              >
                <SelectTrigger size="sm" className="h-6 w-auto gap-1 border-0 bg-transparent px-1.5 text-sm shadow-none" aria-label="Cell language">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  {languageOptions(languages).map((option) => (
                    <SelectItem key={option.value} value={option.value}>
                      {option.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            ) : (
              // A viewer sees which language a cell is, but cannot change it.
              <span className="px-1.5 text-sm text-muted-subtle">
                {languageOptions(languages).find(
                  (o) => o.value === (isMarkdown ? 'markdown' : cell.languageId ?? 'csharp'),
                )?.label ?? cell.languageId}
              </span>
            )}
            {/* The tag as written, when it differs from the language's own name —
                ```zsh against the shellscript language, say. */}
            {cell.tag && cell.tag !== cell.languageId && cell.tag !== 'csharp' && (
              <Badge variant="outline" className="font-mono font-normal">{cell.tag}</Badge>
            )}
          </div>
        </div>
      </div>

      {outputs.length > 0 && (
        <div className={run?.stale ? 'cell-outputs cell-outputs-stale' : 'cell-outputs'}>
          <OutputMenu onClear={onClearOutput} />
          {outputs.map((output, i) => (
            <Output key={i} output={output} />
          ))}
        </div>
      )}
    </div>
  );
}

/** Roughly how tall the menu is, for deciding whether it fits below the button. */
const _menuHeight = 48;

/**
 * The "…" on an output block, where anything acting on results rather than on
 * code belongs.
 *
 * The popup is positioned `fixed` from the button's own rect rather than absolutely
 * inside the block. An absolute popup is clipped by the first scrolling or hidden
 * ancestor, and it has two: the output area scrolls (`overflow: auto` for long
 * results) and the cell hides overflow (for its rounded corners). No z-index
 * escapes that — leaving the container does.
 */
function OutputMenu({ onClear }: { onClear: () => void }) {
  const [at, setAt] = useState<{ top: number; left: number } | null>(null);
  const box = useRef<HTMLDivElement | null>(null);
  const button = useRef<HTMLButtonElement | null>(null);

  useEffect(() => {
    if (at == null) {
      return;
    }
    // Any click outside closes it — including one on another cell's menu, which
    // is what keeps two from being open at once.
    function onDown(event: MouseEvent) {
      if (!box.current?.contains(event.target as Node)) {
        setAt(null);
      }
    }
    // A fixed popup does not travel with the page, so scrolling or resizing
    // closes it rather than leaving it stranded over something unrelated.
    const close = () => setAt(null);
    document.addEventListener('mousedown', onDown);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      document.removeEventListener('mousedown', onDown);
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [at]);

  function toggle() {
    if (at != null || !button.current) {
      setAt(null);
      return;
    }
    const rect = button.current.getBoundingClientRect();
    const below = rect.bottom + 4;
    setAt({
      // Near the bottom of the window it opens upward instead of off-screen.
      top: below + _menuHeight > window.innerHeight ? rect.top - _menuHeight : below,
      left: rect.left,
    });
  }

  return (
    <div className="output-menu" ref={box}>
      <button className="output-menu-button" ref={button} onClick={toggle} title="Output actions">
        …
      </button>
      {at && (
        <div className="output-menu-items" style={{ top: at.top, left: at.left }}>
          <button
            onClick={() => {
              onClear();
              setAt(null);
            }}
          >
            Clear output
          </button>
        </div>
      )}
    </div>
  );
}

function CellBody({
  cell, isMarkdown, path, languages, diagnostics, onChange, onBlur,
}: {
  cell: EditorCell;
  isMarkdown: boolean;
  path: string;
  languages: ApiLanguage[];
  diagnostics?: LspDiagnostic[];
  onChange: (source: string) => void;
  onBlur: () => void;
}) {
  const language = isMarkdown ? 'markdown' : monacoLanguage(cell.languageId, cell.tag);
  // Markdown cells get no binding at all, so completion never fires on prose.
  // For the rest, the id the kernel knows the language by — never Monaco's, which
  // calls a C# cell "csharp" and would reach no language service at all.
  const container = useCellEditor(language, cell.source, onChange, !useCanWrite(),
    isMarkdown ? undefined : {
      path,
      cellId: cell.id,
      languageId: cell.languageId ?? 'csharp-script',
      enabled: hasEditorServices(cell.languageId, languages),
      diagnostics,
    });
  return <div className="cell-editor" ref={container} onBlur={onBlur} />;
}

/**
 * The gap between two cells: hovering reveals the insert buttons, so a notebook
 * can grow anywhere rather than only at the end.
 */
export function CellInserter({
  onInsert,
  always = false,
}: {
  onInsert: (kind: 'code' | 'markdown') => void;
  always?: boolean;
}) {
  if (!useCanWrite()) {
    return null;
  }
  return (
    <div className={always ? 'cell-inserter cell-inserter-always' : 'cell-inserter'}>
      <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={() => onInsert('code')}>
        + Code
      </Button>
      <Button variant="outline" size="sm" className="h-6 px-2 text-sm" onClick={() => onInsert('markdown')}>
        + Markdown
      </Button>
    </div>
  );
}
