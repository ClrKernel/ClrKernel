import { ChevronDown, ChevronRight, FilePlus2, FolderClosed, GitBranch } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import {
  Select,
  SelectContent,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { Button } from '@/components/ui/button';
import { api, projectSlug, type TreeNode } from '../api';
import { BranchOptions, ErrorBanner, PageHeader, usePolling } from '../components/common';
import { FileBadge } from '../components/FileBadge';
import { createNotebook, promptForNotebook } from '../newNotebook';
import { loadBranch, saveBranch } from '../prefs';
import { editPath, jobPath, newJobPath } from '../routes';
import { useIsProjectMember } from '../sessionContext';

function Node({
  node,
  env,
  depth,
  mayEdit,
  onCreate,
}: {
  node: TreeNode;
  env: string;
  depth: number;
  /** Whether this person may write jobs — not whether this branch is writable. */
  mayEdit: boolean;
  onCreate: (path: string) => void;
}) {
  const [open, setOpen] = useState(true);
  // Indent in the row's own padding rather than by nesting <ul>s, so a deep
  // path still gets the full-width hover band.
  const indent = { paddingLeft: `${16 + depth * 14}px` };
  const row =
    'flex items-center gap-1.5 py-[5px] pr-4 hover:bg-muted';

  if (node.isDirectory) {
    return (
      <>
        <button
          type="button"
          className={`${row} w-full text-left text-base font-medium outline-none focus-visible:ring-2 focus-visible:ring-ring`}
          style={indent}
          onClick={() => setOpen(!open)}
          aria-expanded={open}
        >
          {/* The chevron turns; the folder does not. Two drawings for one piece
              of information is one more than anybody reads. */}
          {open ? (
            <ChevronDown className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />
          ) : (
            <ChevronRight className="size-3 shrink-0 text-muted-subtle" aria-hidden="true" />
          )}
          <FolderClosed className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
          {node.name}
        </button>
        {open &&
          (node.children ?? []).map((child) => (
            <Node
              key={child.path}
              node={child}
              env={env}
              depth={depth + 1}
              mayEdit={mayEdit}
              onCreate={onCreate}
            />
          ))}
      </>
    );
  }

  // The project and the branch are in the path for the same reason the project
  // is in a job's: a shared link has to open the notebook it was shared for, on
  // the branch it was shared from.
  const href = editPath(projectSlug(), env, node.path);
  return (
    <div className={row} style={indent}>
      {/* The chevron's width, so a file lines up under the folder icon rather
          than under its arrow. */}
      <span aria-hidden="true" className="size-3 shrink-0" />
      <FileBadge name={node.name} />
      {/* Openable on every branch. test and prod are read-only there rather than
          unopenable — the editor is how you read a notebook, not only how you
          change one. */}
      <Link className="font-mono text-code hover:text-primary hover:underline" to={href}>
        {node.name}
      </Link>
      {node.jobs?.map((job) => (
        <Link
          key={job}
          to={jobPath(projectSlug(), env, job)}
          className="rounded-full border border-env-prod-border bg-env-prod-bg px-2 py-px text-xs font-semibold text-env-prod hover:no-underline"
        >
          {job}
        </Link>
      ))}
      {/* A job runs a notebook, so this belongs on notebooks and nowhere else —
          now that every file is listed, `mayEdit` alone would offer to hang a job
          off a .txt. */}
      {mayEdit && node.kind === 'notebook' && (
        <button
          type="button"
          className="text-xs text-muted-subtle outline-none hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
          onClick={() => onCreate(node.path)}
        >
          + job
        </button>
      )}
    </div>
  );
}

export function Files() {
  const navigate = useNavigate();
  const mayEdit = useIsProjectMember();
  const { data, error, reload } = usePolling(() => api.notebooks(), null);
  const { data: health } = usePolling(() => api.health(), null);
  const [notice, setNotice] = useState<string | null>(null);

  const environments = (data?.environments ?? []).filter((e) => e.tree != null);
  const [env, setEnv] = useState<string>('');
  // The list arrives after first paint, so the initial selection is set once it
  // does: the branch you were last on in this project, then your own, then
  // whatever there is. Checked against the list rather than trusted — a
  // remembered branch can be one that no longer exists, and a Select whose value
  // matches no option renders blank.
  useEffect(() => {
    if (!env && environments.length > 0) {
      const remembered = loadBranch(projectSlug());
      setEnv(
        environments.find((e) => e.name === remembered)?.name
          ?? environments.find((e) => e.name === 'mine')?.name
          ?? environments[0].name,
      );
    }
  }, [environments.length]);

  /** Remembered on change, so nobody re-picks their branch on every visit. */
  function pick(branch: string) {
    setEnv(branch);
    saveBranch(projectSlug(), branch);
  }

  const selected = environments.find((e) => e.name === env);
  // Writing needs both the git workflow and a role that may write. Which branch
  // is on screen is a separate question: everything is written to your own, so
  // browsing test does not stop you making a notebook.
  const mayWrite = (health?.gitEnabled ?? false) && mayEdit;

  /** Makes it on your branch and opens it there, whichever branch is on screen. */
  async function create() {
    const wanted = promptForNotebook();
    if (wanted == null) {
      return;
    }
    setNotice(null);
    try {
      await createNotebook(wanted);
      reload();
      navigate(editPath(projectSlug(), 'mine', wanted));
    } catch (e) {
      setNotice((e as Error).message);
    }
  }

  return (
    <div>
      <PageHeader
        title="Files"
        description={
          <>
            Every <code className="font-mono text-code">*.jobs.yaml</code> found here defines jobs.
            Pick a notebook to add one, or click its name to open the editor.
          </>
        }
      />
      <ErrorBanner error={error} />
      <ErrorBanner error={notice} />

      {health && !health.gitEnabled && (
        <Alert variant="warning" className="mb-4 max-w-[640px]">
          <AlertTitle>Editing needs the git workflow</AlertTitle>
          <AlertDescription>
            <p>
              Editing notebooks in the browser needs the test→prod git workflow, so every save is a
              commit. Enable it once (stop the server first):
            </p>
            <pre className="my-2 overflow-x-auto rounded-lg bg-muted px-2 py-1.5 font-mono text-code text-foreground">
              clrkernel-studio git init --notebooks &lt;your notebooks folder&gt;
            </pre>
            <p>
              Then restart — test notebooks get an <strong>Edit</strong> button, and changes promote
              to production after a green run.
            </p>
          </AlertDescription>
        </Alert>
      )}

      {environments.length === 0 ? (
        <p className="text-base text-muted-foreground">No notebooks under the notebooks root.</p>
      ) : (
        <>
          {/* Which worktree is being listed: your own branch, then the two that
              run. Everything but your own is read-only, which the chip says
              rather than leaving it to be inferred from a name. */}
          <div className="mb-3 flex items-center gap-2">
            <GitBranch className="size-[15px] shrink-0 text-muted-subtle" aria-hidden="true" />
            <Select value={env} onValueChange={pick}>
              <SelectTrigger aria-label="Branch">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <BranchOptions branches={environments} />
              </SelectContent>
            </Select>
            {env !== 'mine' && (
              <span className="rounded-full border border-border px-2 py-px text-xs font-semibold text-muted-subtle">
                read-only
              </span>
            )}
            <span className="flex-1" />
            {mayWrite && (
              <Button variant="outline" size="sm" onClick={create}>
                <FilePlus2 className="size-3.5" aria-hidden="true" />
                New notebook
              </Button>
            )}
          </div>

          <div className="max-w-[640px] overflow-hidden rounded-2xl border border-border bg-card py-2.5">
            {(selected?.tree?.children ?? []).map((child) => (
              <Node
                key={`${env}/${child.path}`}
                node={child}
                env={env}
                depth={0}
                mayEdit={mayWrite}
                onCreate={(path) =>
                  // A job is edited on your branch and starts running when you
                  // push it, so a new one is written there whichever branch you
                  // were reading the notebook on.
                  navigate(newJobPath(projectSlug(), 'mine', path))
                }
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
