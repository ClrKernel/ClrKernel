import { useState } from 'react';
import Markdown from 'react-markdown';
import type { ApiLanguage } from '../api';
import { useCellEditor } from '../monaco/useMonaco';
import { languageOptions, monacoLanguage, type EditorCell } from '../notebook';

interface Props {
  cell: EditorCell;
  index: number;
  count: number;
  languages: ApiLanguage[];
  onChange: (source: string) => void;
  onLanguage: (value: string) => void;
  onMove: (to: number) => void;
  onDelete: () => void;
}

/**
 * One notebook cell: a Monaco editor sized to its content, a language picker fed
 * by whatever the kernel declared, and the structural controls. A markdown cell
 * shows its rendered prose until you click into it — the way a notebook reads —
 * and returns to prose when focus leaves. Run buttons and output land here next.
 */
export function CellEditor({
  cell, index, count, languages, onChange, onLanguage, onMove, onDelete,
}: Props) {
  const isMarkdown = cell.kind === 'markdown';
  const [editing, setEditing] = useState(false);
  const showPreview = isMarkdown && !editing && cell.source.trim().length > 0;

  return (
    <div className={`notebook-cell notebook-cell-${cell.kind}`}>
      <div className="cell-toolbar">
        <span className="cell-number">{index + 1}</span>
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
