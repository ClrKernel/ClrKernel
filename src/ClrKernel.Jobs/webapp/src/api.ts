// The typed client for the jobs API. The key, when the server requires one, is
// kept in localStorage — the SPA is served by the same tool, so same-origin.

export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut';
export type CellStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped';
export type RunTrigger = 'Manual' | 'Schedule' | 'Dependency' | 'Retry';

export interface Job {
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

export interface TreeNode {
  name: string;
  path: string;
  isDirectory: boolean;
  kind: 'notebook' | 'jobs' | null;
  jobs: string[] | null;
  children: TreeNode[] | null;
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
  health: () => request<{ status: string; jobs: number; notebooksRoot: string; errors: string[] }>('/health'),
  stats: (days = 7) => request<Stats>(`/stats?days=${days}`),

  jobs: () => request<{ jobs: Job[]; errors: string[] }>('/jobs'),
  job: (name: string) => request<Job>(`/jobs/${encodeURIComponent(name)}`),
  createJob: (job: Partial<Job>) => request<Job>('/jobs', { method: 'POST', body: JSON.stringify(job) }),
  updateJob: (name: string, job: Partial<Job>) =>
    request<Job>(`/jobs/${encodeURIComponent(name)}`, { method: 'PUT', body: JSON.stringify(job) }),
  deleteJob: (name: string) => request<void>(`/jobs/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  runJob: (name: string) =>
    request<{ runId: string }>(`/jobs/${encodeURIComponent(name)}/run`, { method: 'POST' }),
  cancelJob: (name: string) =>
    request<{ cancelled: boolean }>(`/jobs/${encodeURIComponent(name)}/cancel`, { method: 'POST' }),
  jobRuns: (name: string, limit = 25) =>
    request<Run[]>(`/jobs/${encodeURIComponent(name)}/runs?limit=${limit}`),

  runs: (limit = 25) => request<Run[]>(`/runs?limit=${limit}`),
  run: (id: string) => request<{ run: Run; cells: RunCell[] }>(`/runs/${id}`),
  artifact: (id: string) => request<unknown>(`/runs/${id}/artifact`),
  log: (id: string) =>
    fetch(`/api/runs/${id}/log`, { headers: apiKey() ? { 'X-Api-Key': apiKey() } : {} }).then((r) =>
      r.ok ? r.text() : '',
    ),

  notebooks: () => request<TreeNode>('/notebooks'),
};

export function isActive(status: RunStatus): boolean {
  return status === 'Pending' || status === 'Running';
}
