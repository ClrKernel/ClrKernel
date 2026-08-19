import { useEffect, useState } from 'react';
import { Link, useNavigate, useParams, useSearchParams } from 'react-router-dom';
import { api, type Job, type Run } from '../api';
import { ErrorBanner, usePolling } from '../components/common';
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
        <span className="muted">
          No channels yet — add one under <Link to="/channels">Channels</Link>.
        </span>
      ) : (
        names.map((name) => (
          <label key={name} className="checkbox">
            <input type="checkbox" checked={selected.includes(name)} onChange={() => toggle(name)} />
            {name}
            {!channels.includes(name) && <span className="chip chip-muted">unknown</span>}
          </label>
        ))
      )}
    </div>
  );
}

export function JobDetail() {
  const { env = 'default', name } = useParams<{ env: string; name: string }>();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const isNew = name == null;

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
  const channelNames = (channels?.channels ?? []).map((c) => c.name);

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
      navigate(`/jobs/${env}/${encodeURIComponent(saved.name)}`);
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
      <div className="row-between">
        <h1>{isNew ? 'New job' : name}{env !== 'default' && <span className={`chip env-${env}`}>{env}</span>}</h1>
        {!isNew && (
          <div className="row-gap">
            <button className="button" onClick={() => runNow(false)} disabled={busy}>
              Run now
            </button>
            <button className="button" onClick={() => runNow(true)} disabled={busy}>
              Run with parameters…
            </button>
            <button className="button button-danger" onClick={remove} disabled={busy}>
              Delete
            </button>
          </div>
        )}
      </div>
      <ErrorBanner error={error} />
      <ErrorBanner error={saveError} />
      {job && (
        <p className="muted">
          Defined in <code>{job.jobsFile}</code>
        </p>
      )}

      <div className="form">
        <label>
          Name
          <input value={form.name} onChange={(e) => update('name', e.target.value)} />
        </label>
        <label>
          Notebook <span className="muted">(relative to the notebooks root)</span>
          <input value={form.notebook} onChange={(e) => update('notebook', e.target.value)} />
        </label>
        <label>
          Cron <span className="muted">(empty = manual or dependency-triggered)</span>
          <input
            value={form.cron}
            placeholder="0 2 * * *"
            onChange={(e) => update('cron', e.target.value)}
          />
        </label>
        <label>
          Depends on <span className="muted">(comma-separated job names)</span>
          <input value={form.dependsOn} onChange={(e) => update('dependsOn', e.target.value)} />
        </label>
        <div className="form-row">
          <label>
            Timeout (seconds)
            <input
              value={form.timeoutSeconds}
              onChange={(e) => update('timeoutSeconds', e.target.value)}
            />
          </label>
          <label>
            Retries
            <input value={form.retryCount} onChange={(e) => update('retryCount', e.target.value)} />
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
          Parameters <span className="muted">(JSON, injected into the notebook)</span>
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
        <div className="row-gap">
          <button className="button button-primary" onClick={save} disabled={busy}>
            {isNew ? 'Create job' : 'Save changes'}
          </button>
          <Link className="button" to="/jobs">
            Cancel
          </Link>
        </div>
      </div>

      {!isNew && (
        <>
          <h2>Run history</h2>
          <RunTable runs={runs ?? []} showJob={false} />
        </>
      )}
    </div>
  );
}
