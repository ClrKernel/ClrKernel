import { useEffect, useState } from 'react';
import { ChevronDown, ChevronRight } from 'lucide-react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { api, isActive, type Run, type RunCell } from '../api';
import { rerunOutcome, rerunQuestion } from '../rerun';
import { jobRunsPath } from '../routes';
import { pairCells, type PairedCell } from '../runCells';
import { EnvBadge, ErrorBanner, StatusBadge, usePolling } from '../components/common';
import { NotebookView, Output } from '../components/NotebookView';
import { duration, timeAgo, type Notebook } from '../ipynb';

/** The tabs are routes, so a reload lands where you were. */
const TABS = ['cells', 'notebook', 'log'] as const;
type Tab = (typeof TABS)[number];

/**
 * One cell: a collapsed header — its first line — with the outputs open beneath
 * it. The output is what you came to read; the source is one click away.
 */
function CellBlock({ paired, total }: { paired: PairedCell; total: number }) {
  const { cell, source, outputs } = paired;
  const [open, setOpen] = useState(false);
  const tone = cell.status === 'Running' ? 'row-active' : cell.status === 'Failed' ? 'row-failed' : '';
  const Chevron = open ? ChevronDown : ChevronRight;
  return (
    <div className="rounded-md border border-border bg-card">
      <button
        type="button"
        onClick={() => setOpen(!open)}
        aria-expanded={open}
        className={`flex w-full items-center gap-3 px-3 py-2 text-left hover:bg-muted ${tone}`}
      >
        <Chevron className="size-3.5 shrink-0 text-muted-subtle" aria-hidden="true" />
        <span className="w-[52px] shrink-0 whitespace-nowrap font-mono text-code text-muted-subtle">
          {cell.cellIndex + 1}/{total}
        </span>
        <span className="w-[110px] shrink-0"><StatusBadge status={cell.status} /></span>
        <span className="min-w-0 flex-1 truncate font-mono text-code text-code-fg">
          {cell.sourcePreview}
        </span>
        <span className="shrink-0 whitespace-nowrap font-mono text-code text-muted-subtle">
          {duration(cell.startedAt, cell.finishedAt)}
        </span>
      </button>
      {open && (
        <pre className="cell-source m-0 border-t border-border bg-muted px-3 py-2 font-mono text-code text-code-fg">
          {source ?? cell.sourcePreview}
        </pre>
      )}
      {(outputs.length > 0 || cell.errorSummary) && (
        <div className="cell-outputs border-t border-border">
          {outputs.map((output, i) => <Output key={i} output={output} />)}
          {cell.errorSummary && (
            <div className="mt-1 font-mono text-code text-status-error">{cell.errorSummary}</div>
          )}
        </div>
      )}
    </div>
  );
}

function CellProgress({ cells, artifact }: { cells: RunCell[]; artifact: Notebook | null }) {
  if (cells.length === 0) {
    return <p className="text-base text-muted-foreground">No cells recorded for this run.</p>;
  }
  return (
    <div className="flex flex-col gap-2">
      {pairCells(cells, artifact).map((paired) => (
        <CellBlock key={paired.cell.cellIndex} paired={paired} total={cells.length} />
      ))}
    </div>
  );
}

