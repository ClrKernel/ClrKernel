import { ChevronDown, ChevronRight, FileText, NotebookText, Plus } from 'lucide-react';
import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { Alert, AlertDescription, AlertTitle } from '@/components/ui/alert';
import { Badge } from '@/components/ui/badge';
import { Button } from '@/components/ui/button';
import { api, type TreeNode } from '../api';
import { ErrorBanner, PageHeader, usePolling } from '../components/common';

function Node({
  node,
  env,
  onCreate,
}: {
  node: TreeNode;
  env: string;
  onCreate: (path: string) => void;
}) {
  const [open, setOpen] = useState(true);

  if (node.isDirectory) {
    const children = node.children ?? [];
    return (
      <li>
        <button
          type="button"
          className="flex items-center gap-1 rounded-sm py-0.5 text-base font-medium outline-none hover:text-primary focus-visible:ring-2 focus-visible:ring-ring"
          onClick={() => setOpen(!open)}
          aria-expanded={open}
        >
          {open ? (
            <ChevronDown className="size-3.5 shrink-0" aria-hidden="true" />
          ) : (
            <ChevronRight className="size-3.5 shrink-0" aria-hidden="true" />
          )}
          {node.name}
        </button>
        {open && children.length > 0 && (
          <ul className="ml-4 border-l border-border pl-3">
            {children.map((child) => (
              <Node key={child.path} node={child} env={env} onCreate={onCreate} />
            ))}
          </ul>
        )}
      </li>
    );
  }

  if (node.kind === 'jobs') {
    return (
      <li className="flex items-center gap-2 py-0.5 text-base">
        <FileText className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
        <span className="font-mono">{node.name}</span>
        <span className="text-base text-muted-foreground">jobs file</span>
      </li>
    );
  }

  const editable = env === 'dev' && node.kind === 'notebook';
  const href = `/edit?path=${encodeURIComponent(node.path)}`;
  return (
    <li className="group flex items-center gap-2 py-0.5 text-base">
      <NotebookText className="size-3.5 shrink-0 text-muted-foreground" aria-hidden="true" />
      {editable ? (
        <Link className="font-mono text-primary hover:underline" to={href}>
          {node.name}
        </Link>
      ) : (
        <span className="font-mono">{node.name}</span>
      )}
      {node.jobs?.map((job) => (
        <Link key={job} to={`/jobs/${env}/${encodeURIComponent(job)}`}>
          <Badge variant="outline" className="font-normal hover:bg-accent">
            {job}
          </Badge>
        </Link>
      ))}
      {/* The row's actions stay out of the way until you are on the row. */}
      <span className="flex items-center gap-1 opacity-0 transition-opacity focus-within:opacity-100 group-hover:opacity-100">
        {editable && (
          <Button asChild variant="outline" size="sm" className="h-6 px-2 text-sm">
            <Link to={href}>Edit</Link>
          </Button>
        )}
        <Button
          variant="ghost"
          size="sm"
          className="h-6 px-2 text-sm"
          onClick={() => onCreate(node.path)}
        >
          <Plus className="size-3" aria-hidden="true" />
          job
        </Button>
      </span>
    </li>
  );
}

export function Notebooks() {
  const navigate = useNavigate();
  const { data, error } = usePolling(() => api.notebooks(), null);
  const { data: health } = usePolling(() => api.health(), null);

  const environments = (data?.environments ?? []).filter((e) => e.tree != null);
  return (
    <div>
      <PageHeader
        title="Notebooks"
        description={
          <>
            Every <code className="font-mono">*.jobs.yaml</code> found here defines jobs. Pick a
            notebook to add one.
          </>
        }
      />
      <ErrorBanner error={error} />

      {health && !health.gitEnabled && (
        <Alert variant="warning" className="mb-4">
          <AlertTitle>Editing needs the git workflow</AlertTitle>
          <AlertDescription>
            <p>
              Editing notebooks in the browser needs the dev→prod git workflow, so every save is a
              commit. Enable it once (stop the server first):
            </p>
            <pre className="my-2 overflow-x-auto rounded-sm bg-muted px-2 py-1.5 font-mono text-base text-foreground">
              clrkernel-jobs git init --notebooks &lt;your notebooks folder&gt;
            </pre>
            <p>
              Then restart — dev notebooks get an <strong>Edit</strong> button, and changes promote
              to production after a green run.
            </p>
          </AlertDescription>
        </Alert>
      )}

      {environments.length === 0 ? (
        <p className="text-base text-muted-foreground">No notebooks under the notebooks root.</p>
      ) : (
        environments.map((environment) => (
          <section key={environment.name} className="mb-5">
            {environment.name !== 'default' && (
              <h2 className="mb-1 flex items-center gap-2 text-base font-semibold">
                {environment.name}
                {environment.name === 'prod' && (
                  <Badge variant="secondary" className="font-normal">
                    read-only
                  </Badge>
                )}
              </h2>
            )}
            <ul className="rounded-md border border-border bg-card px-3 py-2 shadow-[var(--shadow-card)]">
              {(environment.tree!.children ?? []).map((child) => (
                <Node
                  key={`${environment.name}/${child.path}`}
                  node={child}
                  env={environment.name}
                  onCreate={(path) =>
                    navigate(`/jobs/${environment.name}/new?notebook=${encodeURIComponent(path)}`)
                  }
                />
              ))}
            </ul>
          </section>
        ))
      )}
    </div>
  );
}
