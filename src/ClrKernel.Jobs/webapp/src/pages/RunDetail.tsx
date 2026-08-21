import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { api, isActive, type Run, type RunCell } from '../api';
import { ErrorBanner, StatusBadge, usePolling } from '../components/common';
import { NotebookView } from '../components/NotebookView';
import { duration, timeAgo, type Notebook } from '../ipynb';

/** The step-by-step view: one row per code cell, updated live while the run is in flight. */
function CellProgress({ cells }: { cells: RunCell[] }) {
  if (cells.length === 0) {
    return <p className="text-sm text-muted-foreground">No cells recorded for this run.</p>;
  }
  const done = cells.filter((c) => c.status === 'Succeeded').length;
  return (
    <>
      <div className="mb-3 h-1 w-full overflow-hidden rounded-full bg-muted">
        <div
          className="h-full bg-primary transition-[width]"
          style={{ width: `${(done / cells.length) * 100}%` }}
        />
      </div>
      <div className="table-box">
        <table className="table">
          <tbody>
            {cells.map((cell) => (
              <tr key={cell.cellIndex} className={cell.status === 'Running' ? 'row-active' : undefined}>
                <td className="whitespace-nowrap font-mono text-xs text-muted-foreground">
                  {cell.cellIndex + 1}/{cells.length}
                </td>
                <td>
                  <StatusBadge status={cell.status} />
                </td>
                <td>
                  <code className="preview">{cell.sourcePreview}</code>
                  {cell.errorSummary && (
                    <div className="mt-1 font-mono text-xs text-status-error">
                      {cell.errorSummary}
                    </div>
                  )}
                </td>
                <td className="whitespace-nowrap text-muted-foreground">
                  {duration(cell.startedAt, cell.finishedAt)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

export function RunDetail() {
  const { id } = useParams<{ id: string }>();
  const [artifact, setArtifact] = useState<Notebook | null>(null);
  const [log, setLog] = useState<string>('');

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
      await api.cancelJob(run!.environment, run!.jobName);
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
    api
      .log(run.id)
      .then(setLog)
      .catch(() => setLog(''));
  }, [run?.id, run?.status]);

  if (error) {
    return <ErrorBanner error={error} />;
  }
  if (!run) {
    return <p className="text-sm text-muted-foreground">Loading…</p>;
  }

  return (
    <div>
      <div className="mb-3 flex items-start justify-between gap-4">
        <h1 className="flex min-w-0 items-center gap-2 text-lg font-semibold tracking-tight">
          <Link
            className="truncate text-primary hover:underline"
            to={`/jobs/${run.environment}/${encodeURIComponent(run.jobName)}`}
          >
            {run.jobName}
          </Link>
          <StatusBadge status={run.status} />
          {run.environment !== 'default' && (
            <Badge variant="secondary" className="font-mono text-[11px]">
              {run.environment}
            </Badge>
          )}
        </h1>
        {live && (
          <div className="flex shrink-0 items-center gap-2">
            <span className="text-xs text-muted-foreground">live · refreshing</span>
            <Button variant="outline" size="sm" onClick={cancel} disabled={cancelling}>
              {cancelling ? 'Cancelling…' : 'Cancel run'}
            </Button>
          </div>
        )}
      </div>
      <ErrorBanner error={cancelError} />

      <div className="mb-3 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-muted-foreground">
        <span className="font-mono">{run.notebookPath}</span>
        <span>{run.trigger}</span>
        {run.attempt > 1 && <span>attempt {run.attempt}</span>}
        <span>started {timeAgo(run.startedAt ?? run.createdAt)}</span>
        <span>took {duration(run.startedAt, run.finishedAt)}</span>
        {run.causedByRunId && (
          <Link className="text-primary hover:underline" to={`/runs/${run.causedByRunId}`}>
            triggered by an upstream run
          </Link>
        )}
      </div>

      {run.errorSummary && (
        <Alert variant="destructive" className="mb-3">
          <AlertDescription className="text-destructive">{run.errorSummary}</AlertDescription>
        </Alert>
      )}

      <Tabs defaultValue="progress">
        <TabsList className="mb-3">
          <TabsTrigger value="progress">Cells</TabsTrigger>
          <TabsTrigger value="notebook">Notebook</TabsTrigger>
          <TabsTrigger value="log">Log</TabsTrigger>
        </TabsList>

        <TabsContent value="progress">
          <CellProgress cells={data?.cells ?? []} />
        </TabsContent>
        <TabsContent value="notebook">
          {artifact ? (
            <NotebookView notebook={artifact} />
          ) : (
            <p className="text-sm text-muted-foreground">
              {live ? 'The artifact is written when the run finishes.' : 'No artifact for this run.'}
            </p>
          )}
        </TabsContent>
        <TabsContent value="log">
          {log ? (
            <pre className="output-text log">{log}</pre>
          ) : (
            <p className="text-sm text-muted-foreground">No log.</p>
          )}
        </TabsContent>
      </Tabs>
    </div>
  );
}