export function RunDetail() {
  const { id, tab: tabParam } = useParams<{ id: string; tab: string }>();
  const navigate = useNavigate();
  const tab: Tab = (TABS as readonly string[]).includes(tabParam ?? '') ? (tabParam as Tab) : 'cells';
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
  const [rerunning, setRerunning] = useState(false);
  const [rerunNote, setRerunNote] = useState<string | null>(null);

  /**
   * Two buttons because they are two acts. "Run again" takes the branch as it is
   * now, which is what you want once something is fixed. "Run the recorded
   * version" goes back to the commit that failed, for reproducing it — offered
   * only when the recording is faithful enough to be worth the label, which the
   * server decides again and refuses out loud if it disagrees.
   */
  async function again(exactVersion: boolean) {
    if (!confirm(rerunQuestion([run!], exactVersion))) {
      return;
    }
    setRerunNote(null);
    setRerunning(true);
    try {
      const result = await api.rerun([run!.id], exactVersion);
      setRerunNote(rerunOutcome(result.started, result.refused));
    } catch (e) {
      setRerunNote((e as Error).message);
    } finally {
      setRerunning(false);
    }
  }

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
    return <p className="text-base text-muted-foreground">Loading…</p>;
  }

  return (
    <div className="flex h-full min-h-0 flex-col">
      <div className="mb-3 flex items-start justify-between gap-4">
        <h1 className="flex min-w-0 items-center gap-2 text-xl font-bold tracking-tight">
          <Link
            className="truncate text-primary hover:underline"
            to={jobRunsPath(run.project, run.environment, run.jobName)}
          >
            {run.jobName}
          </Link>
          <StatusBadge status={run.status} />
          {run.environment !== 'default' && <EnvBadge env={run.environment} />}
        </h1>
        <div className="flex shrink-0 items-center gap-2">
          {live ? (
            <>
              <span className="text-sm text-muted-subtle">live · refreshing</span>
              <Button variant="outline" size="sm" onClick={cancel} disabled={cancelling}>
                {cancelling ? 'Cancelling…' : 'Cancel run'}
              </Button>
            </>
          ) : (
            <>
              <Button variant="outline" size="sm" onClick={() => again(false)} disabled={rerunning}>
                {rerunning ? 'Starting…' : 'Run again'}
              </Button>
              {/* Only when there is a commit to go back to. A run with none, or
                  one that ran over uncommitted changes or ad-hoc parameters,
                  cannot be reproduced — the server refuses it and says which of
                  those it was, so nothing here has to guess. */}
              {run.commitSha && !run.wasDirty && !run.hadOverrides && (
                <Button variant="ghost" size="sm" onClick={() => again(true)} disabled={rerunning}>
                  Run the recorded version
                </Button>
              )}
            </>
          )}
        </div>
      </div>
      <ErrorBanner error={cancelError} />
      {rerunNote && (
        <Alert className="mb-3">
          <AlertDescription>{rerunNote}</AlertDescription>
        </Alert>
      )}

      <div className="mb-3 flex flex-wrap items-center gap-x-3.5 gap-y-1 text-sm text-muted-subtle">
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

      {/* One bar for the whole run, above the tabs — progress is a property of
          the run, not of whichever tab happens to be open. */}
      <div className="my-3 h-1 w-full overflow-hidden rounded-full bg-border">
        <div
          className={`h-full transition-[width] ${
            run.status === 'Failed' ? 'bg-status-error' : 'bg-primary'
          }`}
          style={{
            width: `${
              (data?.cells ?? []).length === 0
                ? 0
                : ((data?.cells ?? []).filter((c) => c.status === 'Succeeded').length /
                    (data?.cells ?? []).length) *
                  100
            }%`,
          }}
        />
      </div>

      <Tabs
        value={tab}
        onValueChange={(next) => navigate(`/runs/${id}/${next}`, { replace: true })}
        className="flex min-h-0 flex-1 flex-col"
      >
        <TabsList variant="line" className="mb-3 shrink-0">
          <TabsTrigger value="cells">Cells</TabsTrigger>
          <TabsTrigger value="notebook">Notebook</TabsTrigger>
          <TabsTrigger value="log">Log</TabsTrigger>
        </TabsList>

        <TabsContent value="cells">
          <CellProgress cells={data?.cells ?? []} artifact={artifact} />
        </TabsContent>
        <TabsContent value="notebook">
          {artifact ? (
            <NotebookView notebook={artifact} />
          ) : (
            <p className="text-base text-muted-foreground">
              {live ? 'The artifact is written when the run finishes.' : 'No artifact for this run.'}
            </p>
          )}
        </TabsContent>
        <TabsContent value="log" className="min-h-0 flex-1">
          {log ? (
            <pre className="output-text log h-full overflow-auto rounded-2xl border border-border bg-muted px-4 py-3.5 font-mono text-code leading-relaxed text-code-fg">
              {log}
            </pre>
          ) : (
            <p className="text-base text-muted-foreground">No log.</p>
          )}
        </TabsContent>
      </Tabs>
    </div>
  );
}
