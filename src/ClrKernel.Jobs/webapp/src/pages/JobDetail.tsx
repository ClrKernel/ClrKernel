import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs';
import { api, type Job, type Run } from '../api';
import { CronField } from '../components/CronField';
import { EnvBadge, ErrorBanner, usePolling } from '../components/common';
import { notebookPaths } from '../notebook';
import { useCanWrite } from '../sessionContext';
import { RunTable } from './Dashboard';

interface FormState {
  name: string;
  notebook: string;
  cron: string;
  enabled: boolean;
  timeoutSeconds: string;
  retryCount: string;
  parameters: string;
  dependsOn: string;
  onFailure: string[];
  onSuccess: string[];
}

function toForm(job: Job): FormState {
  return {
    name: job.name,
    notebook: job.notebook,
    cron: job.cron ?? '',
    enabled: job.enabled,
    timeoutSeconds: job.timeoutSeconds?.toString() ?? '',
    retryCount: job.retryCount.toString(),
    parameters: JSON.stringify(job.parameters ?? {}, null, 2),
    dependsOn: job.dependsOn.join(', '),
    onFailure: job.notify?.onFailure ?? [],
    onSuccess: job.notify?.onSuccess ?? [],
  };
}

const EMPTY: FormState = {
  name: '',
  notebook: '',
  cron: '',
  enabled: true,
  timeoutSeconds: '',
  retryCount: '0',
  parameters: '{}',
  dependsOn: '',
  onFailure: [],
  onSuccess: [],
};

/** Checkboxes over the configured channels, plus any name the job already uses. */
function NotifyPicker({
  label,
  channels,
  selected,
  onChange,
}: {
  label: string;
  channels: string[];
  selected: string[];
  onChange: (next: string[]) => void;
}) {
  // A job may reference a channel that no longer exists; show it so it is not
  // silently dropped when the form is saved.
  const names = Array.from(new Set([...channels, ...selected]));
  const toggle = (name: string) =>
    onChange(selected.includes(name) ? selected.filter((n) => n !== name) : [...selected, name]);

  return (
    <div className="notify-row">
      <span className="notify-label">{label}</span>
      {names.length === 0 ? (
        <span className="text-base text-muted-foreground">
          No channels yet — add one under{' '}
          <Link className="text-primary hover:underline" to="/channels">
            Channels
          </Link>
          .
        </span>
      ) : (
        names.map((name) => (
          <label key={name} className="checkbox">
            <input type="checkbox" checked={selected.includes(name)} onChange={() => toggle(name)} />
            {name}
            {!channels.includes(name) && <Badge variant="outline" className="font-normal">unknown</Badge>}
          </label>
        ))
      )}
    </div>
  );
}

