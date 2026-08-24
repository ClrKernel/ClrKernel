import { GitBranch } from 'lucide-react';
import { useEffect, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select';
import { api, type TreeNode } from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';
import { useCanWrite } from '../sessionContext';

function Node({
  node,
  env,
  depth,
  editable,
  onCreate,
}: {
  node: TreeNode;
  env: string;
  depth: number;
  editable: boolean;
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
          <span aria-hidden="true" className="w-3 shrink-0 text-[10px] text-muted-subtle">
            {open ? '▾' : '▸'}
          </span>
          {node.name}
        </button>
        {open &&
          (node.children ?? []).map((child) => (
            <Node
              key={child.path}
              node={child}
              env={env}
              depth={depth + 1}
              editable={editable}
              onCreate={onCreate}
            />
          ))}
      </>
    );
  }

  if (node.kind === 'jobs') {
    return (
      <div className={`${row} font-mono text-code text-muted-subtle`} style={indent}>
        <span aria-hidden="true" className="w-3 shrink-0" />
        {node.name}
      </div>
    );
  }

  const href = `/edit?path=${encodeURIComponent(node.path)}`;
  return (
    <div className={row} style={indent}>
      <span aria-hidden="true" className="w-3 shrink-0" />
      {editable ? (
        <Link className="font-mono text-code hover:text-primary hover:underline" to={href}>
          {node.name}
        </Link>
      ) : (
        <span className="font-mono text-code">{node.name}</span>
      )}
      {node.jobs?.map((job) => (
        <Link
          key={job}
          to={`/jobs/${env}/${encodeURIComponent(job)}`}
          className="rounded-full border border-env-prod-border bg-env-prod-bg px-2 py-px text-xs font-semibold text-env-prod hover:no-underline"
        >
          {job}
        </Link>
      ))}
      {editable && (
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

export function Notebooks() {
  const navigate = useNavigate();
  const canWrite = useCanWrite();
  const { data, error } = usePolling(() => api.notebooks(), null);
  const { data: health } = usePolling(() => api.health(), null);

  const environments = (data?.environments ?? []).filter((e) => e.tree != null);
  const [env, setEnv] = useState<string>('');
  // The list arrives after first paint, so the initial selection is set once it
  // does — test if it exists, because test is the one you can edit.
  useEffect(() => {
    if (!env && environments.length > 0) {
      setEnv(environments.find((e) => e.name === 'test')?.name ?? environments[0].name);
    }
  }, [environments.length]);

  const selected = environments.find((e) => e.name === env);
  // Editing needs both the git workflow and a role that may write.
  const editable = env === 'test' && (health?.gitEnabled ?? false) && canWrite;

  return (
    <div>
      <PageHeader
        title="Notebooks"
        description={
          <>
            Every <code className="font-mono text-code">*.jobs.yaml</code> found here defines jobs.
            Pick a notebook to add one, or click its name to open the editor.
          </>
        }
      />
      <ErrorBanner error={error} />

      {health && !health.gitEnabled && (
        <Alert variant="warning" className="mb-4 max-w-[640px]">
          <AlertTitle>Editing needs the git workflow</AlertTitle>
          <AlertDescription>
            <p>
              Editing notebooks in the browser needs the test→prod git workflow, so every save is a
              commit. Enable it once (stop the server first):
            </p>
            <pre className="my-2 overflow-x-auto rounded-lg bg-muted px-2 py-1.5 font-mono text-code text-foreground">
              clrkernel-jobs git init --notebooks &lt;your notebooks folder&gt;
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
          {/* The handoff shows a branch picker. There is no branch API — the
              real axis is the environment, which *is* a worktree when the git
              workflow is on, so this is the same control over honest data. */}
          <div className="mb-3 flex items-center gap-2">
            <GitBranch className="size-[15px] shrink-0 text-muted-subtle" aria-hidden="true" />
            <Select value={env} onValueChange={setEnv}>
              <SelectTrigger className="font-mono" aria-label="Environment">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {environments.map((environment) => (
                  <SelectItem key={environment.name} value={environment.name} className="font-mono">
                    {environment.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
            {!editable && (
              <span className="rounded-full border border-border px-2 py-px text-xs font-semibold text-muted-subtle">
                read-only
              </span>
            )}
          </div>

          <div className="max-w-[640px] overflow-hidden rounded-2xl border border-border bg-card py-2.5">
            {(selected?.tree?.children ?? []).map((child) => (
              <Node
                key={`${env}/${child.path}`}
                node={child}
                env={env}
                depth={0}
                editable={editable}
                onCreate={(path) =>
                  navigate(`/jobs/${env}/new?notebook=${encodeURIComponent(path)}`)
                }
              />
            ))}
          </div>
        </>
      )}
    </div>
  );
}
