// The typed client for the jobs API. The key, when the server requires one, is
// kept in localStorage — the SPA is served by the same tool, so same-origin.

import type { NotebookOutput } from './ipynb';
import type { LspDiagnostic } from './monaco/lsp';

/**
 * The project every scoped route is built from.
 *
 * One module-level value rather than a parameter threaded through every page: a
 * server that has registered nothing runs exactly one project, and the pages that
 * would carry it around have nothing to choose between. The switcher sets it.
 */
let currentProject = 'default';

export function setProject(slug: string): void {
  currentProject = slug;
}

export function projectSlug(): string {
  return currentProject;
}

/** `/projects/<slug>` — notebooks trees and anything else that is per project. */
const project = () => `/projects/${encodeURIComponent(currentProject)}`;

/** `/projects/<slug>/branches/<branch>` — everything that reads or writes a worktree. */
const scope = (branch: string) => `${project()}/branches/${encodeURIComponent(branch)}`;

export type RunStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Cancelled' | 'TimedOut';
export type CellStatus = 'Pending' | 'Running' | 'Succeeded' | 'Failed' | 'Skipped';
export type RunTrigger = 'Manual' | 'Schedule' | 'Dependency' | 'Retry';

export type RemoteMode = 'Local' | 'ServerAuthoritative' | 'RemoteAuthoritative';
export type ProjectRole = 'ProjectViewer' | 'ProjectMember' | 'ProjectAdmin';

export interface Project {
  slug: string;
  name: string;
  root: string;
  gitEnabled: boolean;
  /** False when git is on but the folder is not a workspace yet. */
  ready: boolean;
  remoteMode: RemoteMode;
  remote: string | null;
  /** The *name* of a secret, never a credential. */
  remoteSecret: string | null;
  pushUserBranches: boolean;
  environments: string[];
  /** What you may do here. Projects you may do nothing in are not listed at all. */
  role: ProjectRole;
}

export interface BranchStanding {
  hasBranch: boolean;
  branch?: string;
  /** Saved but not yet pushed — a file write, not a commit. */
  dirty?: boolean;
  ahead?: number;
  behind?: number;
  conflicts?: string[];
}

export interface ProjectMember {
  userId: string;
  displayName: string;
  serverRole: string;
  role: ProjectRole;
  createdAt: string;
}

/** What registering or editing a project sends. No credential, ever — see above. */
export interface ProjectWrite {
  slug?: string;
  name: string;
  root?: string;
  gitEnabled: boolean;
  remoteMode: RemoteMode;
  remote?: string | null;
  remoteSecret?: string | null;
  pushUserBranches: boolean;
}

export interface Job {
  /** The project whose workspace this job lives in. */
  project: string;
  /** test | prod, or "default" when the git workflow is off. */
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
  project: string;
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

/**
 * One open cell, as the kernel's language server needs to see it. `languageId` is
 * the kernel's name for the language and not Monaco's, and `source` is the cell's
 * own text — positions are offsets into it, so nothing may be prepended.
 */
export interface ApiSyncCell {
  id: string;
  languageId: string;
  source: string;
}

/** One language question about one cell, at one position. `source` is what the
 *  editor has right now, so the position means what the cursor means. */
export interface ApiLanguageRequest {
  kind: 'completion' | 'resolve' | 'hover' | 'signatureHelp' | 'definition' | 'metadataSource';
  cellId: string;
  languageId: string;
  source: string;
  line: number;
  character: number;
  item?: unknown;
}

/** One parameter a directive accepts — 'flag' is a bare switch, anything else
 *  takes a value. The connection wizard needs this to know how to write a flag. */
export interface ApiDirectiveParameter {
  name: string;
  aliases?: string[];
  kind?: 'value' | 'flag' | 'keyValue' | 'forbidden';
  required?: boolean;
  enumValues?: string[];
  description?: string;
}

export interface ApiDirective {
  selector: string;
  description?: string;
  parameters?: ApiDirectiveParameter[];
}

/** A cell language the kernel declared, for the picker and syntax highlighting. */
export interface ApiLanguage {
  id: string;
  displayName: string;
  defaultSelector: string | null;
  selectors: string[];
  languageTags: string[];
  directives?: ApiDirective[];
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
  /** What opens a completion list / signature help, as the kernel declares it.
   *  Taken from the handshake rather than restated here, so the editor asks on
   *  exactly the characters the server answers on. */
  completionTriggers?: string[];
  signatureTriggers?: string[];
  /** What the kernel says is wrong in each cell, by cell id. An empty array is a
   *  real answer — it is how a fixed error stops being drawn. */
  diagnostics?: Record<string, LspDiagnostic[]>;
  cells?: Record<string, ApiCellRun>;
}

/** One setting a connection type takes. The kernel owns this schema; the browser
 *  renders it without knowing what any particular connection type is. */
export interface ApiConnectionSetting {
  name: string;
  displayName?: string | null;
  kind: 'text' | 'secret' | 'enum' | 'bool' | 'int' | string;
  required?: boolean;
  /** Exactly one of the settings sharing a group is filled in. */
  oneOfGroup?: string | null;
  enumValues?: string[] | null;
  /** Enum values that mean "a credential follows" — those need a secret ref. */
  credentialValues?: string[] | null;
  requires?: string[] | null;
  default?: string | null;
  /** The flag this maps to on the connect directive. */
  directiveFlag?: string | null;
  /** Set at run time only — never written into the notebook. */
  runtimeOnly?: boolean;
  description?: string | null;
}

export interface ApiConnectionProvider {
  type: string;
  displayName: string;
  description?: string | null;
  languageIds?: string[];
  /** The directive a connection for this provider is written as, e.g. `#!sql-connect`. */
  connectSelector?: string | null;
  settings: ApiConnectionSetting[];
  allowExtraSettings?: boolean;
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

export class ApiError extends Error {
  constructor(readonly status: number, message: string) {
    super(message);
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  // No credential header: the session cookie rides along with a same-origin
  // request on its own, and it is HTTP-only precisely so this code cannot see it.
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      ...(init?.body ? { 'Content-Type': 'application/json' } : {}),
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
      projects: number;
      environments: string[];
      gitEnabled: boolean;
      errors: string[];
    }>('/health'),
  stats: (days = 7) => request<Stats>(`/stats?days=${days}`),

