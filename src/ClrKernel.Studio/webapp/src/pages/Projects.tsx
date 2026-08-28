import { FolderGit2, Trash2 } from 'lucide-react';
import { useState } from 'react';
import { toast } from 'sonner';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Badge } from '@/components/ui/badge';
import {
  api,
  type Project,
  type ProjectRole,
  type ProjectWrite,
  type RemoteMode,
} from '../api';
import { ErrorBanner, usePolling } from '../components/common';
import { timeAgo } from '../ipynb';
import { rememberProject, useProjects } from '../projectContext';
import { useIsServerAdmin } from '../sessionContext';

const PROJECT_ROLES: { value: ProjectRole; label: string; hint: string }[] = [
  { value: 'ProjectViewer', label: 'Viewer', hint: 'Reads everything here, changes nothing.' },
  { value: 'ProjectMember', label: 'Member', hint: 'Owns a branch: edits it, runs it, pushes to test.' },
  { value: 'ProjectAdmin', label: 'Admin', hint: 'Promotes to production, configures, manages members.' },
];

/**
 * Who has been granted something on one project.
 *
 * Server Admins are admins of every project without a grant, so they are not
 * offered one — a row that changes nothing is a row that invites you to wonder
 * what it does.
 */
function Members({ project }: { project: Project }) {
  const { data, error, reload } = usePolling(() => api.members(project.slug), null);
  const [problem, setProblem] = useState<string | null>(null);
  const [adding, setAdding] = useState('');

  async function run(work: () => Promise<unknown>) {
    setProblem(null);
    try {
      await work();
      reload();
    } catch (e) {
      setProblem((e as Error).message);
    }
  }

  return (
    <div className="mt-4">
      <h3 className="mb-2 text-base font-semibold">Who can use {project.name}</h3>
      <ErrorBanner error={problem ?? error} />
      <div className="table-box">
        <table className="table">
          <tbody>
            {(data?.members ?? []).map((member) => (
              <tr key={member.userId}>
                <td>{member.displayName}</td>
                <td>
                  <Select
                    value={member.role}
                    onValueChange={(role) =>
                      run(() => api.setMember(project.slug, member.userId, role as ProjectRole))
                    }
                  >
                    <SelectTrigger className="w-[150px]">
                      <SelectValue />
                    </SelectTrigger>
                    <SelectContent>
                      {PROJECT_ROLES.map((r) => (
                        <SelectItem key={r.value} value={r.value}>
                          {r.label}
                        </SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                </td>
                <td className="text-right">
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label={`Remove ${member.displayName}`}
                    onClick={() => run(() => api.removeMember(project.slug, member.userId))}
                  >
                    <Trash2 className="size-3.5" aria-hidden="true" />
                  </Button>
                </td>
              </tr>
            ))}
            {data?.members.length === 0 && (
              <tr>
                <td colSpan={3} className="text-muted-foreground">
                  Nobody has been granted access to this project specifically. Server Admins can
                  always reach it; Server Viewers can read it.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>

      {(data?.candidates ?? []).length > 0 && (
        <div className="mt-2 flex items-center gap-2">
          <Select value={adding} onValueChange={setAdding}>
            <SelectTrigger className="w-[220px]">
              <SelectValue placeholder="Add someone…" />
            </SelectTrigger>
            <SelectContent>
              {(data?.candidates ?? []).map((c) => (
                <SelectItem key={c.userId} value={c.userId}>
                  {c.displayName}
                </SelectItem>
              ))}
            </SelectContent>
          </Select>
          <Button
            size="sm"
            variant="outline"
            disabled={!adding}
            onClick={() =>
              run(async () => {
                await api.setMember(project.slug, adding, 'ProjectMember');
                setAdding('');
              })
            }
          >
            Add as member
          </Button>
        </div>
      )}
    </div>
  );
}

const REMOTE_MODES: { value: RemoteMode; label: string; hint: string }[] = [
  { value: 'Local', label: 'No remote', hint: 'This server holds the only copy.' },
  {
    value: 'ServerAuthoritative',
    label: 'Server wins',
    hint: 'A remote exists and this server pushes to it.',
  },
  {
    value: 'RemoteAuthoritative',
    label: 'Remote wins',
    hint: 'Fetch before promoting; a diverged push fails rather than forcing.',
  },
];

const BLANK: ProjectWrite = {
  name: '',
  root: '',
  gitEnabled: true,
  remoteMode: 'Local',
  remote: '',
  remoteSecret: '',
  pushUserBranches: false,
};

/** The fields a project has, shared by the register form and the edit form. */
function Fields({
  value,
  onChange,
  showRoot,
}: {
  value: ProjectWrite;
  onChange: (next: ProjectWrite) => void;
  showRoot: boolean;
}) {
  const set = (patch: Partial<ProjectWrite>) => onChange({ ...value, ...patch });
  const mode = REMOTE_MODES.find((m) => m.value === value.remoteMode);

  return (
    <div className="grid max-w-[70ch] gap-3">
      <label className="grid gap-1">
        <span className="text-sm text-muted-foreground">Name</span>
        <Input value={value.name} onChange={(e) => set({ name: e.target.value })} placeholder="Finance" />
      </label>

      {showRoot && (
        <label className="grid gap-1">
          <span className="text-sm text-muted-foreground">Folder on this server</span>
          <Input
            value={value.root ?? ''}
            onChange={(e) => set({ root: e.target.value })}
            placeholder="/srv/finance"
            spellCheck={false}
          />
          <span className="text-xs text-muted-subtle">
            An absolute path on the server. It is created if it is not there yet, and cannot be
            changed later — the run history describes what happened there.
          </span>
        </label>
      )}

      <label className="flex items-center gap-2">
        <input
          type="checkbox"
          checked={value.gitEnabled}
          onChange={(e) => set({ gitEnabled: e.target.checked })}
        />
        <span className="text-base">Use the test → prod workflow</span>
      </label>

      <label className="grid gap-1">
        <span className="text-sm text-muted-foreground">Remote</span>
        <Select value={value.remoteMode} onValueChange={(v) => set({ remoteMode: v as RemoteMode })}>
          <SelectTrigger className="w-[240px]">
            <SelectValue />
          </SelectTrigger>
          <SelectContent>
            {REMOTE_MODES.map((m) => (
              <SelectItem key={m.value} value={m.value}>
                {m.label}
              </SelectItem>
            ))}
          </SelectContent>
        </Select>
        <span className="text-xs text-muted-subtle">{mode?.hint}</span>
      </label>

      {value.remoteMode !== 'Local' && (
        <>
          <label className="grid gap-1">
            <span className="text-sm text-muted-foreground">Remote name or url</span>
            <Input
              value={value.remote ?? ''}
              onChange={(e) => set({ remote: e.target.value })}
              placeholder="origin"
              spellCheck={false}
            />
          </label>
          <label className="grid gap-1">
            <span className="text-sm text-muted-foreground">Credential secret name</span>
            <Input
              value={value.remoteSecret ?? ''}
              onChange={(e) => set({ remoteSecret: e.target.value })}
              placeholder="FINANCE_GIT_TOKEN"
              spellCheck={false}
            />
            <span className="text-xs text-muted-subtle">
              The <em>name</em> of a secret, never the secret. It is read at push time from the OS
              credential store or <code>CLRKERNEL_SECRET_*</code>, so nothing here ends up in a file.
            </span>
          </label>
          <label className="flex items-center gap-2">
            <input
              type="checkbox"
              checked={value.pushUserBranches}
              onChange={(e) => set({ pushUserBranches: e.target.checked })}
            />
            <span className="text-base">Push personal branches to the remote too</span>
          </label>
        </>
      )}
    </div>
  );
}

/**
 * The personal worktrees in a project, and the way to be rid of one.
 *
 * A worktree that is clean and fully in test is swept automatically after a month;
 * these are the ones that are not, so removing one means deciding about somebody
 * else's unfinished work. It says which kind it is, and asks twice for the kind
 * that matters.
 */
function Worktrees({ project }: { project: Project }) {
  const { data, error, reload } = usePolling(() => api.worktrees(project.slug), null);
  const [problem, setProblem] = useState<string | null>(null);

  async function remove(userId: string, owner: string, force: boolean) {
    setProblem(null);
    try {
      await api.removeWorktree(project.slug, userId, force);
      reload();
    } catch (e) {
      const message = (e as Error).message;
      if (!force && confirm(`${message}\n\nRemove ${owner}'s branch anyway? It cannot be undone.`)) {
        await remove(userId, owner, true);
        return;
      }
      setProblem(message);
    }
  }

  return (
    <div className="mt-4">
      <h3 className="mb-2 text-base font-semibold">Branches in {project.name}</h3>
      <ErrorBanner error={problem ?? error} />
      <div className="table-box">
        <table className="table">
          <tbody>
            {(data?.worktrees ?? []).map((w) => (
              <tr key={w.userId}>
                <td>{w.owner}</td>
                <td className="text-muted-foreground">
                  {w.dirty
                    ? 'unsaved work'
                    : w.merged
                      ? 'all pushed to test'
                      : 'commits test has not seen'}
                </td>
                <td className="text-muted-subtle">{timeAgo(w.lastCommit)}</td>
                <td className="text-right">
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    aria-label={`Remove ${w.owner}'s branch`}
                    onClick={() => remove(w.userId, w.owner, false)}
                  >
                    <Trash2 className="size-3.5" aria-hidden="true" />
                  </Button>
                </td>
              </tr>
            ))}
            {data?.worktrees.length === 0 && (
              <tr>
                <td colSpan={4} className="text-muted-foreground">
                  Nobody has edited anything here yet.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  );
}

function writeOf(project: Project): ProjectWrite {
  return {
    name: project.name,
    gitEnabled: project.gitEnabled,
    remoteMode: project.remoteMode,
    remote: project.remote ?? '',
    remoteSecret: project.remoteSecret ?? '',
    pushUserBranches: project.pushUserBranches,
  };
}

/**
 * Settings → Projects: what is registered, and the form that registers more.
 *
 * Registering points at a folder that is already on the server. Cloning one from
 * a url is a separate job — it needs the network and a credential resolved before
 * anything exists to configure — and is not here yet.
 */
export function ProjectsSection() {
  const { projects, current, select } = useProjects();
  // Registering and forgetting are server-wide: they decide what exists. Everything
  // else on this page a project's own admins can do.
  const isServerAdmin = useIsServerAdmin();
  const [members, setMembers] = useState<string | null>(null);
  const [worktrees, setWorktrees] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [adding, setAdding] = useState<ProjectWrite | null>(null);
  const [editing, setEditing] = useState<{ slug: string; write: ProjectWrite } | null>(null);

  // The list lives in the provider, so a change here has to reach it: reloading
  // is the honest way to re-derive every project-scoped thing on screen at once.
  async function run(work: () => Promise<unknown>, done: string) {
    setError(null);
    setBusy(true);
    try {
      // A step may say something more specific than the caller could — whether it
      // adopted a folder or made one, for instance.
      const said = await work();
      toast.success(typeof said === 'string' ? said : done);
      window.location.reload();
    } catch (e) {
      setError((e as Error).message);
      setBusy(false);
    }
  }

  return (
    <section className="settings-section">
      <p className="mb-3 max-w-[78ch] text-base text-muted-foreground">
        A project is one repo and one folder on this server, with its own notebooks, jobs and run
        history. Switch between them from the name at the top of the page.
      </p>
      <ErrorBanner error={error} />

      <div className="table-box">
        <table className="table">
          <thead>
            <tr>
              <th>Name</th>
              <th>Folder</th>
              <th>Workflow</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {projects.map((project) => (
              <tr key={project.slug}>
                <td>
                  <button
                    type="button"
                    className="text-primary hover:underline"
                    onClick={() => select(project.slug)}
                  >
                    {project.name}
                  </button>
                  <span className="ml-2 font-mono text-xs text-muted-subtle">{project.slug}</span>
                  {project.slug === current && (
                    <Badge variant="outline" className="ml-2 font-normal">
                      viewing
                    </Badge>
                  )}
                </td>
                <td className="font-mono text-xs text-muted-foreground">{project.root}</td>
                <td>
                  {project.gitEnabled && project.ready ? (
                    <span>
                      test → prod
                      {project.remoteMode !== 'Local' && (
                        <span className="text-muted-foreground"> · {project.remote}</span>
                      )}
                    </span>
                  ) : (
                    // Offered for a flat folder too: setting up the worktrees is how
                    // you ask for the workflow, so it turns it on rather than sending
                    // you to the project editor to tick a box first.
                    <span className="flex items-center gap-2">
                      {!project.gitEnabled && (
                        <span className="text-muted-foreground">flat folder</span>
                      )}
                      <Button
                        variant="outline"
                        size="xs"
                        disabled={busy}
                        onClick={() =>
                          run(() => api.initProject(project.slug), `${project.name} is a workspace now.`)
                        }
                      >
                        <FolderGit2 className="size-3.5" aria-hidden="true" />
                        Set up worktrees
                      </Button>
                    </span>
                  )}
                </td>
                <td className="text-right">
                  <Button
                    variant="ghost"
                    size="xs"
                    onClick={() => setMembers(members === project.slug ? null : project.slug)}
                  >
                    Members
                  </Button>
                  <Button
                    variant="ghost"
                    size="xs"
                    onClick={() => setWorktrees(worktrees === project.slug ? null : project.slug)}
                  >
                    Branches
                  </Button>
                  <Button
                    variant="ghost"
                    size="xs"
                    onClick={() => setEditing({ slug: project.slug, write: writeOf(project) })}
                  >
                    Configure
                  </Button>
                  <Button
                    variant="ghost"
                    size="icon-sm"
                    hidden={!isServerAdmin}
                    aria-label={`Forget ${project.name}`}
                    title="Forget this project. Nothing on disk is deleted."
                    disabled={busy || projects.length === 1 || !isServerAdmin}
                    onClick={() =>
                      run(
                        () => api.forgetProject(project.slug),
                        `${project.name} is no longer registered. Its folder is untouched.`,
                      )
                    }
                  >
                    <Trash2 className="size-3.5" aria-hidden="true" />
                  </Button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {members != null && projects.some((p) => p.slug === members) && (
        <Members project={projects.find((p) => p.slug === members)!} />
      )}

      {worktrees != null && projects.some((p) => p.slug === worktrees) && (
        <Worktrees project={projects.find((p) => p.slug === worktrees)!} />
      )}

      {editing && (
        <div className="mt-4">
          <h3 className="mb-2 text-base font-semibold">Configure {editing.write.name}</h3>
          <Fields
            value={editing.write}
            showRoot={false}
            onChange={(write) => setEditing({ ...editing, write })}
          />
          <div className="mt-3 flex gap-2">
            <Button
              size="sm"
              disabled={busy}
              onClick={() =>
                run(() => api.saveProject(editing.slug, editing.write), 'Project saved.')
              }
            >
              Save project
            </Button>
            <Button variant="outline" size="sm" onClick={() => setEditing(null)}>
              Cancel
            </Button>
          </div>
        </div>
      )}

      {adding ? (
        <div className="mt-4">
          <h3 className="mb-2 text-base font-semibold">Register a project</h3>
          <Fields value={adding} showRoot onChange={setAdding} />
          <div className="mt-3 flex gap-2">
            <Button
              size="sm"
              disabled={busy || !adding.name.trim() || !adding.root?.trim()}
              onClick={() =>
                run(async () => {
                  const { project: made, createdRoot } = await api.registerProject(adding);
                  // Land in the project you just registered: having to go and
                  // find it in the switcher afterwards is a small indignity.
                  rememberProject(made.slug);
                  // Worth saying out loud — a typo in the path is otherwise an
                  // empty project that looks like a working one.
                  return createdRoot
                    ? `${made.name} is registered. Created ${made.root}.`
                    : `${made.name} is registered.`;
                }, `${adding.name} is registered.`)
              }
            >
              Register
            </Button>
            <Button variant="outline" size="sm" onClick={() => setAdding(null)}>
              Cancel
            </Button>
          </div>
        </div>
      ) : (
        isServerAdmin && (
          <Button className="mt-3" size="sm" variant="outline" onClick={() => setAdding({ ...BLANK })}>
            Register a project
          </Button>
        )
      )}
    </section>
  );
}
