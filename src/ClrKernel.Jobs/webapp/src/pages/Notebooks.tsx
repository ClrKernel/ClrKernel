import { useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api, type TreeNode } from '../api';
import { ErrorBanner, usePolling } from '../components/common';

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
      <li className="tree-dir">
        <button className="tree-toggle" onClick={() => setOpen(!open)}>
          {open ? '▾' : '▸'} {node.name}
        </button>
        {open && children.length > 0 && (
          <ul>
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
      <li className="tree-file tree-jobsfile">
        <span className="tree-name">{node.name}</span>
        <span className="muted"> jobs file</span>
      </li>
    );
  }

  const editable = env === 'dev' && node.kind === 'notebook';
  return (
    <li className="tree-file">
      {editable ? (
        <Link className="tree-name" to={`/edit?path=${encodeURIComponent(node.path)}`}>
          {node.name}
        </Link>
      ) : (
        <span className="tree-name">{node.name}</span>
      )}
      {node.jobs?.map((job) => (
        <Link key={job} className="chip" to={`/jobs/${env}/${encodeURIComponent(job)}`}>
          {job}
        </Link>
      ))}
      {editable && (
        <Link className="button button-small" to={`/edit?path=${encodeURIComponent(node.path)}`}>
          Edit
        </Link>
      )}
      <button className="link-button" onClick={() => onCreate(node.path)}>
        + job
      </button>
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
      <h1>Notebooks</h1>
      <ErrorBanner error={error} />
      <p className="muted">
        Every <code>*.jobs.yaml</code> found here defines jobs. Pick a notebook to add one.
      </p>
      {health && !health.gitEnabled && (
        <div className="banner banner-warn">
          Editing notebooks in the browser needs the dev→prod git workflow, so every save is a
          commit. Enable it once (stop the server first):
          <pre className="output-text">clrkernel-jobs git init --notebooks &lt;your notebooks folder&gt;</pre>
          Then restart — dev notebooks get an <strong>Edit</strong> button, and changes promote to
          production after a green run.
        </div>
      )}
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
