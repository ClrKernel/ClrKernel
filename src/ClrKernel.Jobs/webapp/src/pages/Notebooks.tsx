import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, type TreeNode } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

function Node({ node, onCreate }: { node: TreeNode; onCreate: (path: string) => void }) {
  const [open, setOpen] = useState(true);

  if (node.isDirectory) {
    const children = node.children ?? [];
    return (
      <li className="tree-dir">
        <button className="tree-toggle" onClick={() => setOpen(!open)}>
          {open ? '▾' : '▸'} {node.name}
        </button>
        {open && children.length > 0 && (
          <ul>
            {children.map((child) => (
              <Node key={child.path} node={child} onCreate={onCreate} />
            ))}
          </ul>
        )}
      </li>
    );
  }

  if (node.kind === 'jobs') {
    return (
      <li className="tree-file tree-jobsfile">
        <span className="tree-name">{node.name}</span>
        <span className="muted"> jobs file</span>
      </li>
    );
  }

  return (
    <li className="tree-file">
      <span className="tree-name">{node.name}</span>
      {node.jobs?.map((job) => (
        <Link key={job} className="chip" to={`/jobs/${encodeURIComponent(job)}`}>
          {job}
        </Link>
      ))}
      <button className="link-button" onClick={() => onCreate(node.path)}>
        + job
      </button>
    </li>
  );
}

export function Notebooks() {
  const navigate = useNavigate();
  const { data, error } = usePolling(() => api.notebooks(), null);

  const environments = (data?.environments ?? []).filter((e) => e.tree != null);
  return (
    <div>
      <h1>Notebooks</h1>
      <ErrorBanner error={error} />
      <p className="muted">
        Every <code>*.jobs.yaml</code> found here defines jobs. Pick a notebook to add one.
      </p>
      {environments.length === 0 ? (
        <p className="muted">No notebooks under the notebooks root.</p>
      ) : (
        environments.map((environment) => (
          <section key={environment.name}>
            {environment.name !== 'default' && (
              <h2>
                {environment.name}
                {environment.name === 'prod' && <span className="chip chip-muted">read-only</span>}
              </h2>
            )}
            <ul className="tree">
              {(environment.tree!.children ?? []).map((child) => (
                <Node
                  key={`${environment.name}/${child.path}`}
                  node={child}
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
