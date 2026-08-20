import { useState } from 'react';
import Markdown from 'react-markdown';
import type { ApiLanguage } from '../api';
import { useCellEditor } from '../monaco/useMonaco';
import { languageOptions, monacoLanguage, type CellRunState, type EditorCell } from '../notebook';
import { Output } from './NotebookView';

export type RunMode = 'one' | 'before' | 'after';

interface Props {
  cell: EditorCell;
  index: number;
  count: number;
  languages: ApiLanguage[];
  /** What this cell did in the session, if it has run. */
  run: CellRunState | null;
  /** False when this deployment cannot execute — no git workflow, or a server
   *  bound beyond localhost with no API key. The buttons are hidden, not broken. */
  canRun: boolean;
  /** A run is in flight somewhere in this notebook; the kernel takes one at a time. */
  busy: boolean;
  onChange: (source: string) => void;
  onLanguage: (value: string) => void;
  onMove: (to: number) => void;
  onDelete: () => void;
  onRun: (mode: RunMode) => void;
}

/**
 * One notebook cell: a Monaco editor sized to its content, a language picker fed
 * by whatever the kernel declared, the structural controls, and — for code — the
 * run buttons and whatever the kernel last said about it. A markdown cell shows
 * its rendered prose until you click into it, the way a notebook reads.
 */
export function CellEditor({
  cell, index, count, languages, run, canRun, busy,
  onChange, onLanguage, onMove, onDelete, onRun,
}: Props) {
  const isMarkdown = cell.kind === 'markdown';
  const [editing, setEditing] = useState(false);
  const showPreview = isMarkdown && !editing && cell.source.trim().length > 0;

  return (
    <div className={`notebook-cell notebook-cell-${cell.kind}`}>
      <div className="cell-toolbar">
        {!isMarkdown && canRun && (
          <div className="cell-run">
            {/* Always visible — reaching for Run should not mean finding the
                cell first. The other two are rarer, so they wait for a hover. */}
            <button
              className="button button-small"
              onClick={() => onRun('one')}
              disabled={busy}
              title="Run this cell"
            >
              ▶
            </button>
            <span className="cell-run-more">
              <button
                className="button button-small"
                onClick={() => onRun('before')}
                // Nothing above cell one: the server rejects an empty run, so
                // the button says so first.
                disabled={busy || index === 0}
                title="Run every cell above this one"
              >
                ▶ above
              </button>
              <button
                className="button button-small"
                onClick={() => onRun('after')}
                disabled={busy}
                title="Run this cell and everything below it"
              >
                ▶ below
              </button>
            </span>
          </div>
        )}
        <span className="cell-number">
          {run?.executionCount != null ? `[${run.executionCount}]` : index + 1}
        </span>
        <select
          className="cell-language"
          value={isMarkdown ? 'markdown' : (cell.languageId ?? 'csharp')}
          onChange={(e) => onLanguage(e.target.value)}
          title="Cell language"
        >
          {languageOptions(languages).map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
        {/* The tag as written, when it differs from the language's own name —
            ```zsh against the shellscript language, say. */}
        {cell.tag && cell.tag !== cell.languageId && cell.tag !== 'csharp' && (
          <span className="chip chip-muted">{cell.tag}</span>
        )}
        {run && <span className={`badge badge-${run.status}`}>{run.status}</span>}
        <span className="spacer" />
        <div className="cell-actions">
          <button className="button button-small" onClick={() => onMove(index - 1)} disabled={index === 0} title="Move up">
            ↑
          </button>
          <button
            className="button button-small"
            onClick={() => onMove(index + 1)}
            disabled={index === count - 1}
            title="Move down"
          >
            ↓
          </button>
          <button className="button button-small button-danger" onClick={onDelete} title="Delete this cell">
            ✕
          </button>
        </div>
      </div>

      {showPreview ? (
        <div
          className="cell-preview"
          onDoubleClick={() => setEditing(true)}
          title="Double-click to edit"
        >
          <Markdown>{cell.source}</Markdown>
        </div>
      ) : (
        <CellBody
          cell={cell}
          isMarkdown={isMarkdown}
          onChange={onChange}
          onBlur={() => setEditing(false)}
        />
      )}

      {run && run.outputs.length > 0 && (
        <div
          className={run.stale ? 'cell-outputs cell-outputs-stale' : 'cell-outputs'}
          title={run.stale ? 'This cell changed since it ran — the output below is stale.' : undefined}
        >
          {run.outputs.map((output, i) => (
            <Output key={i} output={output} />
          ))}
        </div>
      )}
    </div>
  );
}

function CellBody({
  cell, isMarkdown, onChange, onBlur,
}: {
  cell: EditorCell;
  isMarkdown: boolean;
  onChange: (source: string) => void;
  onBlur: () => void;
}) {
  const language = isMarkdown ? 'markdown' : monacoLanguage(cell.languageId, cell.tag);
  const container = useCellEditor(language, cell.source, onChange);
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
  return (
    <div className={always ? 'cell-inserter cell-inserter-always' : 'cell-inserter'}>
      <button className="button button-small" onClick={() => onInsert('code')}>
        + Code
      </button>
      <button className="button button-small" onClick={() => onInsert('markdown')}>
        + Markdown
      </button>
    </div>
  );
}
