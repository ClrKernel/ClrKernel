import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, isActive, type Run, type RunCell } from '../api';
import { duration, timeAgo, type Notebook } from '../ipynb';
import { ErrorBanner, StatusBadge, usePolling } from '../components/common';
import { NotebookView } from '../components/NotebookView';

/** The step-by-step view: one row per code cell, updated live while the run is in flight. */
function CellProgress({ cells }: { cells: RunCell[] }) {
  if (cells.length === 0) {
    return <p className="muted">No cells recorded for this run.</p>;
  }
  const done = cells.filter((c) => c.status === 'Succeeded').length;
  return (
    <>
      <div className="progress">
        <div className="progress-bar" style={{ width: `${(done / cells.length) * 100}%` }} />
      </div>
      <table className="table">
        <tbody>
          {cells.map((cell) => (
            <tr key={cell.cellIndex} className={cell.status === 'Running' ? 'row-active' : undefined}>
              <td className="cell-index">
                {cell.cellIndex + 1}/{cells.length}
              </td>
              <td>
                <StatusBadge status={cell.status} />
              </td>
              <td>
                <code className="preview">{cell.sourcePreview}</code>
                {cell.errorSummary && <div className="error-text">{cell.errorSummary}</div>}
              </td>
              <td className="muted">{duration(cell.startedAt, cell.finishedAt)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

export function RunDetail() {
  const { id } = useParams<{ id: string }>();
  const [artifact, setArtifact] = useState<Notebook | null>(null);
  const [log, setLog] = useState<string>('');
  const [tab, setTab] = useState<'progress' | 'notebook' | 'log'>('progress');

  const { data, error } = usePolling<{ run: Run; cells: RunCell[] }>(
    () => api.run(id!),
    // Poll while it is running; stop once the run has settled.
    2000,
    [id],
  );
  const run = data?.run;
  const live = run ? isActive(run.status) : true;
  const [cancelling, setCancelling] = useState(false);
  const [cancelError, setCancelError] = useState<string | null>(null);

  async function cancel() {
    setCancelError(null);
    setCancelling(true);
    try {
      // Cancellation is per job: the scheduler kills that job's running kernel.
      await api.cancelJob(run!.jobName);
    } catch (e) {
      setCancelError((e as Error).message);
    } finally {
      setCancelling(false);
    }
  }

  // Artifact and log are written when the run finishes; fetch once it settles.
  useEffect(() => {
    if (!run || isActive(run.status)) {
      return;
    }
    api
      .artifact(run.id)
      .then((n) => setArtifact(n as Notebook))
      .catch(() => setArtifact(null));
    api.log(run.id).then(setLog).catch(() => setLog(''));
  }, [run?.id, run?.status]);

  if (error) {
    return <ErrorBanner error={error} />;
  }
  if (!run) {
    return <p className="muted">Loading…</p>;
  }

  return (
    <div>
      <div className="row-between">
        <h1>
          <Link to={`/jobs/${encodeURIComponent(run.jobName)}`}>{run.jobName}</Link>{' '}
          <StatusBadge status={run.status} />
        </h1>
        {live && (
          <div className="row-gap">
            <span className="muted">live · refreshing</span>
            <button className="button button-danger" onClick={cancel} disabled={cancelling}>
              {cancelling ? 'Cancelling…' : 'Cancel run'}
            </button>
          </div>
        )}
      </div>
      <ErrorBanner error={cancelError} />

      <div className="meta">
        <span>{run.notebookPath}</span>
        <span>{run.trigger}</span>
        {run.attempt > 1 && <span>attempt {run.attempt}</span>}
        <span>started {timeAgo(run.startedAt ?? run.createdAt)}</span>
        <span>took {duration(run.startedAt, run.finishedAt)}</span>
        {run.causedByRunId && (
          <Link to={`/runs/${run.causedByRunId}`}>triggered by an upstream run</Link>
        )}
      </div>
      {run.errorSummary && <div className="banner banner-error">{run.errorSummary}</div>}

      <div className="tabs">
        <button className={tab === 'progress' ? 'active' : ''} onClick={() => setTab('progress')}>
          Cells
        </button>
        <button className={tab === 'notebook' ? 'active' : ''} onClick={() => setTab('notebook')}>
          Notebook
        </button>
        <button className={tab === 'log' ? 'active' : ''} onClick={() => setTab('log')}>
          Log
        </button>
      </div>

      {tab === 'progress' && <CellProgress cells={data?.cells ?? []} />}
      {tab === 'notebook' &&
        (artifact ? (
          <NotebookView notebook={artifact} />
        ) : (
          <p className="muted">
            {live ? 'The artifact is written when the run finishes.' : 'No artifact for this run.'}
          </p>
        ))}
      {tab === 'log' && (log ? <pre className="output-text log">{log}</pre> : <p className="muted">No log.</p>)}
    </div>
  );
}