export function JobDetail() {
  const canWrite = useCanWrite();
  const { project = 'default', env = 'default', name } =
    useParams<{ project: string; env: string; name: string }>();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const isNew = name == null;

  // A jobs file is content like any other: you edit your own copy of it. What is
  // in test and prod is what runs, and it is read-only there for everybody.
  const editable = env === 'mine' || env === 'default';
  const mayEdit = canWrite && editable;
  const mine = `/jobs/${project}/mine/${name ? encodeURIComponent(name) : 'new'}`;

  const [form, setForm] = useState<FormState>({ ...EMPTY, notebook: search.get('notebook') ?? '' });
  const [saveError, setSaveError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const { data: job, error, reload } = usePolling<Job | null>(
    () => (isNew ? Promise.resolve(null) : api.job(env, name!)),
    null,
    [name],
  );
  const { data: runs } = usePolling<Run[]>(
    () => (isNew ? Promise.resolve([]) : api.jobRuns(env, name!, 25)),
    isNew ? null : 3000,
    [name],
  );
  const { data: channels } = usePolling(() => api.channels(), null);
  // The notebooks this job could name. A job's notebook has to exist on the branch
  // the job is written to — the server refuses one that does not — so the list is
  // the field, rather than a text box that meets that refusal at save time.
  const { data: trees } = usePolling(() => api.notebooks(), null);
  const notebooks = notebookPaths((trees?.environments ?? []).find((e) => e.name === env)?.tree);
  // Whatever it already says, even when that is not on this branch: a job whose
  // notebook moved must still render as the job it is.
  const notebookOptions =
    form.notebook && !notebooks.includes(form.notebook) ? [form.notebook, ...notebooks] : notebooks;
  const channelNames = (channels?.channels ?? []).map((c) => c.name);
  // The jobs file itself, not a client-side re-serialisation of the form: what
  // is on disk is the thing worth showing, and it needs no new endpoint.
  const { data: yaml } = usePolling<string>(
    () => (job ? api.notebookContent(env, job.jobsFile) : Promise.resolve('')),
    null,
    [job?.jobsFile],
  );

  useEffect(() => {
    if (job) {
      setForm(toForm(job));
    }
  }, [job]);

  const update = <K extends keyof FormState>(key: K, value: FormState[K]) =>
    setForm((f) => ({ ...f, [key]: value }));

  async function save() {
    setSaveError(null);
    let parameters: Record<string, unknown>;
    try {
      parameters = form.parameters.trim() ? JSON.parse(form.parameters) : {};
    } catch (e) {
      setSaveError(`Parameters must be valid JSON: ${(e as Error).message}`);
      return;
    }

    const body = {
      name: form.name.trim(),
      notebook: form.notebook.trim(),
      cron: form.cron.trim() || null,
      enabled: form.enabled,
      timeoutSeconds: form.timeoutSeconds.trim() ? Number(form.timeoutSeconds) : null,
      retryCount: Number(form.retryCount) || 0,
      parameters,
      dependsOn: form.dependsOn
        .split(',')
        .map((d) => d.trim())
        .filter(Boolean),
      notify: { onFailure: form.onFailure, onSuccess: form.onSuccess },
    };

    setBusy(true);
    try {
      const saved = isNew ? await api.createJob(env, body) : await api.updateJob(env, name!, body);
      navigate(`/jobs/${project}/${env}/${encodeURIComponent(saved.name)}`);
      reload();
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function runNow(withOverrides: boolean) {
    setSaveError(null);
    let overrides: Record<string, unknown> | undefined;
    if (withOverrides) {
      const entered = prompt(
        'Parameters for this run only (JSON). The job is not modified.',
        form.parameters,
      );
      if (entered == null) {
        return;
      }
      try {
        overrides = JSON.parse(entered);
      } catch (e) {
        setSaveError(`Parameters must be valid JSON: ${(e as Error).message}`);
        return;
      }
    }

    setBusy(true);
    try {
      const { runId } = await api.runJob(env, name!, overrides);
      navigate(`/runs/${runId}`);
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function remove() {
    if (!confirm(`Delete job '${name}'? Its run history is kept.`)) {
      return;
    }
    setBusy(true);
    try {
      await api.deleteJob(env, name!);
      navigate('/jobs');
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <div>
      <div className="mb-4 flex items-start justify-between gap-4">
        <h1 className="flex min-w-0 items-center gap-2 text-xl font-bold tracking-tight">
          <span className="truncate">{isNew ? 'New job' : name}</span>
          {env !== 'default' && <EnvBadge env={env} />}
        </h1>
        {!isNew && canWrite && (
          <div className="flex shrink-0 items-center gap-2">
            {/* A job runs where it is scheduled, and is edited where you work. */}
            {editable ? (
              <Button variant="outline" size="sm" onClick={() => navigate(`/jobs/${project}/test/${encodeURIComponent(name!)}`)}>
                See it in test
              </Button>
            ) : (
              <>
                <Button variant="outline" size="sm" onClick={() => navigate(mine)}>
                  Edit on my branch
                </Button>
                <Button variant="outline" size="sm" onClick={() => runNow(false)} disabled={busy}>
                  Run now
                </Button>
                <Button variant="outline" size="sm" onClick={() => runNow(true)} disabled={busy}>
                  Run with parameters…
                </Button>
              </>
            )}
            {editable && (
              <Button
                variant="outline"
                size="sm"
                className="text-destructive hover:bg-destructive/10 hover:text-destructive"
                onClick={remove}
                disabled={busy}
              >
                Delete
              </Button>
            )}
          </div>
        )}
      </div>
      <ErrorBanner error={error} />
      <ErrorBanner error={saveError} />
      {isNew && env === 'mine' && (
        <Alert className="mb-4 max-w-[640px]">
          <AlertTitle>This job is written on your own branch</AlertTitle>
          <AlertDescription>
            Jobs are edited where everything else is — your branch — whichever branch you were
            browsing when you started. Nothing is scheduled from a personal branch: it begins
            running when you <strong>Push to test</strong> from the notebook editor, and reaches
            production by being promoted.
          </AlertDescription>
        </Alert>
      )}

      <Tabs defaultValue="overview">
        <TabsList variant="line" className="mb-4">
          <TabsTrigger value="overview">Overview</TabsTrigger>
          {!isNew && <TabsTrigger value="runs">Runs</TabsTrigger>}
          {!isNew && <TabsTrigger value="yaml">YAML</TabsTrigger>}
        </TabsList>

        <TabsContent value="overview">
      <fieldset className="form" disabled={!mayEdit}>
        <label>
          Name
          <Input value={form.name} onChange={(e) => update('name', e.target.value)} />
        </label>
        <label>
          Notebook{' '}
          <span className="text-base text-muted-foreground">(on {env})</span>
          <Select
            value={form.notebook || undefined}
            onValueChange={(notebook) => update('notebook', notebook)}
          >
            <SelectTrigger className="font-mono" aria-label="Notebook">
              <SelectValue placeholder="Pick a notebook" />
            </SelectTrigger>
            <SelectContent>
              {notebookOptions.map((notebook) => (
                <SelectItem key={notebook} value={notebook} className="font-mono">
                  {notebook}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          {notebookOptions.length === 0 && (
            <span className="block text-base text-muted-foreground">
              No notebooks on this branch yet — make one from{' '}
              <Link className="text-primary hover:underline" to="/notebooks">
                Notebooks
              </Link>
              .
            </span>
          )}
          {/* Said here rather than met at save time. Clicking "+ job" on a notebook
              you were reading in test names a file your branch may not have yet —
              test moves on without you — and the save refuses it with a message
              that arrives after the whole form is filled in. */}
          {trees != null && form.notebook && !notebooks.includes(form.notebook) && (
            <span className="block text-base text-status-warning">
              Not on this branch yet. Open it and{' '}
              <strong>Update from test</strong> first — a job can only name a notebook
              that is here.
            </span>
          )}
        </label>
        <CronField value={form.cron} disabled={!mayEdit} onChange={(cron) => update('cron', cron)} />
        <label>
          Depends on <span className="text-base text-muted-foreground">(comma-separated job names)</span>
          <Input value={form.dependsOn} onChange={(e) => update('dependsOn', e.target.value)} />
        </label>
        <div className="form-row">
          <label>
            Timeout (seconds)
            <Input
              value={form.timeoutSeconds}
              onChange={(e) => update('timeoutSeconds', e.target.value)}
            />
          </label>
          <label>
            Retries
            <Input value={form.retryCount} onChange={(e) => update('retryCount', e.target.value)} />
          </label>
          <label className="checkbox">
            <input
              type="checkbox"
              checked={form.enabled}
              onChange={(e) => update('enabled', e.target.checked)}
            />
            Enabled
          </label>
        </div>
        <label>
          Parameters <span className="text-base text-muted-foreground">(JSON, injected into the notebook)</span>
          <textarea
            rows={6}
            value={form.parameters}
            onChange={(e) => update('parameters', e.target.value)}
          />
        </label>

        <fieldset className="fieldset">
          <legend>Notify</legend>
          <NotifyPicker
            label="On failure"
            channels={channelNames}
            selected={form.onFailure}
            onChange={(next) => update('onFailure', next)}
          />
          <NotifyPicker
            label="On success"
            channels={channelNames}
            selected={form.onSuccess}
            onChange={(next) => update('onSuccess', next)}
          />
        </fieldset>
        <div className="flex items-center gap-2">
          <Button size="sm" onClick={save} disabled={busy}>
            {isNew ? 'Create job' : 'Save changes'}
          </Button>
          <Button asChild variant="outline" size="sm">
            <Link to="/jobs">Cancel</Link>
          </Button>
        </div>
      </fieldset>
        </TabsContent>

        <TabsContent value="runs">
          <RunTable runs={runs ?? []} showJob={false} />
        </TabsContent>

        <TabsContent value="yaml">
          {job && (
            <p className="mb-2 text-base text-muted-foreground">
              Defined in <code className="font-mono text-code">{job.jobsFile}</code>
            </p>
          )}
          <pre className="max-w-[820px] overflow-x-auto rounded-2xl border border-border bg-muted px-4 py-3.5 font-mono text-sm leading-relaxed text-code-fg">
            {yaml ?? ''}
          </pre>
        </TabsContent>
      </Tabs>
    </div>
  );
}
