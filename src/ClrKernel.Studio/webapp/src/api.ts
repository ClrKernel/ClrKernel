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

/**
 * Which branch the open notebook is being read from. The same argument as
 * `currentProject`: the editor holds one notebook on one branch, and the seven
 * routes that talk to that notebook's kernel would otherwise all be carrying the
 * identical value down through Monaco's model map to get here.
 *
 * Writes are deliberately *not* on it — `saveNotebookContent` and
 * `saveNotebookCells` name `mine` outright, because the branch you are reading is
 * never the branch you write to. Reading prod and saving is "copy to my branch",
 * not "edit production".
 */
let currentBranch = 'mine';

export function setBranch(branch: string): void {
  currentBranch = branch;
}

/** The branch the editor currently has open. */
const open = () => scope(currentBranch);

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

export interface BranchTree {
  name: string;
  label: string;
  tree: TreeNode | null;
}

export interface CronPreview {
  valid: boolean;
  /** Why it was refused, from Cronos, or null. */
  error: string | null;
  /** The next few occurrences as ISO instants. Always UTC — the scheduler's clock. */
  next: string[];
}

export interface BranchSummary {
  /** What a route calls it: `mine`, `user-<id>`, `test`, `prod`. */
  id: string;
  label: string;
  owner: string | null;
  mine: boolean;
  writable: boolean;
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

export interface Worktree {
  userId: string;
  owner: string;
  lastCommit: string;
  /** Saved but never pushed. */
  dirty: boolean;
  /** Everything on it is already in test, so removing it loses nothing. */
  merged: boolean;
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
  attempt: number;
  createdAt: string;
  startedAt: string | null;
  finishedAt: string | null;
  errorSummary: string | null;
  artifactPath: string | null;
  logPath: string | null;
  /** Who pressed run. Null for a scheduled run, and null for any manual run
   *  recorded before the column existed — read it beside `trigger`, never alone. */
  actorId: string | null;
  actorName: string | null;
  /** The branch's git HEAD when this started. Null without the git workflow —
   *  and then there is no exact version to go back to. */
  commitSha: string | null;
  /** Uncommitted changes under the job's files, so the sha is not what ran. */
  wasDirty: boolean;
  /** Ran with one-off parameters that were not kept, so it cannot be repeated. */
  hadOverrides: boolean;
  /** For a rerun, the run it repeats. For a chained run, the upstream success. */
  causedByRunId: string | null;
}

/** One page of the monitoring grid. `hasMore` costs a row, where a total would
 *  cost a COUNT(*) over the whole history on every poll. */
export interface RunPage {
  runs: Run[];
  hasMore: boolean;
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
  /** What a picker groups this under — 'SQL' clusters the dialects. */
  category?: string | null;
  /** The connections.json `$type`s this language's cells can run on. A
   *  compatibility declaration, not part of the language's identity: a cell does
   *  not change language when it is pointed at a different connection. */
  supportedProviders?: string[];
  /** The id an editor should give this language's cells. Distinct per language:
   *  it identifies a cell, so two sharing one would route one to the other. */
  editorLanguageId?: string;
  /** The syntax to highlight those cells with when the editor has no grammar of
   *  its own for `editorLanguageId`. Several languages may share one — the SQL
   *  dialects are three identities and one tokenizer. */
  grammarId?: string;
  /** Two to four characters naming this language in a chip. */
  monogram?: string;
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
  /** Only on the Connections area's own list: whether this server can open the
   *  connection itself, as opposed to only storing it for a notebook to name. */
  queryable?: boolean;
  settings: ApiConnectionSetting[];
  allowExtraSettings?: boolean;
}

/** One problem with a `*.jobs.yaml`, positioned so the editor can underline it. */
export interface ApiJobsProblem {
  /** 1-based, as editors count. */
  line: number;
  column: number;
  message: string;
}

export interface TreeNode {
  name: string;
  path: string;
  isDirectory: boolean;
  kind: 'notebook' | 'jobs' | 'file' | null;
  /** Whether a write to this path would be accepted — the server's answer. */
  editable?: boolean;
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
  /** Only projects that ran something in the window — a row of zeroes for a
   *  project nobody scheduled is noise, not information. */
  byProject: ProjectStats[];
}

export interface ProjectStats {
  project: string;
  total: number;
  succeeded: number;
  failed: number;
}

export type NotificationEventName =
  'JobFailed' | 'JobRecovered' | 'RunTooSlow' | 'PromotedToProd';

/** One rule: when this happens here, tell these channels. */
export interface NotificationRule {
  event: NotificationEventName;
  /** Empty means every project. */
  project?: string | null;
  /** Empty means every branch that runs anything. */
  environment?: string | null;
  to: string[];
  /** How slow is too slow, for RunTooSlow. */
  afterSeconds?: number | null;
  enabled: boolean;
}

/** One notification that was attempted — including one that did not arrive. */
export interface NotificationDelivery {
  id: string;
  project: string;
  environment: string | null;
  event: NotificationEventName;
  channel: string;
  subject: string | null;
  runId: string | null;
  sentAt: string;
  /** Null when it went out. Set when it did not, and why. */
  error: string | null;
}

/** One scheduled occurrence that has not happened yet. */
export interface UpcomingRun {
  project: string;
  environment: string;
  job: string;
  jobsFile: string;
  cron: string;
  at: string;
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

