import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { api, type ApiCell, type ApiLanguage } from '../api';
import { CellEditor } from '../components/CellEditor';
import { ErrorBanner, usePolling } from '../components/common';
import { useCellEditor } from '../monaco/useMonaco';
import {
  emptyCell,
  insertCell,
  isDirty,
  moveCell,
  removeCell,
  setCellLanguage,
  toApiCells,
  withIds,
  type EditorCell,
} from '../notebook';

type Tab = 'notebook' | 'source' | 'diff';

/**
 * The dev notebook editor: cells with syntax highlighting and a language picker,
 * a raw-source escape hatch, and the diff that shows what promotion would ship.
 * Every save is a commit on the dev branch — and a save that changes nothing is
 * skipped, because a needless commit invalidates the notebook's promotion
 * evidence.
 */
export function Editor() {
  const [search] = useSearchParams();
  const path = search.get('path') ?? '';
  const isNotebook = /\.(nb\.)?md$/i.test(path);

  const [cells, setCells] = useState<EditorCell[] | null>(null);
  const [saved, setSaved] = useState<ApiCell[]>([]);
  const [languages, setLanguages] = useState<ApiLanguage[]>([]);
  const [source, setSource] = useState<string | null>(null);
  const [savedSource, setSavedSource] = useState<string | null>(null);
  const [diff, setDiff] = useState('');
  const [tab, setTab] = useState<Tab>(isNotebook ? 'notebook' : 'source');
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const { data: promotion, reload: reloadPromotion } = usePolling(
    () => api.promotionStatus(path),
    null,
    [path],
  );

  const dirty = tab === 'source' || !isNotebook
    ? source != null && source !== savedSource
    : cells != null && isDirty(cells, saved);

  useEffect(() => {
    setError(null);
    api
      .notebookContent('dev', path)
      .then((text) => {
        setSource(text);
        setSavedSource(text);
      })
      .catch(() => setError(`Could not load ${path}.`));
    if (isNotebook) {
      api
        .notebookCells('dev', path)
        .then((result) => {
          setCells(withIds(result.cells));
          setSaved(result.cells);
          setLanguages(result.languages ?? []);
        })
        .catch((e) => setError((e as Error).message));
    }
  }, [path, isNotebook]);

  async function save() {
    setError(null);
    setNotice(null);
    if (!dirty) {
      setNotice('Nothing changed — nothing to commit.');
      return;
    }
    setBusy(true);
    try {
      const result = tab === 'source' || !isNotebook
        ? await api.saveNotebookContent(path, source ?? '')
        : await api.saveNotebookCells(path, toApiCells(cells ?? []));
      setNotice(`Saved and committed (${result.commitSha.slice(0, 8)}).`);
      // Re-read: the server is the authority on how cells serialize.
      const text = await api.notebookContent('dev', path);
      setSource(text);
      setSavedSource(text);
      if (isNotebook) {
        const reloaded = await api.notebookCells('dev', path);
        setCells(withIds(reloaded.cells));
        setSaved(reloaded.cells);
      }
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function showDiff() {
    setDiff(await api.gitDiff(path));
    setTab('diff');
  }

  async function promote() {
    if (!confirm(`Promote ${path} to production?`)) {
      return;
    }
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      const result = await api.promote(path);
      setNotice(
        `Promoted to production (${result.commitSha.slice(0, 8)}). The prod scheduler picks it up on its next tick.`,
      );
      reloadPromotion();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  function update(index: number, change: (cell: EditorCell) => EditorCell) {
    setCells((current) => current?.map((cell, i) => (i === index ? change(cell) : cell)) ?? current);
  }

  return (
    <div>
      <div className="row-between">
        <h1>
          <code>{path}</code> <span className="chip env-dev">dev</span>
        </h1>
        <div className="row-gap">
          <button className="button button-primary" onClick={save} disabled={busy || !dirty}>
            {dirty ? 'Save (commits to dev)' : 'Saved'}
          </button>
          <button
            className="button"
            onClick={promote}
            disabled={busy || !promotion?.eligible}
            title={promotion?.eligible ? 'Ship to production' : promotion?.reasons.join('\n')}
          >
            {promotion?.isDeletion ? 'Promote deletion' : 'Promote to production'}
          </button>
        </div>
      </div>
      <ErrorBanner error={error} />
      {notice && <div className="banner banner-ok">{notice}</div>}

      {promotion && !promotion.eligible && (
        <div className="banner banner-warn">
          <strong>Not promotable yet</strong>
          <ul>
            {promotion.reasons.map((reason) => (
              <li key={reason}>{reason}</li>
            ))}
          </ul>
        </div>
      )}

      <div className="tabs">
        {isNotebook && (
          <button className={tab === 'notebook' ? 'active' : ''} onClick={() => setTab('notebook')}>
            Notebook
          </button>
        )}
        <button className={tab === 'source' ? 'active' : ''} onClick={() => setTab('source')}>
          Source
        </button>
        <button className={tab === 'diff' ? 'active' : ''} onClick={showDiff}>
          Diff vs production
        </button>
      </div>

      {tab === 'notebook' &&
        (cells == null ? (
          <p className="muted">Loading…</p>
        ) : (
          <div className="notebook-editor">
            {cells.map((cell, index) => (
              <CellEditor
                key={cell.id}
                cell={cell}
                index={index}
                count={cells.length}
                languages={languages}
                onChange={(value) => update(index, (c) => ({ ...c, source: value }))}
                onLanguage={(value) => update(index, (c) => setCellLanguage(c, value, languages))}
                onMove={(to) => setCells((current) => (current ? moveCell(current, index, to) : current))}
                onDelete={() => setCells((current) => (current ? removeCell(current, index) : current))}
                onInsertAfter={() =>
                  setCells((current) => (current ? insertCell(current, index + 1, emptyCell()) : current))
                }
              />
            ))}
            <div className="row-gap">
              <button
                className="button button-small"
                onClick={() => setCells((current) => [...(current ?? []), emptyCell()])}
              >
                + Code
              </button>
              <button
                className="button button-small"
                onClick={() => setCells((current) => [...(current ?? []), emptyCell('markdown')])}
              >
                + Markdown
              </button>
            </div>
          </div>
        ))}

      {tab === 'source' &&
        (source == null ? <p className="muted">Loading…</p> : <SourceEditor value={source} onChange={setSource} />)}

      {tab === 'diff' &&
        (diff ? (
          <pre className="output-text log">{diff}</pre>
        ) : (
          <p className="muted">No differences — dev and production are identical for this file.</p>
        ))}

      <p className="muted">
        Every save commits to the dev branch. Run the notebook's jobs from the{' '}
        <Link to="/jobs">Jobs</Link> page; promotion unlocks when every job on this notebook has a
        clean green run of exactly this content.
      </p>
    </div>
  );
}

/** The whole file as one editor — the fallback for non-notebooks, and the escape
 *  hatch when you want to see exactly what is on disk. */
function SourceEditor({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const container = useCellEditor('markdown', value, onChange);
  return <div className="source-editor" ref={container} />;
}
