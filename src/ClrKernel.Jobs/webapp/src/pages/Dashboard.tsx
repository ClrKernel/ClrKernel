import { Link } from 'react-router-dom';
import { api, isActive, type Run, type Stats } from '../api';
import { duration, timeAgo } from '../ipynb';
import { ErrorBanner, StatusBadge, usePolling } from '../components/common';

export function RunTable({ runs, showJob = true }: { runs: Run[]; showJob?: boolean }) {
  if (runs.length === 0) {
    return <p className="muted">No runs yet.</p>;
  }
  return (
    <table className="table">
      <thead>
        <tr>
          <th>Status</th>
          {showJob && <th>Job</th>}
          <th>Trigger</th>
          <th>Started</th>
          <th>Took</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {runs.map((run) => (
          <tr key={run.id}>
            <td>
              <StatusBadge status={run.status} />
            </td>
            {showJob && (
              <td>
                <Link to={`/jobs/${encodeURIComponent(run.jobName)}`}>{run.jobName}</Link>
              </td>
            )}
            <td className="muted">{run.trigger}</td>
            <td className="muted">{timeAgo(run.startedAt ?? run.createdAt)}</td>
            <td className="muted">{duration(run.startedAt, run.finishedAt)}</td>
            <td>
              <Link to={`/runs/${run.id}`}>details</Link>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

export function Dashboard() {
  const { data, error } = usePolling<{ stats: Stats; runs: Run[] }>(
    async () => ({ stats: await api.stats(7), runs: await api.runs(25) }),
    3000,
  );

  const stats = data?.stats;
  const runs = data?.runs ?? [];
  const rate = stats && stats.total > 0 ? Math.round((stats.succeeded / stats.total) * 100) : null;
  const running = runs.filter((r) => isActive(r.status)).length;

  return (
    <div>
      <h1>Dashboard</h1>
      <ErrorBanner error={error} />

      <div className="cards">
        <div className="card">
          <div className="card-value">{stats?.total ?? '—'}</div>
          <div className="card-label">runs (7 days)</div>
        </div>
        <div className="card">
          <div className="card-value">{rate == null ? '—' : `${rate}%`}</div>
          <div className="card-label">success rate</div>
        </div>
        <div className="card">
          <div className="card-value card-failed">{stats?.failed ?? '—'}</div>
          <div className="card-label">failed</div>
        </div>
        <div className="card">
          <div className="card-value">{running}</div>
          <div className="card-label">in flight</div>
        </div>
      </div>

      <h2>Recent runs</h2>
      <RunTable runs={runs} />
    </div>
  );
}