  /** When things get sent, as against where — Channels is the destinations. */
  notificationRules: () =>
    request<{
      rules: NotificationRule[];
      channels: string[];
      events: NotificationEventName[];
      errors: string[];
    }>('/notification-rules'),
  saveNotificationRules: (rules: NotificationRule[]) =>
    request<{ rules: NotificationRule[] }>('/notification-rules', {
      method: 'PUT',
      body: JSON.stringify(rules),
    }),
  /** What was actually sent, and what was not. */
  notifications: (failuresOnly = false, limit = 50) =>
    request<{ deliveries: NotificationDelivery[] }>(
      `/notifications?failuresOnly=${failuresOnly}&limit=${limit}`,
    ),

  /** What the crons say is next, across every project you can see. */
  upcoming: (limit = 8) =>
    request<{ upcoming: UpcomingRun[] }>(`/schedule/upcoming?limit=${limit}`),

  projects: () =>
    request<{ projects: Project[]; projectsRoot: string | null }>('/projects'),
  /** Folders on the server, for picking where a project goes. Server admins only. */
  serverFolders: (path?: string) =>
    request<{
      path: string;
      /** Null at the filesystem root, which is where "up" stops. */
      parent: string | null;
      projectsRoot: string | null;
      /** `taken` is a folder some project is already rooted at. */
      folders: { name: string; path: string; taken: boolean }[];
    }>(`/server/folders${path ? `?path=${encodeURIComponent(path)}` : ''}`),
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

  /** The personal worktrees in a project, for whoever has to tidy up. */
  worktrees: (slug: string) =>
    request<{ worktrees: Worktree[] }>(`/projects/${encodeURIComponent(slug)}/worktrees`),
  /** Removes one. `force` is needed for a branch holding work test has not seen. */
  removeWorktree: (slug: string, userId: string, force = false) =>
    request<void>(
      `/projects/${encodeURIComponent(slug)}/worktrees/${userId}?force=${force}`,
      { method: 'DELETE' },
    ),

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
    request<RunPage>(`/runs?limit=${limit}&project=${encodeURIComponent(currentProject)}`),

  // The monitoring grid, which is the one view that is deliberately *not* scoped
  // to the selected project — Project is its first column. `query` comes from
  // runFilters.runsQuery, and the server applies all of it; the page is a page.
  runGrid: (query: string) => request<RunPage>(`/runs?${query}`),

  /** Runs recorded runs again. `exactVersion` goes back to the commit each one
   *  recorded, and is one run at a time; the default runs branch HEAD. */
  rerun: (runIds: string[], exactVersion = false) =>
    request<{
      project: string;
      environment: string;
      exactVersion: boolean;
      started: { runId: string; rerunOf: string; job: string; sha: string | null }[];
      refused: { runId: string; reason: string }[];
    }>('/runs/rerun', { method: 'POST', body: JSON.stringify({ runIds, exactVersion }) }),
  run: (id: string) => request<{ run: Run; cells: RunCell[] }>(`/runs/${id}`),
  artifact: (id: string) => request<unknown>(`/runs/${id}/artifact`),
  log: (id: string) =>
    fetch(`/api/runs/${id}/log`).then((r) =>
      r.ok ? r.text() : '',
    ),

