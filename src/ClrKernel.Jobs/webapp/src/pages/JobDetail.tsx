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
};

export function JobDetail() {
  const { name } = useParams<{ name: string }>();
  const [search] = useSearchParams();
  const navigate = useNavigate();
  const isNew = name == null;

  const [form, setForm] = useState<FormState>({ ...EMPTY, notebook: search.get('notebook') ?? '' });
  const [saveError, setSaveError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const { data: job, error, reload } = usePolling<Job | null>(
    () => (isNew ? Promise.resolve(null) : api.job(name)),
    null,
    [name],
  );
  const { data: runs } = usePolling<Run[]>(
    () => (isNew ? Promise.resolve([]) : api.jobRuns(name, 25)),
    isNew ? null : 3000,
    [name],
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
    };

    setBusy(true);
    try {
      const saved = isNew ? await api.createJob(body) : await api.updateJob(name, body);
      navigate(`/jobs/${encodeURIComponent(saved.name)}`);
      reload();
    } catch (e) {
      setSaveError((e as Error).message);
    } finally {
      setBusy(false);
    }
  }

  async function runNow() {
    setSaveError(null);
    setBusy(true);
    try {
      const { runId } = await api.runJob(name!);
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
      await api.deleteJob(name!);
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
        <h1>{isNew ? 'New job' : name}</h1>
        {!isNew && (
          <div className="row-gap">
            <button className="button" onClick={runNow} disabled={busy}>
              Run now
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
