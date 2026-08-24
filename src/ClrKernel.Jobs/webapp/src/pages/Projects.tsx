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
import { api, type Project, type ProjectWrite, type RemoteMode } from '../api';
import { ErrorBanner } from '../components/common';
import { rememberProject, useProjects } from '../projectContext';

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
            An absolute path that already exists. It cannot be changed later — the run history
            describes what happened there.
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
      await work();
      toast.success(done);
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
                  {!project.gitEnabled ? (
                    <span className="text-muted-foreground">flat folder</span>
                  ) : project.ready ? (
                    <span>
                      test → prod
                      {project.remoteMode !== 'Local' && (
                        <span className="text-muted-foreground"> · {project.remote}</span>
                      )}
                    </span>
                  ) : (
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
                  )}
                </td>
                <td className="text-right">
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
                    aria-label={`Forget ${project.name}`}
                    title="Forget this project. Nothing on disk is deleted."
                    disabled={busy || projects.length === 1}
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
                  // Land in the project you just registered: having to go and
                  // find it in the switcher afterwards is a small indignity.
                  rememberProject((await api.registerProject(adding)).slug);
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
        <Button className="mt-3" size="sm" variant="outline" onClick={() => setAdding({ ...BLANK })}>
          Register a project
        </Button>
      )}
    </section>
  );
}
