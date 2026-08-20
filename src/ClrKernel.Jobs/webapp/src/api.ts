// The typed client for the jobs API. The key, when the server requires one, is
// kept in localStorage — the SPA is served by the same tool, so same-origin.

import type { NotebookOutput } from './ipynb';

export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut';
export type CellStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped';
export type RunTrigger = 'Manual' | 'Schedule' | 'Dependency' | 'Retry';

export interface Job {
  /** dev | prod, or "default" when the git workflow is off. */
  environment: string;
  name: string;
  notebook: string;
  jobsFile: string;
  cron: string | null;
  enabled: boolean;
  timeoutSeconds: number | null;
  retryCount: number;
  parameters: Record<string, unknown>;
  dependsOn: string[];
  notify: { onFailure: string[]; onSuccess: string[] } | null;
}

export interface Run {
  id: string;
  environment: string;
  jobName: string;
  notebookPath: string;
  status: RunStatus;
  trigger: RunTrigger;
  causedByRunId: string | null;
  attempt: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  errorSummary: string | null;
  artifactPath: string | null;
  logPath: string | null;
}

export interface RunCell {
  runId: string;
  cellIndex: number;
  status: CellStatus;
  sourcePreview: string;
  startedAt: string | null;
  finishedAt: string | null;
  errorSummary: string | null;
}

/** One notebook cell as the server parses and writes it. */
export interface ApiCell {
  id?: string;
  kind: 'code' | 'markdown';
  /** The code-block tag as written ("sql", "zsh"); null for prose. Preserved on
   *  save — one language claims several tags and rewriting one changes meaning. */
  tag: string | null;
  languageId?: string | null;
  source: string;
  blankLinesAfter?: number;
  closed?: boolean;
}

/** A cell language the kernel declared, for the picker and syntax highlighting. */
export interface ApiLanguage {
  id: string;
  displayName: string;
  defaultSelector: string | null;
  selectors: string[];
  languageTags: string[];
  hasEditorServices?: boolean;
  hasConnections?: boolean;
}

/** What one cell did in an interactive session. Outputs are nbformat shapes —
 *  the same ones the run view renders. */
export interface ApiCellRun {
  status: 'pending' | 'running' | 'succeeded' | 'failed' | 'skipped' | string;
  executionCount: number | null;
  truncated: boolean;
  outputs: NotebookOutput[];
}

/** The warm kernel behind the editor. `started: false` means no kernel is
 *  running for this notebook yet — the first run starts one. */
export interface ApiSession {
  sessionId?: string;
  started: boolean;
  running: boolean;
  kernel?: string | null;
  version?: string | null;
  /** The kernel died on its own and was replaced: variables are gone. */
  kernelRestarted?: boolean;
  /** A scheduled run of this notebook is in flight, in its own kernel. */
  scheduledRunActive?: boolean;
  languages?: ApiLanguage[];
  cells?: Record<string, ApiCellRun>;
}

export interface TreeNode {
  name: string;
  path: string;
  isDirectory: boolean;
  kind: 'notebook' | 'jobs' | null;
  jobs: string[] | null;
  children: TreeNode[] | null;
}

export interface Channel {
  name: string;
  type: 'webhook' | 'email' | string;
  url?: string | null;
  host?: string | null;
  port?: number | null;
  from?: string | null;
  to?: string[] | null;
  user?: string | null;
  /** A reference resolved on the server — never the secret itself. */
  bearerSecretRef?: string | null;
  passwordSecretRef?: string | null;
}

export interface SettingField {
  name: string;
  label: string;
  type: 'string' | 'int' | 'bool' | 'secret' | string;
  value?: unknown;
  isSet?: boolean | null;
  source: string;
  webWritable: boolean;
  restartRequired: boolean;
  help?: string | null;
}

export interface SettingsSection {
  key: string;
  title: string;
  description?: string | null;
  linkTo?: string | null;
  fields: SettingField[];
}

export interface Stats {
  total: number;
  succeeded: number;
  failed: number;
  byStatus: Record<string, number>;
}

const KEY_STORAGE = 'clrkernel-jobs-api-key';

export function apiKey(): string {
  return localStorage.getItem(KEY_STORAGE) ?? '';
}

export function setApiKey(key: string): void {
  if (key) {
    localStorage.setItem(KEY_STORAGE, key);
  } else {
    localStorage.removeItem(KEY_STORAGE);
  }
}

