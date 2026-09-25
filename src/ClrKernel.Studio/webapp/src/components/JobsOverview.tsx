import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { History, Pause, Play, Plus, Square, Trash2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import {
  DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu';
import { CheckboxField, Field, FieldRow } from '@/components/ui/field';
import { Input } from '@/components/ui/input';
import { api, isActive, type JobsFileState } from '../api';
import { timeAgo } from '../ipynb';
import { StatusBadge, usePolling } from './common';
import {
  addJob, readJobsFile, removeJob, setJobField, setJobParameter, type JobView,
} from '../jobsFile';
import { notebookParameters, type NotebookParameter } from '../parameters';
import { CronField } from './CronField';

/**
 * A `*.jobs.yaml` as a form: one card per job.
 *
 * It edits the **text**, not a model of its own. Every change goes through
 * `jobsFile.ts` and comes back as YAML, which is the same buffer the YAML tab
 * shows and the same bytes autosave writes, diff compares and push sends. Two
 * views of one file rather than two models that have to be kept in step — and
 * the reason a comment you wrote survives a checkbox.
 *
 * The form shows the settings that are one value each, and `parameters:` as a
 * row per parameter the notebook declares. `notify:` and `dependsOn:` are named
 * on the card and left to the YAML tab, which is honest about where they live
 * rather than offering a half-editor for them.
 */
export function JobsOverview({
  text, onChange, readOnly, path, notebooks, project, branch,
}: {
  text: string;
  onChange: (next: string) => void;
  readOnly: boolean;
  /** This jobs file's path, so the paired notebook can be found beside it. */
  path: string;
  /** Notebooks on this branch, to find the paired one. */
  notebooks: string[];
  project: string;
  /** Which branch this file is open on — jobs only run on test and prod. */
  branch: string;
}) {
  const view = readJobsFile(text);
  // Jobs run where they are scheduled. On your own branch there is no job to run
  // yet, only a file describing one, so the card offers no button to press.
  const runnable = branch === 'test' || branch === 'prod' || branch === 'default';

  // The parameters the paired notebook declares, read once per file. Null until
  // the notebook has answered, so "none" is not shown while it is still loading.
  const notebook = pairedNotebook(path, notebooks);
  const [declared, setDeclared] = useState<NotebookParameter[] | null>(null);
  useEffect(() => {
    let live = true;
    setDeclared(null);
    if (notebook == null) {
      setDeclared([]);
      return;
    }
    api.notebookCells(branch, notebook)
      .then((reply) => live && setDeclared(notebookParameters(reply.cells)))
      .catch(() => live && setDeclared([]));
    return () => {
      live = false;
    };
  }, [branch, notebook]);

  if (view.error) {
    return (
      <div className="p-4">
        <Alert variant="warning" className="max-w-[640px]">
          <AlertDescription>
            <p>This file cannot be read as jobs yet: {view.error}</p>
            <p className="mt-2">
              Fix it on the <strong>YAML</strong> tab. A form over a file it could not parse
              would invite you to repair it by typing into boxes, and saving that would
              write a new file over the one that needs fixing.
            </p>
          </AlertDescription>
        </Alert>
      </div>
    );
  }

  const set = (index: number, key: keyof JobView, value: string | boolean) =>
    onChange(setJobField(text, index, key, value));

  return (
    <div className="flex flex-col gap-4 p-4">
      {view.hasDefaults && (
        <Alert className="max-w-[720px]">
          <AlertDescription>
            This file has a <code className="font-mono">defaults:</code> block. Every job
            inherits it, so a box left empty here may still have a value — the{' '}
            <strong>YAML</strong> tab shows what.
          </AlertDescription>
        </Alert>
      )}

      {readOnly && runnable && (
        <p className="max-w-[720px] text-sm text-muted-subtle">
          {branch} is read-only — edit on your own branch (<strong>Copy to my branch</strong>, top
          right) and push. Running and the schedule switch work here.
        </p>
      )}

      {runnable && view.jobs.length > 0 && (
        <FileSwitch
          branch={branch}
          path={path}
          anyEnabled={view.jobs.some((job) => job.enabled)}
        />
      )}

      {view.jobs.length === 0 && (
        <p className="text-base text-muted-foreground">
          No jobs in this file yet.
        </p>
      )}

      {view.jobs.map((job, index) => (
        <div
          key={index}
          className="flex max-w-[720px] flex-col gap-3 rounded-2xl border border-border bg-card p-4"
        >
          <div className="flex items-end gap-2">
            <Field label="Name" className="flex-1">
              <Input
                value={job.name}
                disabled={readOnly}
                placeholder="daily"
                onChange={(e) => set(index, 'name', e.target.value)}
              />
            </Field>
            {!readOnly && (
              <Button
                variant="outline"
                size="sm"
                aria-label={`Remove ${job.name || 'this job'}`}
                onClick={() => {
                  // A job is a scheduled thing somebody relies on, and this is
                  // the one control here that cannot be undone by retyping.
                  if (confirm(`Remove the job '${job.name || '(unnamed)'}' from this file?`)) {
                    onChange(removeJob(text, index));
                  }
                }}
              >
                <Trash2 className="size-3.5" aria-hidden="true" />
              </Button>
            )}
          </div>

          <CronField
            value={job.cron}
            disabled={readOnly}
            onChange={(cron) => set(index, 'cron', cron)}
          />

          <Parameters
            declared={declared}
            values={job.parameters}
            readOnly={readOnly}
            onChange={(name, value) => onChange(setJobParameter(text, index, name, value))}
          />

          <FieldRow>
            <Field label="Timeout (seconds)" className="w-40">
              <Input
                value={job.timeoutSeconds}
                disabled={readOnly}
                onChange={(e) => set(index, 'timeoutSeconds', e.target.value)}
              />
            </Field>
            <Field label="Retries" className="w-28">
              <Input
                value={job.retryCount}
                disabled={readOnly}
                onChange={(e) => set(index, 'retryCount', e.target.value)}
              />
            </Field>
            <CheckboxField
              label="Enabled"
              className="pb-1.5"
              checked={job.enabled}
              disabled={readOnly}
              onChange={(enabled) => set(index, 'enabled', enabled)}
            />
          </FieldRow>

          {job.extras.length > 0 && (
            <p className="mt-2 text-base text-muted-foreground">
              Also sets <code className="font-mono text-code">{job.extras.join(', ')}</code>
              {' '}— edit on the <strong>YAML</strong> tab. That is where{' '}
              <code className="font-mono text-code">dependsOn</code> and{' '}
              <code className="font-mono text-code">notify</code> live.
            </p>
          )}

          {runnable && job.name !== '' && (
            <JobActions project={project} branch={branch} name={job.name} />
          )}
        </div>
      ))}

      {!readOnly && (
        <div>
          <Button
            variant="outline"
            size="sm"
            onClick={() => {
              const name = prompt('Name for the new job — unique within the environment.');
              if (name?.trim()) {
                onChange(addJob(text, name.trim()));
              }
            }}
          >
            <Plus className="size-3.5" aria-hidden="true" />
            Add a job
          </Button>
        </div>
      )}
    </div>
  );
}

/**
 * Run it now, stop it, or go and look at what it has done.
 *
 * On the card rather than on a page of its own: the job *is* this entry in this
 * file, and a second page that showed the same fields again was two places to
 * edit one thing. Its history is the monitoring grid filtered to it — one grid
 * over every run beats a private table per job.
 */
function JobActions({ project, branch, name }: { project: string; branch: string; name: string }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  // The job's own last run, polled. "Started." was a note this component wrote
  // to itself and then never revised — it still said Started long after the run
  // had finished, which is worse than saying nothing. This is the run, so it
  // says Running and then Succeeded, and it says it whether or not you were the
  // one who pressed the button.
  const { data: runs, reload } = usePolling(() => api.jobRuns(branch, name, 1), 3000, [branch, name]);
  const last = runs?.[0] ?? null;

  async function press(what: 'run' | 'cancel') {
    setNote(null);
    setBusy(true);
    try {
      if (what === 'run') {
        await api.runJob(branch, name);
      } else {
        await api.cancelJob(branch, name);
      }
      // Ahead of the next tick, so the badge moves under the finger that pressed.
      reload();
    } catch (e) {
      setNote((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  const running = last != null && isActive(last.status);

  return (
    <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-border pt-3">
      <Button variant="outline" size="sm" disabled={busy || running} onClick={() => press('run')}>
        <Play className="size-3.5" aria-hidden="true" />
        Run now
      </Button>
      {/* Only while there is something to stop. It used to sit there always,
          offering to cancel a job that had not run since Tuesday. */}
      {running && (
        <Button variant="ghost" size="sm" disabled={busy} onClick={() => press('cancel')}>
          <Square className="size-3.5" aria-hidden="true" />
          Cancel run
        </Button>
      )}
      <Button variant="ghost" size="sm" asChild>
        <Link
          className="hover:no-underline"
          to={`/monitoring?project=${encodeURIComponent(project)}`
            + `&env=${encodeURIComponent(branch)}&job=${encodeURIComponent(name)}`}
        >
          <History className="size-3.5" aria-hidden="true" />
          View runs
        </Link>
      </Button>
      {last != null && (
        <span className="flex items-center gap-1.5">
          <StatusBadge status={last.status} />
          <span className="text-xs text-muted-subtle">
            {running ? 'now' : timeAgo(last.finishedAt ?? last.startedAt ?? last.createdAt)}
          </span>
        </span>
      )}
      {note && <span className="text-base text-status-danger">{note}</span>}
    </div>
  );
}

/** Snooze lengths, in hours. */
const PAUSES: [string, number][] = [
  ['2 hours', 2], ['8 hours', 8], ['1 day', 24], ['4 days', 96], ['1 week', 168],
];

/**
 * The switch on the whole file — every job in it — kept apart from `enabled:`
 * on each job on purpose.
 *
 * `enabled:` says whether a job is defined to run; a file whose jobs are all
 * disabled cannot be activated, because that takes an edit, a push and a
 * promotion. Active is the operator's switch; off is paused indefinitely. A
 * pause is a snooze that ends by itself. None of them stops "Run now".
 *
 * Read from the server, not the text beside it, and polled: somebody else can
 * pause the file while you are looking at it.
 */
function FileSwitch({ branch, path, anyEnabled }: { branch: string; path: string; anyEnabled: boolean }) {
  const [busy, setBusy] = useState(false);
  const [note, setNote] = useState<string | null>(null);
  const { data: state, reload } = usePolling(() => api.jobsFileState(branch, path), 10000, [branch, path]);

  async function switchTo(next: { active: boolean; pausedUntil: string | null }) {
    setNote(null);
    setBusy(true);
    try {
      await api.setJobsFileState(branch, path, next);
      reload();
    } catch (e) {
      setNote((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  if (state == null) {
    return null;
  }
  return (
    <div className="flex max-w-[720px] flex-wrap items-center gap-2 rounded-2xl border border-border bg-card px-4 py-3">
      <span className="mr-1 text-sm font-semibold">Schedule</span>
      <Switch state={state} anyEnabled={anyEnabled} busy={busy} onSwitch={switchTo} />
      {note && <span className="text-base text-status-danger">{note}</span>}
    </div>
  );
}

function Switch({
  state, anyEnabled, busy, onSwitch,
}: {
  state: JobsFileState;
  anyEnabled: boolean;
  busy: boolean;
  onSwitch: (state: { active: boolean; pausedUntil: string | null }) => void;
}) {
  if (!anyEnabled) {
    return (
      <span className="text-xs text-muted-subtle" title="Enable a job on its card, push, and the file can be activated.">
        Every job here is disabled in the file — nothing is scheduled
      </span>
    );
  }
  if (!state.active) {
    return (
      <>
        <span className="text-xs text-status-warning">Inactive — paused indefinitely</span>
        <Button variant="outline" size="sm" disabled={busy}
          onClick={() => onSwitch({ active: true, pausedUntil: null })}>
          Activate
        </Button>
      </>
    );
  }
  return (
    <>
      {state.pausedUntil != null ? (
        <>
          <span className="text-xs text-status-warning">
            Paused until {new Date(state.pausedUntil).toLocaleString()}
          </span>
          <Button variant="outline" size="sm" disabled={busy}
            onClick={() => onSwitch({ active: true, pausedUntil: null })}>
            Resume
          </Button>
        </>
      ) : (
        <>
        <span className="text-xs text-muted-subtle">Active — runs as scheduled</span>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="outline" size="sm" disabled={busy}>
              <Pause className="size-3.5" aria-hidden="true" />
              Pause
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="start" className="w-auto whitespace-nowrap">
            {PAUSES.map(([label, hours]) => (
              <DropdownMenuItem key={label} onSelect={() =>
                onSwitch({ active: true, pausedUntil: new Date(Date.now() + hours * 3600_000).toISOString() })}>
                {label}
              </DropdownMenuItem>
            ))}
            <DropdownMenuItem onSelect={() => {
              // ponytail: a typed time. A date picker if people keep mistyping it.
              const typed = prompt('Pause until (local time, e.g. 2026-09-25 08:00)');
              const at = typed ? new Date(typed) : null;
              if (at != null && !Number.isNaN(at.getTime())) {
                onSwitch({ active: true, pausedUntil: at.toISOString() });
              } else if (typed) {
                alert(`Could not read "${typed}" as a time.`);
              }
            }}>
              Until…
            </DropdownMenuItem>
            <DropdownMenuItem onSelect={() => onSwitch({ active: false, pausedUntil: null })}>
              Indefinitely
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
        </>
      )}
      <Button variant="ghost" size="sm" disabled={busy}
        onClick={() => onSwitch({ active: false, pausedUntil: null })}>
        Deactivate
      </Button>
    </>
  );
}

/**
 * One row per parameter the notebook declares, plus any the job sets that the
 * notebook does not — those are shown too, flagged, rather than silently kept.
 */
function Parameters({
  declared, values, readOnly, onChange,
}: {
  declared: NotebookParameter[] | null;
  values: Record<string, string>;
  readOnly: boolean;
  onChange: (name: string, value: string) => void;
}) {
  if (declared == null) {
    return null;
  }
  const names = declared.map((p) => p.name);
  const orphans = Object.keys(values).filter((name) => !names.includes(name));
  if (names.length === 0 && orphans.length === 0) {
    return (
      <p className="text-sm text-muted-subtle">
        No parameters — the notebook has no <code className="font-mono">// parameters</code> cell.
      </p>
    );
  }
  return (
    <Field label="Parameters" hint="Empty means the notebook's own default.">
      <div className="flex flex-col gap-1.5">
        {declared.map((p) => (
          <div key={p.name} className="flex items-center gap-2">
            <code className="w-40 shrink-0 truncate font-mono text-code" title={p.name}>{p.name}</code>
            <Input
              value={values[p.name] ?? ''}
              disabled={readOnly}
              placeholder={p.defaultValue}
              aria-label={`Parameter ${p.name}`}
              onChange={(e) => onChange(p.name, e.target.value)}
            />
          </div>
        ))}
        {orphans.map((name) => (
          <div key={name} className="flex items-center gap-2">
            <code className="w-40 shrink-0 truncate font-mono text-code text-status-warning"
              title="The notebook does not declare this parameter">{name}</code>
            <Input
              value={values[name]}
              disabled={readOnly}
              aria-label={`Parameter ${name}`}
              onChange={(e) => onChange(name, e.target.value)}
            />
          </div>
        ))}
      </div>
    </Field>
  );
}

/** The notebook this jobs file schedules: the one beside it with the same stem. */
function pairedNotebook(path: string, notebooks: string[]): string | null {
  const stem = path.replace(/\.jobs\.yaml$/i, '').toLowerCase();
  return notebooks.find((n) => n.replace(/\.(nb\.md|ipynb|dib|csx)$/i, '').toLowerCase() === stem) ?? null;
}