  projects: () => request<{ projects: Project[] }>('/projects'),
  /** `createdRoot` says the folder was made rather than adopted. */
  registerProject: (write: ProjectWrite) =>
    request<{ project: Project; createdRoot: boolean }>('/projects', {
      method: 'POST',
      body: JSON.stringify(write),
    }),
  saveProject: (slug: string, write: ProjectWrite) =>
    request<Project>(`/projects/${encodeURIComponent(slug)}`, {
      method: 'PUT',
      body: JSON.stringify(write),
    }),
  /** Forgets a project. Nothing on disk is touched. */
  forgetProject: (slug: string) =>
    request<void>(`/projects/${encodeURIComponent(slug)}`, { method: 'DELETE' }),
  /** Turns the project's folder into a test/prod workspace. Idempotent. */
  initProject: (slug: string) =>
    request<{ message: string }>(`/projects/${encodeURIComponent(slug)}/init`, { method: 'POST' }),

  members: (slug: string) =>
    request<{ members: ProjectMember[]; candidates: { userId: string; displayName: string }[] }>(
      `/projects/${encodeURIComponent(slug)}/members`,
    ),
  setMember: (slug: string, userId: string, role: ProjectRole) =>
    request<{ granted: boolean }>(
      `/projects/${encodeURIComponent(slug)}/members/${userId}`,
      { method: 'PUT', body: JSON.stringify({ role }) },
    ),
  removeMember: (slug: string, userId: string) =>
    request<void>(`/projects/${encodeURIComponent(slug)}/members/${userId}`, { method: 'DELETE' }),

  // Every project's jobs; the page filters to the one you are looking at.
  jobs: () => request<{ jobs: Job[]; errors: string[] }>('/jobs'),
  job: (env: string, name: string) => request<Job>(`${scope(env)}/jobs/${encodeURIComponent(name)}`),
  createJob: (env: string, job: Partial<Job>) =>
    request<Job>(`${scope(env)}/jobs`, { method: 'POST', body: JSON.stringify(job) }),
  updateJob: (env: string, name: string, job: Partial<Job>) =>
    request<Job>(`${scope(env)}/jobs/${encodeURIComponent(name)}`, { method: 'PUT', body: JSON.stringify(job) }),
  deleteJob: (env: string, name: string) =>
    request<void>(`${scope(env)}/jobs/${encodeURIComponent(name)}`, { method: 'DELETE' }),
  /** Optional parameters apply to this run only; the job's yaml is untouched. */
  runJob: (env: string, name: string, parameters?: Record<string, unknown>) =>
    request<{ runId: string }>(`${scope(env)}/jobs/${encodeURIComponent(name)}/run`, {
      method: 'POST',
      ...(parameters && Object.keys(parameters).length > 0
        ? { body: JSON.stringify({ parameters }) }
        : {}),
    }),
  cancelJob: (env: string, name: string) =>
    request<{ cancelled: boolean }>(`${scope(env)}/jobs/${encodeURIComponent(name)}/cancel`, {
      method: 'POST',
    }),
  jobRuns: (env: string, name: string, limit = 25) =>
    request<Run[]>(`${scope(env)}/jobs/${encodeURIComponent(name)}/runs?limit=${limit}`),

  // Scoped to the selected project: the breadcrumb says which project you are
  // in, so a list that quietly spanned all of them would be saying otherwise.
  runs: (limit = 25) =>
    request<Run[]>(`/runs?limit=${limit}&project=${encodeURIComponent(currentProject)}`),
  run: (id: string) => request<{ run: Run; cells: RunCell[] }>(`/runs/${id}`),
  artifact: (id: string) => request<unknown>(`/runs/${id}/artifact`),
  log: (id: string) =>
    fetch(`/api/runs/${id}/log`).then((r) =>
      r.ok ? r.text() : '',
    ),

