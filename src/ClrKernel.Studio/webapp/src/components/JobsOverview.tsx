import { useState } from 'react';
import { Link } from 'react-router-dom';
import { Play, Plus, Square, Trash2 } from 'lucide-react';
import { Alert, AlertDescription } from '@/components/ui/alert';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { api } from '../api';
import { addJob, readJobsFile, removeJob, setJobField, type JobView } from '../jobsFile';
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
 * The form shows the settings that are one value each. `parameters:` is a
 * free-form map and `notify:` is a pair of lists; both are named on the card and
 * left to the YAML tab, which is honest about where they live rather than
 * offering a half-editor for them.
 */
export function JobsOverview({
  text, onChange, readOnly, notebooks, project, branch,
}: {
  text: string;
  onChange: (next: string) => void;
  readOnly: boolean;
  /** Notebooks on this branch, for the "not here yet" warning. */
  notebooks: string[];
  project: string;
  /** Which branch this file is open on — jobs only run on test and prod. */
  branch: string;
}) {
  const view = readJobsFile(text);
  // Jobs run where they are scheduled. On your own branch there is no job to run
  // yet, only a file describing one, so the card offers no button to press.
  const runnable = branch === 'test' || branch === 'prod' || branch === 'default';

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

      {view.jobs.length === 0 && (
        <p className="text-base text-muted-foreground">
          No jobs in this file yet.
        </p>
      )}

      {view.jobs.map((job, index) => (
        <div key={index} className="max-w-[720px] rounded-2xl border border-border bg-card p-4">
          <div className="mb-3 flex items-center gap-2">
            <label className="flex-1">
              Name
              <Input
                value={job.name}
                disabled={readOnly}
                placeholder="daily"
                onChange={(e) => set(index, 'name', e.target.value)}
              />
            </label>
            {!readOnly && (
              <Button
                variant="outline"
                size="sm"
                className="mt-5"
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

          <label>
            Notebook
            <Input
              value={job.notebook}
              disabled={readOnly}
              placeholder={view.notebook || './daily.nb.md'}
              onChange={(e) => set(index, 'notebook', e.target.value)}
            />
            {job.notebook === '' && view.notebook !== '' && (
              <span className="block text-base text-muted-foreground">
                Empty, so it runs the file's <code className="font-mono">{view.notebook}</code>.
              </span>
            )}
            {job.notebook !== '' && notebooks.length > 0
              && !notebooks.includes(resolve(job.notebook)) && (
              <span className="block text-base text-status-warning">
                Not on this branch yet — a job can only name a notebook that is here.
              </span>
            )}
          </label>

          <CronField
            value={job.cron}
            disabled={readOnly}
            onChange={(cron) => set(index, 'cron', cron)}
          />

          <label>
            Depends on{' '}
            <span className="text-base text-muted-foreground">
              (job names, comma-separated — this one runs after they succeed)
            </span>
            <Input
              value={job.dependsOn}
              disabled={readOnly}
              onChange={(e) => set(index, 'dependsOn', e.target.value)}
            />
          </label>

          <div className="form-row">
            <label>
              Timeout (seconds)
              <Input
                value={job.timeoutSeconds}
                disabled={readOnly}
                onChange={(e) => set(index, 'timeoutSeconds', e.target.value)}
              />
            </label>
            <label>
              Retries
              <Input
                value={job.retryCount}
                disabled={readOnly}
                onChange={(e) => set(index, 'retryCount', e.target.value)}
              />
            </label>
            <label className="checkbox">
              <input
                type="checkbox"
                checked={job.enabled}
                disabled={readOnly}
                onChange={(e) => set(index, 'enabled', e.target.checked)}
              />
              Enabled
            </label>
          </div>

          {job.extras.length > 0 && (
            <p className="mt-2 text-base text-muted-foreground">
              Also sets <code className="font-mono text-code">{job.extras.join(', ')}</code>
              {' '}— edit on the <strong>YAML</strong> tab. That is where{' '}
              <code className="font-mono text-code">parameters</code> and{' '}
              <code className="font-mono text-code">notify</code> live too.
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

  async function press(what: 'run' | 'cancel') {
    setNote(null);
    setBusy(true);
    try {
      if (what === 'run') {
        await api.runJob(branch, name);
        setNote('Started.');
      } else {
        await api.cancelJob(branch, name);
        setNote('Cancelling.');
      }
    } catch (e) {
      setNote((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="mt-3 flex flex-wrap items-center gap-2 border-t border-border pt-3">
      <Button variant="outline" size="sm" disabled={busy} onClick={() => press('run')}>
        <Play className="size-3.5" aria-hidden="true" />
        Run now
      </Button>
      <Button variant="ghost" size="sm" disabled={busy} onClick={() => press('cancel')}>
        <Square className="size-3.5" aria-hidden="true" />
        Cancel run
      </Button>
      <Link
        className="text-base text-primary hover:underline"
        to={`/monitoring?project=${encodeURIComponent(project)}`
          + `&env=${encodeURIComponent(branch)}&job=${encodeURIComponent(name)}`}
      >
        Its runs
      </Link>
      {note && <span className="text-base text-muted-foreground">{note}</span>}
    </div>
  );
}

/** `./daily.nb.md` and `daily.nb.md` name the same file to the tree. */
function resolve(notebook: string): string {
  return notebook.replace(/^\.\//, '');
}