export class ApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const key = apiKey();
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
      ...(key ? { 'X-Api-Key': key } : {}),
      ...init?.headers,
    },
  });

  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const body = await response.json();
      if (body?.error) {
        message = body.error;
      }
    } catch {
      // Non-JSON error body; the status line is all we have.
    }
    throw new ApiError(response.status, message);
  }

  if (response.status === 204) {
    return undefined as T;
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  health: () =>
    request<{
      status: string;
      jobs: number;
      notebooksRoot: string;
      environments: string[];
      gitEnabled: boolean;
      errors: string[];
    }>('/health'),
  stats: (days = 7) => request<Stats>(`/stats?days=${days}`),

  jobs: () => request<{ jobs: Job[]; errors: string[] }>('/jobs'),
  job: (env: string, name: string) => request<Job>(`/envs/${env}/jobs/${encodeURIComponent(name)}`),
  createJob: (env: string, job: Partial<Job>) =>
    request<Job>(`/envs/${env}/jobs`, { method: 'POST', body: JSON.stringify(job) }),
  updateJob: (env: string, name: string, job: Partial<Job>) =>
    request<Job>(`/envs/${env}/jobs/${encodeURIComponent(name)}`, { method: 'PUT', body: JSON.stringify(job) }),
  deleteJob: (env: string, name: string) =>
    request<void>(`/envs/${env}/jobs/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  /** Optional parameters apply to this run only; the job's yaml is untouched. */
  runJob: (env: string, name: string, parameters?: Record<string, unknown>) =>
    request<{ runId: string }>(`/envs/${env}/jobs/${encodeURIComponent(name)}/run`, {
      method: 'POST',
      ...(parameters && Object.keys(parameters).length > 0
        ? { body: JSON.stringify({ parameters }) }
        : {}),
    }),
  cancelJob: (env: string, name: string) =>
    request<{ cancelled: boolean }>(`/envs/${env}/jobs/${encodeURIComponent(name)}/cancel`, {
      method: 'POST',
    }),
  jobRuns: (env: string, name: string, limit = 25) =>
    request<Run[]>(`/envs/${env}/jobs/${encodeURIComponent(name)}/runs?limit=${limit}`),

  runs: (limit = 25) => request<Run[]>(`/runs?limit=${limit}`),
  run: (id: string) => request<{ run: Run; cells: RunCell[] }>(`/runs/${id}`),
  artifact: (id: string) => request<unknown>(`/runs/${id}/artifact`),
  log: (id: string) =>
    fetch(`/api/runs/${id}/log`, { headers: apiKey() ? { 'X-Api-Key': apiKey() } : {} }).then((r) =>
      r.ok ? r.text() : '',
    ),

  notebooks: () =>
    request<{ environments: { name: string; tree: TreeNode | null }[] }>('/notebooks'),

  notebookContent: (env: string, path: string) =>
    fetch(`/api/envs/${env}/notebooks/content?path=${encodeURIComponent(path)}`, {
      headers: apiKey() ? { 'X-Api-Key': apiKey() } : {},
    }).then((r) => (r.ok ? r.text() : Promise.reject(new Error(`${r.status}`)))),
  saveNotebookContent: (path: string, content: string) =>
    request<{ saved: boolean; commitSha: string }>(
      `/envs/dev/notebooks/content?path=${encodeURIComponent(path)}`,
      { method: 'PUT', body: content, headers: { 'Content-Type': 'text/plain' } },
    ),
  // The UI diffs by fetching both environments' content into Monaco. GET
  // /api/git/diff still exists and is still the right thing over curl.
  // The notebook as cells, with the languages this kernel can run — parsed and
  // written server-side so the browser never needs its own copy of the format.
  notebookCells: (env: string, path: string) =>
    request<{ cells: ApiCell[]; languages: ApiLanguage[] }>(
      `/envs/${env}/notebooks/cells?path=${encodeURIComponent(path)}`,
    ),
  saveNotebookCells: (path: string, cells: ApiCell[]) =>
    request<{ saved: boolean; commitSha: string }>(
      `/envs/dev/notebooks/cells?path=${encodeURIComponent(path)}`,
      { method: 'PUT', body: JSON.stringify({ cells }) },
    ),
  // Interactive execution against the notebook's warm kernel. None of this
  // writes to the run store: an interactive run never appears in run history
  // and can never become the green evidence promotion requires.
  runCells: (path: string, cells: ApiCell[]) =>
    request<{ running: string[] }>(`/envs/dev/notebooks/run?path=${encodeURIComponent(path)}`, {
      method: 'POST',
      body: JSON.stringify({ cells }),
    }),
  sessionStatus: (path: string) =>
    request<ApiSession>(`/envs/dev/notebooks/session/status?path=${encodeURIComponent(path)}`),
  /** Kills the kernel. Also the only interrupt there is — no RPC surface can
   *  cancel a cell that is already running. */
  restartSession: (path: string) =>
    request<{ restarted: boolean }>(`/envs/dev/notebooks/session?path=${encodeURIComponent(path)}`, {
      method: 'DELETE',
    }),

  promotionStatus: (path: string) =>
    request<{
      eligible: boolean;
      reasons: string[];
      paths: string[];
      isDeletion: boolean;
    }>(`/envs/dev/notebooks/promotion?path=${encodeURIComponent(path)}`),
  promote: (path: string) =>
    request<{ promoted: boolean; commitSha: string }>(
      `/envs/dev/notebooks/promote?path=${encodeURIComponent(path)}`,
      { method: 'POST' },
    ),

  settings: () => request<{ sections: SettingsSection[] }>('/settings'),
  saveSettings: (section: string, values: Record<string, unknown>) =>
    request<{ saved: boolean; restartRequired: boolean }>(`/settings/${encodeURIComponent(section)}`, {
      method: 'PUT',
      body: JSON.stringify(values),
    }),

  channels: () => request<{ channels: Channel[]; errors: string[] }>('/channels'),
  saveChannels: (channels: Channel[]) =>
    request<{ channels: number }>('/channels', { method: 'PUT', body: JSON.stringify({ channels }) }),
  testChannel: (name: string) =>
    request<{ sent: boolean }>(`/channels/${encodeURIComponent(name)}/test`, { method: 'POST' }),
};

export function isActive(status: RunStatus): boolean {
  return status === 'Pending' || status === 'Running';
}