  notebooks: () =>
    request<{ environments: { name: string; tree: TreeNode | null }[] }>(`${project()}/notebooks`),

  notebookContent: (env: string, path: string) =>
    fetch(`/api${scope(env)}/notebooks/content?path=${encodeURIComponent(path)}`)
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error(`${r.status}`)))),
  /**
   * `keepalive` for the write that happens as the page goes away. An ordinary
   * fetch is cancelled with the document; a keepalive one is not, at the cost of
   * a 64 KB body cap the browser enforces.
   */
  saveNotebookContent: (path: string, content: string, keepalive = false) =>
    request<{ saved: boolean; branch: string }>(
      `${scope('mine')}/notebooks/content?path=${encodeURIComponent(path)}`,
      { method: 'PUT', body: content, headers: { 'Content-Type': 'text/plain' }, keepalive },
    ),
  // The UI diffs by fetching both environments' content into Monaco. GET
  // /api/git/diff still exists and is still the right thing over curl.
  // The notebook as cells, with the languages this kernel can run — parsed and
  // written server-side so the browser never needs its own copy of the format.
  notebookCells: (env: string, path: string) =>
    request<{ cells: ApiCell[]; languages: ApiLanguage[] }>(
      `${scope(env)}/notebooks/cells?path=${encodeURIComponent(path)}`,
    ),
  saveNotebookCells: (path: string, cells: ApiCell[], keepalive = false) =>
    request<{ saved: boolean; branch: string }>(
      `${scope('mine')}/notebooks/cells?path=${encodeURIComponent(path)}`,
      { method: 'PUT', body: JSON.stringify({ cells }), keepalive },
    ),
  // Interactive execution against the notebook's warm kernel. None of this
  // writes to the run store: an interactive run never appears in run history
  // and can never become the green evidence promotion requires.
  runCells: (path: string, cells: ApiCell[]) =>
    request<{ running: string[] }>(`${scope('mine')}/notebooks/run?path=${encodeURIComponent(path)}`, {
      method: 'POST',
      body: JSON.stringify({ cells }),
    }),
  /** The connection wizard's schema, from the notebook's own kernel. */
  connectionProviders: (path: string, languageId: string) =>
    request<{ providers: ApiConnectionProvider[] }>(
      `${scope('mine')}/notebooks/connections?path=${encodeURIComponent(path)}&languageId=${encodeURIComponent(languageId)}`,
    ),
  /** Starts (or touches) the notebook's kernel. Opening the editor does this, so
   *  language features work on the first keystroke rather than the first run. */
  startSession: (path: string) =>
    request<ApiSession>(`${scope('mine')}/notebooks/session?path=${encodeURIComponent(path)}`, {
      method: 'POST',
    }),
  /** Tells the kernel which cells are open, so completion and hover have documents
   *  to answer about. Authoritative: cells left out are closed. */
  syncCells: (path: string, cells: ApiSyncCell[]) =>
    request<{ started: boolean; sent: number }>(
      `${scope('mine')}/notebooks/sync?path=${encodeURIComponent(path)}`,
      { method: 'POST', body: JSON.stringify({ cells }) },
    ),
  /** One language question about one cell. Returns null when the notebook has no
   *  session yet, or when the kernel had nothing to say — a language feature that
   *  cannot answer is silent, never an error. */
  languageRequest: <T>(path: string, body: ApiLanguageRequest) =>
    request<{ started: boolean; result: T | null }>(
      `${scope('mine')}/notebooks/language?path=${encodeURIComponent(path)}`,
      { method: 'POST', body: JSON.stringify(body) },
    ).then((r) => r.result),
  sessionStatus: (path: string) =>
    request<ApiSession>(`${scope('mine')}/notebooks/session/status?path=${encodeURIComponent(path)}`),
  /** Kills the kernel. Also the only interrupt there is — no RPC surface can
   *  cancel a cell that is already running. */
  restartSession: (path: string) =>
    request<{ restarted: boolean }>(`${scope('mine')}/notebooks/session?path=${encodeURIComponent(path)}`, {
      method: 'DELETE',
    }),

  promotionStatus: (path: string) =>
    request<{
      eligible: boolean;
      reasons: string[];
      paths: string[];
      isDeletion: boolean;
    }>(`${scope('test')}/notebooks/promotion?path=${encodeURIComponent(path)}`),
  promote: (path: string) =>
    request<{ promoted: boolean; commitSha: string }>(
      `${scope('test')}/notebooks/promote?path=${encodeURIComponent(path)}`,
      { method: 'POST' },
    ),

  /** Where your own branch stands against test: unsaved work, and either drift. */
  branchStanding: () =>
    request<BranchStanding>(`${project()}/branch`),
  /** Commits everything on your branch and fast-forwards test onto it. */
  pushToTest: (message: string) =>
    request<{ pushed: boolean; commitSha: string }>(`${project()}/branch/push`, {
      method: 'POST',
      body: JSON.stringify({ message }),
    }),
  /** Merges test into your branch, in your own worktree. */
  updateFromTest: () =>
    request<{ merged: boolean; conflicts: string[] }>(`${project()}/branch/update`, {
      method: 'POST',
    }),

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
