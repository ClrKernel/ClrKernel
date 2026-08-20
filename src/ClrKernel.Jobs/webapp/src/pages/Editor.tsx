import { useEffect, useRef, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { ApiError, api, type ApiCell, type ApiLanguage } from '../api';
import { CellEditor, CellInserter, type RunMode } from '../components/CellEditor';
import { ErrorBanner, usePolling } from '../components/common';
import { useCellEditor } from '../monaco/useMonaco';
import {
  cellsToRun,
  emptyCell,
  insertCell,
  isDirty,
  mergeStatus,
  moveCell,
  removeCell,
  setCellLanguage,
  toApiCells,
  toRunCells,
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
  const [pollFast, setPollFast] = useState(false);
  const [restartDismissed, setRestartDismissed] = useState(false);
  // The source each cell had when it was last run, so an edit can dim its output
  // instead of silently leaving a result that no longer matches the code.
  const ranSource = useRef<Record<string, string>>({});

  const { data: promotion, reload: reloadPromotion } = usePolling(
    () => api.promotionStatus(path),
    null,
    [path],
  );

  // Fast while a run is in flight, not at all when idle — polling a warm kernel
  // that is doing nothing is pure noise. TryStartRun takes its slot before the
  // 202 comes back, so the first poll after a run always sees it as running.
  const { data: session, error: sessionError, reload: reloadSession } = usePolling(
    () => api.sessionStatus(path),
    pollFast ? 400 : null,
    [path],
  );

  useEffect(() => {
    if (pollFast && session && !session.running) {
      setPollFast(false);
    }
  }, [session, pollFast]);

  // Execution is gated server-side (git workflow, dev only, and a key required
  // off localhost). One rejected status call is how the editor finds out.
  const canRun = isNotebook && sessionError == null;
  const running = (session?.running ?? false) || pollFast;
  const runState = mergeStatus(cells ?? [], session, ranSource.current);

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

  /**
   * Runs a slice of the notebook against the warm kernel. What runs is decided
   * here and sent as an ordered list, so the server never learns which button
   * was pressed — one endpoint covers cell, above, below and all.
   */
  async function run(index: number, mode: RunMode | 'all') {
    const toRun = cellsToRun(cells ?? [], index, mode);
    if (toRun.length === 0) {
      setNotice('Nothing to run there — those cells are all prose.');
      return;
    }
    setError(null);
    setNotice(null);
    try {
      await api.runCells(path, toRunCells(toRun));
      for (const cell of toRun) {
        ranSource.current[cell.id] = cell.source;
      }
      setPollFast(true);
    } catch (e) {
      if (e instanceof ApiError && e.status === 409) {
        // Expected, not a failure: the kernel runs one cell at a time.
        setNotice(e.message);
        setPollFast(true);
        return;
      }
      setError((e as Error).message);
    }
  }

  async function restartKernel() {
    setError(null);
    try {
      await api.restartSession(path);
      // ponytail: outputs live only in the session, so a restart clears them.
      // Cache them client-side if keeping them across a restart ever matters.
      ranSource.current = {};
      setRestartDismissed(false);
      setNotice('Kernel restarted — variables and cell outputs are cleared.');
      reloadSession();
    } catch (e) {
      setError((e as Error).message);
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

  function insertAt(index: number, kind: 'code' | 'markdown') {
    setCells((current) => insertCell(current ?? [], index, emptyCell(kind)));
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
            {canRun ? (
              <div className="notebook-toolbar">
                <button className="button" onClick={() => run(0, 'all')} disabled={running}>
                  ▶ Run All
                </button>
                <button
                  className="button"
                  onClick={restartKernel}
                  title="Kills the kernel. This is also the only way to stop a cell that will not finish."
                >
                  ⟳ Restart kernel {running && '(stops the running cell)'}
                </button>
                <span className="spacer" />
                <span className={running ? 'chip chip-running' : 'chip chip-muted'}>
                  {running
                    ? 'running…'
                    : session?.started
                      ? `${session.kernel ?? 'kernel'} ${session.version ?? ''} · idle`
                      : 'kernel not started'}
                </span>
              </div>
            ) : (
              isNotebook && (
                <p className="muted small">
                  Running cells is unavailable here: {sessionError}
                </p>
              )
            )}

            {session?.scheduledRunActive && (
              <div className="banner banner-warn">
                A scheduled run of this notebook is in flight. It executes in its own kernel from
                the committed file, so what you run here does not affect it — but saving now
                changes what the <em>next</em> run picks up.
              </div>
            )}

            {session?.kernelRestarted && !restartDismissed && (
              <div className="banner banner-warn">
                The kernel exited on its own and was replaced — variables from earlier cells are
                gone. Re-run the cells you need.{' '}
                <button className="button button-small" onClick={() => setRestartDismissed(true)}>
                  Dismiss
                </button>
              </div>
            )}

            <CellInserter always={cells.length === 0} onInsert={(kind) => insertAt(0, kind)} />
            {cells.map((cell, index) => (
              <div key={cell.id}>
                <CellEditor
                  cell={cell}
                  index={index}
                  count={cells.length}
                  languages={languages}
                  run={runState[cell.id] ?? null}
                  canRun={canRun}
                  busy={running}
                  onChange={(value) => update(index, (c) => ({ ...c, source: value }))}
                  onLanguage={(value) => update(index, (c) => setCellLanguage(c, value, languages))}
                  onMove={(to) => setCells((current) => (current ? moveCell(current, index, to) : current))}
                  onDelete={() => setCells((current) => (current ? removeCell(current, index) : current))}
                  onRun={(mode) => run(index, mode)}
                />
                <CellInserter
                  always={index === cells.length - 1}
                  onInsert={(kind) => insertAt(index + 1, kind)}
                />
              </div>
            ))}
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
        Every save commits to the dev branch. Cells you run here execute in a warm kernel that is
        dropped after 30 idle minutes; those runs never appear in run history and never count
        towards promotion. Run the notebook's jobs from the <Link to="/jobs">Jobs</Link> page —
        promotion unlocks when every job on this notebook has a clean green run of exactly this
        content.
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
