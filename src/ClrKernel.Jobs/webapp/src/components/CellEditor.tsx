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
  onInsertAfter: () => void;
}

/**
 * One notebook cell: a Monaco editor sized to its content, a language picker fed
 * by whatever the kernel declared, and the structural controls. Output and run
 * buttons land here in the next phase.
 */
export function CellEditor({
  cell, index, count, languages, onChange, onLanguage, onMove, onDelete, onInsertAfter,
}: Props) {
  const language = cell.kind === 'markdown' ? 'markdown' : monacoLanguage(cell.languageId, cell.tag);
  const container = useCellEditor(language, cell.source, onChange);
  const selected =
    cell.kind === 'markdown' ? 'markdown' : (cell.languageId ?? 'csharp');

  return (
    <div className={`notebook-cell notebook-cell-${cell.kind}`}>
      <div className="cell-toolbar">
        <select
          className="cell-language"
          value={selected}
          onChange={(e) => onLanguage(e.target.value)}
          title="Cell language"
        >
          {languageOptions(languages).map((option) => (
            <option key={option.value} value={option.value}>
              {option.label}
            </option>
          ))}
        </select>
        {cell.tag && cell.tag !== selected && <span className="chip chip-muted">{cell.tag}</span>}
        <span className="spacer" />
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
        <button className="button button-small" onClick={onInsertAfter} title="Insert a cell below">
          +
        </button>
        <button className="button button-small button-danger" onClick={onDelete} title="Delete this cell">
          ✕
        </button>
      </div>
      <div className="cell-editor" ref={container} />
    </div>
  );
}