  /** What a cron expression actually does, from the scheduler's own parser. */
  cronPreview: (expression: string) =>
    request<CronPreview>(`/cron/preview?expression=${encodeURIComponent(expression)}`),

  /**
   * Every branch's file tree. `name` is what a route calls it, `label` is what a
   * person reads — they differ for the branches that belong to somebody, because
   * `user-<id>` is not a thing to show anyone.
   */
  notebooks: () =>
    request<{ environments: BranchTree[] }>(`${project()}/notebooks`),

  notebookContent: (env: string, path: string) =>
    fetch(`/api${scope(env)}/notebooks/content?path=${encodeURIComponent(path)}`)
      .then((r) => (r.ok ? r.text() : Promise.reject(new Error(`${r.status}`)))),
  /**
   * `keepalive` for the write that happens as the page goes away. An ordinary
   * fetch is cancelled with the document; a keepalive one is not, at the cost of
   * a 64 KB body cap the browser enforces.
   */
  saveNotebookContent: (path: string, content: string, keepalive = false) =>
    request<{ saved: boolean; branch: string; problems: ApiJobsProblem[] | null }>(
      `${scope('mine')}/notebooks/content?path=${encodeURIComponent(path)}`,
      { method: 'PUT', body: content, headers: { 'Content-Type': 'text/plain' }, keepalive },
    ),
  /** Renames it, or moves it out of the scratch folder — the same operation. */
  moveNotebook: (path: string, to: string) =>
    request<{ moved: boolean; path: string; branch: string }>(
      `${scope('mine')}/notebooks/move?path=${encodeURIComponent(path)}`,
      { method: 'POST', body: JSON.stringify({ to }) },
    ),
  // The UI diffs by fetching both environments' content into Monaco. GET
  // /api/git/diff still exists and is still the right thing over curl.
  // The notebook as cells, with the languages this kernel can run — parsed and
  // written server-side so the browser never needs its own copy of the format.
  notebookCells: (env: string, path: string) =>
    request<{
      cells: ApiCell[];
      languages: ApiLanguage[];
      /** Connection names this notebook uses that only their owner can resolve.
       *  Empty for everybody else's notebooks, because they cannot see them. */
      privateConnections?: string[];
      /** Every connection name it references, which is what SQL cells complete
       *  table and column names against. */
      connections?: string[];
    }>(
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
    request<{ running: string[] }>(`${open()}/notebooks/run?path=${encodeURIComponent(path)}`, {
      method: 'POST',
      body: JSON.stringify({ cells }),
    }),
  /** The connection wizard's schema, from the notebook's own kernel. */
  connectionProviders: (path: string, languageId: string) =>
    request<{ providers: ApiConnectionProvider[] }>(
      `${open()}/notebooks/connections?path=${encodeURIComponent(path)}&languageId=${encodeURIComponent(languageId)}`,
    ),
  /** Starts (or touches) the notebook's kernel. Opening the editor does this, so
   *  language features work on the first keystroke rather than the first run. */
  startSession: (path: string) =>
    request<ApiSession>(`${open()}/notebooks/session?path=${encodeURIComponent(path)}`, {
      method: 'POST',
    }),
  /** Tells the kernel which cells are open, so completion and hover have documents
   *  to answer about. Authoritative: cells left out are closed. */
  syncCells: (path: string, cells: ApiSyncCell[]) =>
    request<{ started: boolean; sent: number }>(
      `${open()}/notebooks/sync?path=${encodeURIComponent(path)}`,
      { method: 'POST', body: JSON.stringify({ cells }) },
    ),
  /** One language question about one cell. Returns null when the notebook has no
   *  session yet, or when the kernel had nothing to say — a language feature that
   *  cannot answer is silent, never an error. */
  languageRequest: <T>(path: string, body: ApiLanguageRequest) =>
    request<{ started: boolean; result: T | null }>(
      `${open()}/notebooks/language?path=${encodeURIComponent(path)}`,
      { method: 'POST', body: JSON.stringify(body) },
    ).then((r) => r.result),
  sessionStatus: (path: string) =>
    request<ApiSession>(`${open()}/notebooks/session/status?path=${encodeURIComponent(path)}`),
  /** Kills the kernel. Also the only interrupt there is — no RPC surface can
   *  cancel a cell that is already running. */
  restartSession: (path: string) =>
    request<{ restarted: boolean }>(`${open()}/notebooks/session?path=${encodeURIComponent(path)}`, {
      method: 'DELETE',
    }),

  promotionStatus: (path: string) =>
    request<{
      eligible: boolean;
      reasons: string[];
      paths: string[];
      isDeletion: boolean;
      /** Schedules this promotion switches off, so the confirmation can name them. */
      unscheduling: { name: string; cron: string | null; nextRun: string | null }[];
    }>(`${scope('test')}/notebooks/promotion?path=${encodeURIComponent(path)}`),
  promote: (path: string) =>
    request<{ promoted: boolean; commitSha: string }>(
      `${scope('test')}/notebooks/promote?path=${encodeURIComponent(path)}`,
      { method: 'POST' },
    ),

  /** Every branch of this project, with who owns each and which you may write to. */
  branches: () => request<{ branches: BranchSummary[] }>(`${project()}/branches`),

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

  // --- connections ---------------------------------------------------------

  connections: () => request<{ connections: ApiConnection[] }>('/connections'),
  /** The form's schema, and whether this server can keep a password at all. */
  connectionProviderSchema: () =>
    request<{
      providers: ApiConnectionProvider[];
      canPersistSecrets: boolean;
      secretHelp: string | null;
      /** When on, a private connection needs a least-privilege login too — so the
       *  form has to offer one, or it can never be made runnable. */
      privateConnectionsReadOnly: boolean;
    }>('/connections/providers'),
  saveConnection: (id: string | null, body: ApiConnectionSave) =>
    request<ApiConnection>(id == null ? '/connections' : `/connections/${encodeURIComponent(id)}`, {
      method: id == null ? 'POST' : 'PUT',
      body: JSON.stringify(body),
    }),
  deleteConnection: (id: string) =>
    request<{ removed: string }>(`/connections/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  /** Tests what is on screen without saving it: a connection that does not answer
   *  is one you probably do not want stored. */
  testDraftConnection: (body: ApiConnectionSave) =>
    request<{ ok: boolean; error: string | null }>(
      '/connections/test', { method: 'POST', body: JSON.stringify(body) }),
  testConnection: (id: string, password?: string) =>
    request<{ ok: boolean; error: string | null }>(
      `/connections/${encodeURIComponent(id)}/test`,
      { method: 'POST', body: JSON.stringify({ password }) },
    ),
  /** The client names the query so Cancel can name it too — before the response
   *  it would otherwise have learned the id from has arrived. */
  runQuery: (id: string, sql: string, queryId: string, password?: string) =>
    request<ApiQueryResult>(`/connections/${encodeURIComponent(id)}/query`, {
      method: 'POST',
      body: JSON.stringify({ sql, queryId, password }),
    }),
  cancelQuery: (id: string, queryId: string) =>
    request<{ cancelled: boolean }>(`/connections/${encodeURIComponent(id)}/cancel`, {
      method: 'POST',
      body: JSON.stringify({ queryId }),
    }),
  /** Drops the pooled sockets. The tree forgets what it loaded at the same time,
   *  so "connected" and "we have its objects" stay one fact rather than two. */
  disconnectConnection: (id: string) =>
    request<{ disconnected: string }>(`/connections/${encodeURIComponent(id)}/disconnect`, {
      method: 'POST',
    }),
  connectionMetadata: <T>(id: string, body: ApiMetadataRequest) =>
    request<ApiMetadataReply<T>>(`/connections/${encodeURIComponent(id)}/metadata`, {
      method: 'POST',
      body: JSON.stringify(body),
    }),
  connectionHistory: (id: string) =>
    request<{ history: ApiQueryAudit[] }>(`/connections/${encodeURIComponent(id)}/history`),

  // --- what you have run, and what you have kept ---------------------------

  /** Yours, across every connection — including your own private ones, which the
   *  per-connection audit deliberately never shows to anybody else. */
  queryHistory: () => request<{ history: ApiQueryAudit[] }>('/queries/history'),
  savedQueries: () => request<{ queries: ApiSavedQuery[] }>('/queries'),
  saveQuery: (body: {
    id?: string;
    name: string;
    scope: ConnectionScope;
    connectionId?: string | null;
    connectionName?: string | null;
    sql: string;
  }) => request<ApiSavedQuery>('/queries', { method: 'POST', body: JSON.stringify(body) }),
  deleteSavedQuery: (id: string) =>
    request<{ removed: string }>(`/queries/${encodeURIComponent(id)}`, { method: 'DELETE' }),

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

// --- connections ----------------------------------------------------------
//
// Server-wide, not per project: one list of shared connections a server admin
// manages and each person's own, which is why none of these routes go through
// `scope()`.

export type ConnectionScope = 'shared' | 'private';

export interface ApiConnection {
  id: string;
  name: string;
  scope: ConnectionScope;
  type: string;
  settings: Record<string, string>;
  /** Whether a password exists. Never the password. */
  secretConfigured: boolean;
  secretRef: string | null;
  promptForPassword: boolean;
  readOnlyUser: string | null;
  readOnlySecretConfigured: boolean;
  /** False with a reason rather than a button that fails. */
  canExecute: boolean;
  canExecuteReason: string | null;
  /** Whether this server can open it, as opposed to only storing it for a notebook
   *  to name. False for providers whose driver is loaded into a kernel session. */
  queryable: boolean;
  canEdit: boolean;
  timeoutSeconds: number;
  rowCap: number;
  updatedAt: string;
}

export interface ApiConnectionSave {
  name: string;
  scope: ConnectionScope;
  type: string;
  settings: Record<string, string>;
  password?: string;
  secretRef?: string;
  promptForPassword?: boolean;
  readOnlyUser?: string;
  readOnlyPassword?: string;
  readOnlySecretRef?: string;
  timeoutSeconds?: number;
  rowCap?: number;
}

export interface ApiResultSet {
  columns: string[];
  /** `number` | `date` | `string`, for type-aware sorting. */
  types: string[];
  /** Already text, and `null` where the database had NULL. */
  rows: (string | null)[][];
  /** The cap stopped it short. There is no total — knowing one costs a COUNT. */
  truncated: boolean;
}

export interface ApiQueryResult {
  queryId: string;
  resultSets: ApiResultSet[];
  messages: string[];
  rowsAffected: number;
  elapsedMs: number;
  canceled: boolean;
  error: string | null;
}

export interface ApiMetadataNode {
  name: string;
  kind: 'database' | 'schema' | 'table' | 'view' | 'procedure' | 'function';
}

export interface ApiColumnDetail {
  name: string;
  type: string;
  nullable: boolean;
  primaryKey: boolean;
  identity: boolean;
}

export interface ApiObjectDetail {
  columns: ApiColumnDetail[];
  keys: string[];
  indexes: string[];
}

export interface ApiMetadataRequest {
  level: 'databases' | 'schemas' | 'objects' | 'detail' | 'script' | 'completions';
  database?: string;
  schema?: string;
  object?: string;
  kind?: string;
  /** Only for `script`: create | drop | select | insert | update | delete | execute. */
  variant?: string;
  password?: string;
}

/** `supported: false` is a provider this server cannot open — the tree shows the
 *  connection as a leaf rather than as a folder that opens onto nothing. */
export interface ApiMetadataReply<T> {
  supported: boolean;
  reason?: string;
  error?: string;
  payload?: T;
}

export interface ApiSavedQuery {
  id: string;
  name: string;
  scope: ConnectionScope;
  connectionId: string | null;
  connectionName: string | null;
  sql: string;
  createdByName: string | null;
  updatedAt: string;
  /** Whether this reader may change it — shared ones are a server admin's. */
  canEdit: boolean;
}

export interface ApiQueryAudit {
  id: string;
  connectionId: string;
  connectionName: string;
  actorId: string;
  actorName: string;
  startedAt: string;
  durationMs: number;
  statement: string;
  leastPrivilege: boolean;
  outcome: string;
  rowsAffected: number;
  errorSummary: string | null;
  /** Which kind of row this is, and therefore who may read it. Private ones are
   *  only ever their own actor's. */
  scope?: ConnectionScope;
}

export function isActive(status: RunStatus): boolean {
  return status === 'Pending' || status === 'Running';
}
